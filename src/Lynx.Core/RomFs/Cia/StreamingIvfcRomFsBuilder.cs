using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lynx.Core.RomFs.Cia
{
    public sealed class RomFsBuildFileSpec
    {
        public string RelativePath { get; init; } = string.Empty;
        public long Length { get; init; }
        public Action<Stream> WriteContentTo { get; init; } = _ => { };
        /// <summary>
        /// Optional relative path of another file in the same RomFS whose Level-3 <c>(DataRelativeOffset, DataLength)</c>
        /// slice this entry should alias without writing duplicate physical bytes.
        /// </summary>
        public string AliasOfRelativePath { get; init; }
    }

    public sealed class IvfcBuildResult
    {
        public long TotalIvfcSizeBytes { get; init; }
        public uint HashRegionSizeMediaUnits { get; init; }
        public byte[] SuperblockSha256 { get; init; } = Array.Empty<byte>();
        public int DirectoryCount { get; init; }
        public int FileCount { get; init; }
    }

    /// <summary>
    /// Single-pass streaming builder for Nintendo 3DS IVFC + Level-3 RomFS partitions.
    /// Writes directly to the target output stream with 4 KB block hashing and &lt; 20 MB RAM even on 2+ GB games.
    /// </summary>
    public static class StreamingIvfcRomFsBuilder
    {
        private sealed class DirNode
        {
            public string Name { get; init; } = string.Empty;
            public string FullRelativePath { get; init; } = string.Empty;
            public DirNode Parent { get; set; }
            public List<DirNode> ChildDirs { get; } = new();
            public List<FileNode> ChildFiles { get; } = new();

            public uint MetaByteOffset { get; set; }
            public uint ParentMetaOffset => Parent?.MetaByteOffset ?? 0u;
            public uint SiblingMetaOffset { get; set; } = IvfcRomFsReader.UnusedOffset;
            public uint FirstChildDirOffset { get; set; } = IvfcRomFsReader.UnusedOffset;
            public uint FirstFileOffset { get; set; } = IvfcRomFsReader.UnusedOffset;
            public uint NextHashBucketOffset { get; set; } = IvfcRomFsReader.UnusedOffset;
            public byte[] NameUtf16Bytes { get; init; } = Array.Empty<byte>();
        }

        private sealed class FileNode
        {
            public string Name { get; init; } = string.Empty;
            public string FullRelativePath { get; init; } = string.Empty;
            public DirNode Parent { get; init; }
            public RomFsBuildFileSpec Spec { get; init; }
            public FileNode AliasTarget { get; set; }

            public uint MetaByteOffset { get; set; }
            public uint SiblingMetaOffset { get; set; } = IvfcRomFsReader.UnusedOffset;
            public ulong DataRelativeOffset { get; set; }
            public ulong DataLength => AliasTarget != null ? AliasTarget.DataLength : (ulong)Spec.Length;
            public uint NextHashBucketOffset { get; set; } = IvfcRomFsReader.UnusedOffset;
            public byte[] NameUtf16Bytes { get; init; } = Array.Empty<byte>();
        }

        public static IvfcBuildResult BuildToStream(
            Stream outputStream,
            long outputStartOffset,
            IEnumerable<string> explicitDirectories,
            IEnumerable<RomFsBuildFileSpec> fileSpecs,
            IProgress<int> progress = null)
        {
            if (outputStream == null)
                throw new ArgumentNullException(nameof(outputStream));
            if (!outputStream.CanWrite || !outputStream.CanSeek)
                throw new ArgumentException("Output stream must be writable and seekable.", nameof(outputStream));

            // 1. Construct deterministic directory/file tree (preserving case of names, deduplicating case-insensitively)
            var root = new DirNode { Name = string.Empty, FullRelativePath = string.Empty };
            var dirLookup = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = root
            };

            DirNode EnsureDir(string relDir)
            {
                string norm = NormalizeRelPath(relDir);
                if (dirLookup.TryGetValue(norm, out var existing))
                    return existing;

                int slash = norm.LastIndexOf('/');
                string parentRel = slash >= 0 ? norm.Substring(0, slash) : string.Empty;
                string name = slash >= 0 ? norm.Substring(slash + 1) : norm;

                DirNode parentNode = EnsureDir(parentRel);
                var created = new DirNode
                {
                    Name = name,
                    FullRelativePath = string.IsNullOrEmpty(parentNode.FullRelativePath)
                        ? name
                        : parentNode.FullRelativePath + "/" + name,
                    Parent = parentNode,
                    NameUtf16Bytes = Encoding.Unicode.GetBytes(name)
                };
                parentNode.ChildDirs.Add(created);
                dirLookup[norm] = created;
                return created;
            }

            if (explicitDirectories != null)
            {
                foreach (string d in explicitDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(d))
                        EnsureDir(d);
                }
            }

            var dedupFiles = new Dictionary<string, RomFsBuildFileSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in fileSpecs ?? Enumerable.Empty<RomFsBuildFileSpec>())
            {
                string norm = NormalizeRelPath(spec.RelativePath);
                if (string.IsNullOrEmpty(norm))
                    continue;
                string normAlias = string.IsNullOrWhiteSpace(spec.AliasOfRelativePath)
                    ? null
                    : NormalizeRelPath(spec.AliasOfRelativePath);
                dedupFiles[norm] = new RomFsBuildFileSpec
                {
                    RelativePath = norm,
                    Length = spec.Length,
                    WriteContentTo = spec.WriteContentTo,
                    AliasOfRelativePath = normAlias
                };
            }

            foreach (var spec in dedupFiles.Values)
            {
                int slash = spec.RelativePath.LastIndexOf('/');
                string parentRel = slash >= 0 ? spec.RelativePath.Substring(0, slash) : string.Empty;
                string fileName = slash >= 0 ? spec.RelativePath.Substring(slash + 1) : spec.RelativePath;

                DirNode parentDir = EnsureDir(parentRel);
                parentDir.ChildFiles.Add(new FileNode
                {
                    Name = fileName,
                    FullRelativePath = spec.RelativePath,
                    Parent = parentDir,
                    Spec = spec,
                    NameUtf16Bytes = Encoding.Unicode.GetBytes(fileName)
                });
            }

            // 2. Flatten directories & files in deterministic order and assign metadata byte offsets
            var allDirs = new List<DirNode>();
            var allFiles = new List<FileNode>();

            void Traverse(DirNode node)
            {
                node.ChildDirs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                node.ChildFiles.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                allDirs.Add(node);
                foreach (var f in node.ChildFiles)
                    allFiles.Add(f);
                foreach (var c in node.ChildDirs)
                    Traverse(c);
            }
            Traverse(root);

            uint dirMetaCursor = 0;
            foreach (var d in allDirs)
            {
                d.MetaByteOffset = dirMetaCursor;
                dirMetaCursor += 0x18u + (uint)CiaConstants.AlignUp(d.NameUtf16Bytes.Length, 4);
            }

            var fileNodeByPath = new Dictionary<string, FileNode>(allFiles.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in allFiles)
            {
                fileNodeByPath[f.FullRelativePath] = f;
            }

            foreach (var f in allFiles)
            {
                if (!string.IsNullOrEmpty(f.Spec.AliasOfRelativePath) &&
                    fileNodeByPath.TryGetValue(f.Spec.AliasOfRelativePath, out var targetNode) &&
                    !ReferenceEquals(targetNode, f) &&
                    string.IsNullOrEmpty(targetNode.Spec.AliasOfRelativePath))
                {
                    f.AliasTarget = targetNode;
                }
            }

            uint fileMetaCursor = 0;
            long fileDataCursor = 0;
            foreach (var f in allFiles)
            {
                f.MetaByteOffset = fileMetaCursor;
                fileMetaCursor += 0x20u + (uint)CiaConstants.AlignUp(f.NameUtf16Bytes.Length, 4);

                if (f.AliasTarget != null)
                {
                    continue;
                }

                if (f.Spec.Length > 0)
                {
                    fileDataCursor = CiaConstants.AlignUp(fileDataCursor, CiaConstants.RomFsFileDataAlignment);
                    f.DataRelativeOffset = (ulong)fileDataCursor;
                    fileDataCursor += f.Spec.Length;
                }
                else
                {
                    // Per Nintendo makerom (romfs_gen.c:276), empty files store dataoffset = 0 without advancing u_dataLen
                    f.DataRelativeOffset = 0;
                }
            }

            foreach (var f in allFiles)
            {
                if (f.AliasTarget != null)
                {
                    f.DataRelativeOffset = f.AliasTarget.DataRelativeOffset;
                }
            }

            // Link sibling / first-child pointers
            foreach (var d in allDirs)
            {
                if (d.ChildDirs.Count > 0)
                {
                    d.FirstChildDirOffset = d.ChildDirs[0].MetaByteOffset;
                    for (int i = 0; i + 1 < d.ChildDirs.Count; i++)
                        d.ChildDirs[i].SiblingMetaOffset = d.ChildDirs[i + 1].MetaByteOffset;
                }

                if (d.ChildFiles.Count > 0)
                {
                    d.FirstFileOffset = d.ChildFiles[0].MetaByteOffset;
                    for (int i = 0; i + 1 < d.ChildFiles.Count; i++)
                        d.ChildFiles[i].SiblingMetaOffset = d.ChildFiles[i + 1].MetaByteOffset;
                }
            }

            // 3. Populate kernel hash-bucket tables
            uint dirBucketCount = IvfcRomFsReader.ComputeBucketCount((uint)allDirs.Count);
            uint[] dirBuckets = new uint[dirBucketCount];
            Array.Fill(dirBuckets, IvfcRomFsReader.UnusedOffset);

            foreach (var d in allDirs)
            {
                uint bucket = IvfcRomFsReader.CalcPathHash(d.ParentMetaOffset, d.Name) % dirBucketCount;
                d.NextHashBucketOffset = dirBuckets[bucket];
                dirBuckets[bucket] = d.MetaByteOffset;
            }

            uint fileBucketCount = IvfcRomFsReader.ComputeBucketCount((uint)allFiles.Count);
            uint[] fileBuckets = new uint[fileBucketCount];
            Array.Fill(fileBuckets, IvfcRomFsReader.UnusedOffset);

            foreach (var f in allFiles)
            {
                uint bucket = IvfcRomFsReader.CalcPathHash(f.Parent.MetaByteOffset, f.Name) % fileBucketCount;
                f.NextHashBucketOffset = fileBuckets[bucket];
                fileBuckets[bucket] = f.MetaByteOffset;
            }

            // 4. Compute Level 3 header & IVFC physical/logical geometry
            uint dirHashOff = CiaConstants.RomFsLevel3HeaderSize;
            uint dirHashLen = dirBucketCount * 4u;
            uint dirMetaOff = (uint)CiaConstants.AlignUp(dirHashOff + dirHashLen, 4);
            uint dirMetaLen = dirMetaCursor;
            uint fileHashOff = (uint)CiaConstants.AlignUp(dirMetaOff + dirMetaLen, 4);
            uint fileHashLen = fileBucketCount * 4u;
            uint fileMetaOff = (uint)CiaConstants.AlignUp(fileHashOff + fileHashLen, 4);
            uint fileMetaLen = fileMetaCursor;
            uint fileDataOff = (uint)CiaConstants.AlignUp(fileMetaOff + fileMetaLen, CiaConstants.RomFsFileDataAlignment);

            long level3RawSize = fileDataOff + fileDataCursor;
            long level3AlignedSize = Math.Max(CiaConstants.IvfcBlockSize, CiaConstants.AlignUp(level3RawSize, CiaConstants.IvfcBlockSize));
            long level3BlockCount = level3AlignedSize / CiaConstants.IvfcBlockSize;

            long level2RawSize = level3BlockCount * 32L;
            long level2AlignedSize = Math.Max(CiaConstants.IvfcBlockSize, CiaConstants.AlignUp(level2RawSize, CiaConstants.IvfcBlockSize));
            long level2BlockCount = level2AlignedSize / CiaConstants.IvfcBlockSize;

            long level1RawSize = level2BlockCount * 32L;
            long level1AlignedSize = Math.Max(CiaConstants.IvfcBlockSize, CiaConstants.AlignUp(level1RawSize, CiaConstants.IvfcBlockSize));
            long level1BlockCount = level1AlignedSize / CiaConstants.IvfcBlockSize;

            uint masterHashSize = (uint)(level1BlockCount * 32L);
            long level0RawSize = CiaConstants.IvfcPaddedHeaderSize + masterHashSize;
            long level3PhysOffset = CiaConstants.AlignUp(level0RawSize, CiaConstants.IvfcBlockSize);
            long level1PhysOffset = level3PhysOffset + level3AlignedSize;
            long level2PhysOffset = level1PhysOffset + level1AlignedSize;
            long totalIvfcSize = level2PhysOffset + level2AlignedSize;

            // Serialize Level 3 metadata prefix (typically a few KB)
            byte[] level3Prefix = new byte[fileDataOff];
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x00, 4), CiaConstants.RomFsLevel3HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x04, 4), dirHashOff);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x08, 4), dirHashLen);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x0C, 4), dirMetaOff);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x10, 4), dirMetaLen);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x14, 4), fileHashOff);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x18, 4), fileHashLen);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x1C, 4), fileMetaOff);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x20, 4), fileMetaLen);
            BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(0x24, 4), fileDataOff);

            for (int i = 0; i < dirBuckets.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan((int)dirHashOff + i * 4, 4), dirBuckets[i]);

            foreach (var d in allDirs)
            {
                int p = (int)(dirMetaOff + d.MetaByteOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x00, 4), d.ParentMetaOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x04, 4), d.SiblingMetaOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x08, 4), d.FirstChildDirOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x0C, 4), d.FirstFileOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x10, 4), d.NextHashBucketOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x14, 4), (uint)d.NameUtf16Bytes.Length);
                if (d.NameUtf16Bytes.Length > 0)
                    Buffer.BlockCopy(d.NameUtf16Bytes, 0, level3Prefix, p + 0x18, d.NameUtf16Bytes.Length);
            }

            for (int i = 0; i < fileBuckets.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan((int)fileHashOff + i * 4, 4), fileBuckets[i]);

            foreach (var f in allFiles)
            {
                int p = (int)(fileMetaOff + f.MetaByteOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x00, 4), f.Parent.MetaByteOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x04, 4), f.SiblingMetaOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(level3Prefix.AsSpan(p + 0x08, 8), f.DataRelativeOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(level3Prefix.AsSpan(p + 0x10, 8), f.DataLength);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x18, 4), f.NextHashBucketOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(level3Prefix.AsSpan(p + 0x1C, 4), (uint)f.NameUtf16Bytes.Length);
                if (f.NameUtf16Bytes.Length > 0)
                    Buffer.BlockCopy(f.NameUtf16Bytes, 0, level3Prefix, p + 0x20, f.NameUtf16Bytes.Length);
            }

            // 5. Stream Level 3 straight to outputStream while computing Level 2 SHA-256 hashes per 4 KB block
            byte[] level2Buffer = new byte[level2AlignedSize];
            outputStream.Seek(outputStartOffset + level3PhysOffset, SeekOrigin.Begin);

            using (var l3Hasher = new BlockHashingPassthroughStream(outputStream, level2Buffer))
            {
                l3Hasher.Write(level3Prefix, 0, level3Prefix.Length);

                Span<byte> zeroPad16 = stackalloc byte[16];
                zeroPad16.Clear();

                long currentDataWritten = 0;
                long totalDataTarget = Math.Max(1L, fileDataCursor);
                int lastPct = -1;

                foreach (var f in allFiles)
                {
                    if (f.AliasTarget != null || f.Spec.Length == 0)
                        continue;

                    long padNeeded = (long)f.DataRelativeOffset - currentDataWritten;
                    while (padNeeded > 0)
                    {
                        int chunk = (int)Math.Min(16, padNeeded);
                        l3Hasher.Write(zeroPad16.Slice(0, chunk));
                        currentDataWritten += chunk;
                        padNeeded -= chunk;
                    }

                    long before = l3Hasher.TotalWritten;
                    f.Spec.WriteContentTo(l3Hasher);
                    long actualFileWritten = l3Hasher.TotalWritten - before;
                    if (actualFileWritten != f.Spec.Length)
                    {
                        throw new InvalidDataException(
                            $"Longitud real escrita para '{f.FullRelativePath}' ({actualFileWritten} B) no coincide con la declarada ({f.Spec.Length} B).");
                    }

                    currentDataWritten += actualFileWritten;
                    if (progress != null)
                    {
                        int pct = (int)((currentDataWritten * 100L) / totalDataTarget);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress.Report(pct);
                        }
                    }
                }

                // Pad Level 3 to 0x1000 boundary
                long l3TailPad = level3AlignedSize - l3Hasher.TotalWritten;
                if (l3TailPad < 0)
                    throw new InvalidDataException("Desbordamiento interno al escribir Level 3 RomFS.");
                if (l3TailPad > 0)
                    l3Hasher.WriteZeros(l3TailPad);

                if (l3Hasher.HashedBlockCount != level3BlockCount)
                    throw new InvalidDataException("Recuento de bloques SHA-256 de Level 3 inconsistente.");
            }

            // 6. Compute Level 1 from Level 2, and Level 0 Master Hashes from Level 1
            byte[] level1Buffer = new byte[level1AlignedSize];
            for (long b = 0; b < level2BlockCount; b++)
            {
                SHA256.HashData(
                    level2Buffer.AsSpan((int)(b * 0x1000), 0x1000),
                    level1Buffer.AsSpan((int)(b * 32), 32));
            }

            byte[] level0PaddedBlock = new byte[level3PhysOffset];
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x00, 4), CiaConstants.IvfcMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x04, 4), CiaConstants.IvfcRomFsId);
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x08, 4), masterHashSize);

            ulong l1Logical = 0;
            ulong l2Logical = (ulong)level1AlignedSize;
            ulong l3Logical = l2Logical + (ulong)level2AlignedSize;

            // Level 1 descriptor
            BinaryPrimitives.WriteUInt64LittleEndian(level0PaddedBlock.AsSpan(0x0C, 8), l1Logical);
            BinaryPrimitives.WriteUInt64LittleEndian(level0PaddedBlock.AsSpan(0x14, 8), (ulong)level1RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x1C, 4), CiaConstants.IvfcBlockSizeLog2);

            // Level 2 descriptor
            BinaryPrimitives.WriteUInt64LittleEndian(level0PaddedBlock.AsSpan(0x24, 8), l2Logical);
            BinaryPrimitives.WriteUInt64LittleEndian(level0PaddedBlock.AsSpan(0x2C, 8), (ulong)level2RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x34, 4), CiaConstants.IvfcBlockSizeLog2);

            // Level 3 descriptor
            BinaryPrimitives.WriteUInt64LittleEndian(level0PaddedBlock.AsSpan(0x3C, 8), l3Logical);
            BinaryPrimitives.WriteUInt64LittleEndian(level0PaddedBlock.AsSpan(0x44, 8), (ulong)level3RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x4C, 4), CiaConstants.IvfcBlockSizeLog2);

            // Optional header size = 0x5C
            BinaryPrimitives.WriteUInt32LittleEndian(level0PaddedBlock.AsSpan(0x54, 4), CiaConstants.IvfcHeaderSize);

            for (long b = 0; b < level1BlockCount; b++)
            {
                SHA256.HashData(
                    level1Buffer.AsSpan((int)(b * 0x1000), 0x1000),
                    level0PaddedBlock.AsSpan((int)(CiaConstants.IvfcPaddedHeaderSize + b * 32), 32));
            }

            // Write Level 1 & Level 2 after Level 3, then write Level 0 at the front
            outputStream.Seek(outputStartOffset + level1PhysOffset, SeekOrigin.Begin);
            outputStream.Write(level1Buffer, 0, level1Buffer.Length);
            outputStream.Write(level2Buffer, 0, level2Buffer.Length);

            outputStream.Seek(outputStartOffset, SeekOrigin.Begin);
            outputStream.Write(level0PaddedBlock, 0, level0PaddedBlock.Length);

            long hashRegionBytes = CiaConstants.AlignUp(level0RawSize, CiaConstants.NcchMediaUnitSize);
            byte[] superblockHash = SHA256.HashData(level0PaddedBlock.AsSpan(0, (int)hashRegionBytes));

            return new IvfcBuildResult
            {
                TotalIvfcSizeBytes = totalIvfcSize,
                HashRegionSizeMediaUnits = (uint)(hashRegionBytes / CiaConstants.NcchMediaUnitSize),
                SuperblockSha256 = superblockHash,
                DirectoryCount = allDirs.Count,
                FileCount = allFiles.Count
            };
        }

        public static string NormalizeRelPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;
            string p = raw.Replace('\\', '/').Trim().Trim('/');
            while (p.Contains("//", StringComparison.Ordinal))
                p = p.Replace("//", "/", StringComparison.Ordinal);
            return p;
        }

        private sealed class BlockHashingPassthroughStream : Stream
        {
            private readonly Stream _target;
            private readonly byte[] _hashDest;
            private readonly byte[] _blockBuf = new byte[0x1000];
            private int _inBlockPos;

            public long TotalWritten { get; private set; }
            public long HashedBlockCount { get; private set; }

            public BlockHashingPassthroughStream(Stream target, byte[] hashDest)
            {
                _target = target;
                _hashDest = hashDest;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => TotalWritten;
            public override long Position { get => TotalWritten; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Write(buffer.AsSpan(offset, count));
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                int pos = 0;
                while (pos < buffer.Length)
                {
                    if (_inBlockPos == 0 && (buffer.Length - pos) >= 0x1000)
                    {
                        ReadOnlySpan<byte> fullBlock = buffer.Slice(pos, 0x1000);
                        _target.Write(fullBlock);
                        SHA256.HashData(fullBlock, _hashDest.AsSpan((int)(HashedBlockCount * 32), 32));
                        HashedBlockCount++;
                        TotalWritten += 0x1000;
                        pos += 0x1000;
                    }
                    else
                    {
                        int take = Math.Min(0x1000 - _inBlockPos, buffer.Length - pos);
                        buffer.Slice(pos, take).CopyTo(_blockBuf.AsSpan(_inBlockPos, take));
                        _inBlockPos += take;
                        TotalWritten += take;
                        pos += take;

                        if (_inBlockPos == 0x1000)
                        {
                            _target.Write(_blockBuf, 0, 0x1000);
                            SHA256.HashData(_blockBuf, _hashDest.AsSpan((int)(HashedBlockCount * 32), 32));
                            HashedBlockCount++;
                            _inBlockPos = 0;
                        }
                    }
                }
            }

            public void WriteZeros(long count)
            {
                Span<byte> zeros = stackalloc byte[4096];
                zeros.Clear();
                long rem = count;
                while (rem > 0)
                {
                    int take = (int)Math.Min(zeros.Length, rem);
                    Write(zeros.Slice(0, take));
                    rem -= take;
                }
            }

            public override void Flush() => _target.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
