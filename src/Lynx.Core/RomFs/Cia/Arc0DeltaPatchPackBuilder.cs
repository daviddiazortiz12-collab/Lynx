using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Tools;

namespace Lynx.Core.RomFs.Cia
{
    public sealed class Arc0DeltaPatchPackResult
    {
        public string OutputPackPath { get; init; } = string.Empty;
        public long PackSizeBytes { get; init; }
        public long TargetArc0SizeBytes { get; init; }
        public int TotalFileEntries { get; init; }
        public int ReusedInstalledFiles { get; init; }
        public int EmbeddedPatchFiles { get; init; }
        public long ReusedInstalledBytes { get; init; }
        public long EmbeddedUncompressedBytes { get; init; }
        public long EmbeddedStoredBytes { get; init; }
    }

    /// <summary>
    /// Builds and verifies compact standalone 2DS update packs (<c>.ie4p</c>) that reconstruct
    /// a target <c>ie_a.fa</c> directly on a Nintendo 2DS/3DS from the user's already-installed
    /// <c>0004000000112F00</c> title (whether vanilla <c>v0.0.0</c> or a prior randomized install
    /// such as <c>v1.2.2</c>) into <c>sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa</c>.
    /// </summary>
    public static class Arc0DeltaPatchPackBuilder
    {
        public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("IE4PV100");
        public const byte OpCopyFromInstalled = 0;
        public const byte OpEmbeddedRaw = 1;
        public const byte OpEmbeddedLz4 = 2;

        private sealed class IndexedArc0Entry
        {
            public int FileIndex { get; init; }
            public string VirtualPath { get; init; } = string.Empty;
            public uint FileOffsetInData { get; init; }
            public uint FileSize { get; init; }
            public long AbsoluteOffset { get; init; }
        }

        private sealed class ParsedArc0Index
        {
            public ARC0Support.Header Header { get; init; }
            public long FileLength { get; init; }
            public byte[] PrefixBytes { get; init; } = Array.Empty<byte>();
            public IndexedArc0Entry[] EntriesByIndex { get; init; } = Array.Empty<IndexedArc0Entry>();
        }

        /// <summary>
        /// Returns true if ANY version of the randomizer (<c>v1.0.x .. v1.2.x</c>) could ever mutate
        /// this virtual path inside <c>ie_a.fa</c>. When <paramref name="assumePriorRandomizedInstall"/>
        /// is true (default), these paths are always embedded in the delta pack even if unchanged in
        /// the current seed/options so the 2DS updater works cleanly over an existing <c>v1.2.2</c> install.
        /// </summary>
        public static bool CouldBeModifiedByPriorRandomizerVersion(string virtualPath)
        {
            if (string.IsNullOrEmpty(virtualPath))
                return true;

            string norm = virtualPath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

            if (norm.StartsWith("data/res/", StringComparison.Ordinal) ||
                norm.StartsWith("data/bustup/face/", StringComparison.Ordinal) ||
                norm.StartsWith("data/chr/model/rpg/face/", StringComparison.Ordinal) ||
                norm.StartsWith("data/chr/model/waza/face/", StringComparison.Ordinal) ||
                norm.StartsWith("data/script/", StringComparison.Ordinal))
            {
                return true;
            }

            if (norm.StartsWith("data/map/", StringComparison.Ordinal) &&
                (norm.EndsWith(".npc.bin", StringComparison.Ordinal) ||
                 norm.EndsWith(".tre.bin", StringComparison.Ordinal) ||
                 norm.EndsWith(".talk.bin", StringComparison.Ordinal) ||
                 norm.EndsWith(".enc.bin", StringComparison.Ordinal)))
            {
                return true;
            }

            return false;
        }

        public static Arc0DeltaPatchPackResult BuildPack(
            string cleanRetailArc0Path,
            string targetPatchedArc0Path,
            string outputPackPath,
            bool assumePriorRandomizedInstall = true)
        {
            if (string.IsNullOrWhiteSpace(cleanRetailArc0Path) || !File.Exists(cleanRetailArc0Path))
                throw new FileNotFoundException("Clean retail ie_a.fa not found.", cleanRetailArc0Path);
            if (string.IsNullOrWhiteSpace(targetPatchedArc0Path) || !File.Exists(targetPatchedArc0Path))
                throw new FileNotFoundException("Target patched ie_a.fa not found.", targetPatchedArc0Path);
            if (string.IsNullOrWhiteSpace(outputPackPath))
                throw new ArgumentException("Output pack path cannot be empty.", nameof(outputPackPath));

            string parentDir = Path.GetDirectoryName(Path.GetFullPath(outputPackPath));
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            using var retailFs = new FileStream(cleanRetailArc0Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var targetFs = new FileStream(targetPatchedArc0Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

            ParsedArc0Index retailIdx = ParseArc0Index(retailFs);
            ParsedArc0Index targetIdx = ParseArc0Index(targetFs);

            if (retailIdx.EntriesByIndex.Length != targetIdx.EntriesByIndex.Length)
            {
                throw new InvalidDataException(
                    $"ARC0 FileEntriesCount mismatch between retail ({retailIdx.EntriesByIndex.Length}) " +
                    $"and patched ({targetIdx.EntriesByIndex.Length}) archives.");
            }

            // Order target entries strictly by physical layout offset inside target ie_a.fa
            IndexedArc0Entry[] orderedTarget = targetIdx.EntriesByIndex
                .OrderBy(e => e.FileOffsetInData)
                .ThenBy(e => e.FileIndex)
                .ToArray();

            // Verify physical contiguity (each entry + zero padding reaches the next entry / EOF)
            long cursor = targetIdx.Header.DataOffset;
            uint[] postPadding = new uint[orderedTarget.Length];
            for (int i = 0; i < orderedTarget.Length; i++)
            {
                var entry = orderedTarget[i];
                if (entry.AbsoluteOffset != cursor)
                {
                    throw new InvalidDataException(
                        $"Unexpected gap before entry #{entry.FileIndex} ('{entry.VirtualPath}'): " +
                        $"cursor=0x{cursor:X}, entryOffset=0x{entry.AbsoluteOffset:X}.");
                }

                long afterData = entry.AbsoluteOffset + entry.FileSize;
                long nextBoundary = (i + 1 < orderedTarget.Length)
                    ? orderedTarget[i + 1].AbsoluteOffset
                    : targetIdx.FileLength;

                if (nextBoundary < afterData)
                {
                    throw new InvalidDataException(
                        $"Overlapping ARC0 entry #{entry.FileIndex} ('{entry.VirtualPath}'): " +
                        $"end=0x{afterData:X} > next=0x{nextBoundary:X}.");
                }

                // Verify padding bytes in target are strictly 0x00
                long padLen = nextBoundary - afterData;
                if (padLen > 0)
                {
                    targetFs.Seek(afterData, SeekOrigin.Begin);
                    byte[] padBuf = new byte[padLen];
                    ReadExact(targetFs, padBuf, 0, (int)padLen);
                    for (int p = 0; p < padBuf.Length; p++)
                    {
                        if (padBuf[p] != 0)
                        {
                            throw new InvalidDataException(
                                $"Non-zero padding byte 0x{padBuf[p]:X2} after entry #{entry.FileIndex}.");
                        }
                    }
                }

                postPadding[i] = checked((uint)padLen);
                cursor = nextBoundary;
            }

            uint prefixCrc32 = ComputeCrc32(targetIdx.PrefixBytes);
            byte[] compressedPrefix = CompressLz4Block(targetIdx.PrefixBytes);
            bool prefixUsesLz4 = compressedPrefix.Length < targetIdx.PrefixBytes.Length;
            byte[] storedPrefix = prefixUsesLz4 ? compressedPrefix : targetIdx.PrefixBytes;

            int reusedCount = 0;
            int embeddedCount = 0;
            long reusedBytes = 0;
            long embeddedRawBytes = 0;
            long embeddedStoredBytes = 0;

            string tempOut = outputPackPath + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var outFs = new FileStream(tempOut, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (var bw = new BinaryWriter(outFs, Encoding.UTF8, leaveOpen: true))
                {
                    // Pack Header (48 bytes):
                    // [0x00..0x08] Magic "IE4PV100"
                    // [0x08..0x10] uint64 TargetArc0SizeBytes
                    // [0x10..0x14] uint32 TotalFileEntries (FileEntriesCount)
                    // [0x14..0x18] uint32 OperationCount (orderedTarget.Length)
                    // [0x18..0x1C] uint32 PrefixUncompressedLength (DataOffset)
                    // [0x1C..0x20] uint32 PrefixStoredLength
                    // [0x20..0x24] uint32 PrefixCrc32
                    // [0x24]       byte   PrefixCodec (1 = Raw, 2 = LZ4)
                    // [0x25..0x28] reserved (0)
                    // [0x28..0x30] reserved (0)
                    bw.Write(MagicBytes);
                    bw.Write((ulong)targetIdx.FileLength);
                    bw.Write((uint)targetIdx.EntriesByIndex.Length);
                    bw.Write((uint)orderedTarget.Length);
                    bw.Write((uint)targetIdx.PrefixBytes.Length);
                    bw.Write((uint)storedPrefix.Length);
                    bw.Write(prefixCrc32);
                    bw.Write(prefixUsesLz4 ? OpEmbeddedLz4 : OpEmbeddedRaw);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write(0UL);

                    // Prefix payload
                    bw.Write(storedPrefix);

                    // Operations stream:
                    // Each operation record header (24 bytes):
                    // [0x00]       byte   OpType (0 = CopyFromInstalled, 1 = EmbeddedRaw, 2 = EmbeddedLz4)
                    // [0x01..0x04] reserved (0)
                    // [0x04..0x08] uint32 FileIndex (index into ARC0 FileEntries table)
                    // [0x08..0x0C] uint32 UncompressedLength
                    // [0x0C..0x10] uint32 StoredLength (0 when OpCopyFromInstalled)
                    // [0x10..0x14] uint32 PostPaddingZeroBytes
                    // [0x14..0x18] uint32 ExpectedCrc32
                    // Followed immediately by StoredLength bytes of payload.
                    for (int i = 0; i < orderedTarget.Length; i++)
                    {
                        var targetEntry = orderedTarget[i];
                        var retailEntry = retailIdx.EntriesByIndex[targetEntry.FileIndex];

                        byte[] targetData = ReadSlice(targetFs, targetEntry.AbsoluteOffset, checked((int)targetEntry.FileSize));
                        uint targetCrc = ComputeCrc32(targetData);

                        bool canReuseFromInstalled = false;
                        if (!(assumePriorRandomizedInstall && CouldBeModifiedByPriorRandomizerVersion(targetEntry.VirtualPath)) &&
                            retailEntry.FileSize == targetEntry.FileSize)
                        {
                            byte[] retailData = ReadSlice(retailFs, retailEntry.AbsoluteOffset, checked((int)retailEntry.FileSize));
                            if (retailData.AsSpan().SequenceEqual(targetData))
                            {
                                canReuseFromInstalled = true;
                            }
                        }

                        if (canReuseFromInstalled)
                        {
                            bw.Write(OpCopyFromInstalled);
                            bw.Write((byte)0);
                            bw.Write((byte)0);
                            bw.Write((byte)0);
                            bw.Write((uint)targetEntry.FileIndex);
                            bw.Write(targetEntry.FileSize);
                            bw.Write(0U);
                            bw.Write(postPadding[i]);
                            bw.Write(targetCrc);

                            reusedCount++;
                            reusedBytes += targetEntry.FileSize;
                        }
                        else
                        {
                            byte[] lz4 = targetData.Length >= 32
                                ? CompressLz4Block(targetData)
                                : targetData;
                            bool useLz4 = targetData.Length >= 32 && lz4.Length < targetData.Length;
                            byte[] stored = useLz4 ? lz4 : targetData;

                            bw.Write(useLz4 ? OpEmbeddedLz4 : OpEmbeddedRaw);
                            bw.Write((byte)0);
                            bw.Write((byte)0);
                            bw.Write((byte)0);
                            bw.Write((uint)targetEntry.FileIndex);
                            bw.Write(targetEntry.FileSize);
                            bw.Write((uint)stored.Length);
                            bw.Write(postPadding[i]);
                            bw.Write(targetCrc);
                            bw.Write(stored);

                            embeddedCount++;
                            embeddedRawBytes += targetEntry.FileSize;
                            embeddedStoredBytes += stored.Length;
                        }
                    }

                    bw.Flush();
                }

                if (File.Exists(outputPackPath))
                    File.Delete(outputPackPath);
                File.Move(tempOut, outputPackPath);
            }
            finally
            {
                if (File.Exists(tempOut))
                {
                    try { File.Delete(tempOut); } catch { }
                }
            }

            return new Arc0DeltaPatchPackResult
            {
                OutputPackPath = Path.GetFullPath(outputPackPath),
                PackSizeBytes = new FileInfo(outputPackPath).Length,
                TargetArc0SizeBytes = targetIdx.FileLength,
                TotalFileEntries = targetIdx.EntriesByIndex.Length,
                ReusedInstalledFiles = reusedCount,
                EmbeddedPatchFiles = embeddedCount,
                ReusedInstalledBytes = reusedBytes,
                EmbeddedUncompressedBytes = embeddedRawBytes,
                EmbeddedStoredBytes = embeddedStoredBytes
            };
        }

        /// <summary>
        /// Reference C# reconstruction matching the ARM11 2DS C engine (<c>ie4p_core.c</c>)
        /// for cold verification in CI and unit tests.
        /// </summary>
        public static void ApplyPackToStream(string installedArc0Path, string packPath, string outputArc0Path)
        {
            using var installedFs = new FileStream(installedArc0Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            ParsedArc0Index installedIdx = ParseArc0Index(installedFs);

            using var packFs = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var br = new BinaryReader(packFs, Encoding.UTF8, leaveOpen: true);

            byte[] magic = br.ReadBytes(8);
            if (!magic.AsSpan().SequenceEqual(MagicBytes))
                throw new InvalidDataException("Invalid IE4P magic header.");

            ulong expectedSize = br.ReadUInt64();
            uint totalEntries = br.ReadUInt32();
            uint opCount = br.ReadUInt32();
            uint prefixRawLen = br.ReadUInt32();
            uint prefixStoredLen = br.ReadUInt32();
            uint prefixCrc = br.ReadUInt32();
            byte prefixCodec = br.ReadByte();
            br.ReadBytes(11); // reserved

            if (totalEntries != (uint)installedIdx.EntriesByIndex.Length)
            {
                throw new InvalidDataException(
                    $"Installed ie_a.fa has {installedIdx.EntriesByIndex.Length} entries, expected {totalEntries}.");
            }

            byte[] prefixStored = br.ReadBytes(checked((int)prefixStoredLen));
            byte[] prefixRaw = prefixCodec == OpEmbeddedLz4
                ? DecompressLz4Block(prefixStored, checked((int)prefixRawLen))
                : prefixStored;

            if (ComputeCrc32(prefixRaw) != prefixCrc)
                throw new InvalidDataException("Prefix CRC32 verification failed.");

            using var outFs = new FileStream(outputArc0Path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            outFs.Write(prefixRaw, 0, prefixRaw.Length);

            byte[] zeroPad = new byte[4096];
            for (uint op = 0; op < opCount; op++)
            {
                byte opType = br.ReadByte();
                br.ReadBytes(3);
                uint fileIndex = br.ReadUInt32();
                uint rawLen = br.ReadUInt32();
                uint storedLen = br.ReadUInt32();
                uint padLen = br.ReadUInt32();
                uint expectedCrc = br.ReadUInt32();

                byte[] payload;
                if (opType == OpCopyFromInstalled)
                {
                    var instEntry = installedIdx.EntriesByIndex[fileIndex];
                    if (instEntry.FileSize != rawLen)
                    {
                        throw new InvalidDataException(
                            $"Installed entry #{fileIndex} size mismatch ({instEntry.FileSize} != {rawLen}).");
                    }
                    payload = ReadSlice(installedFs, instEntry.AbsoluteOffset, checked((int)rawLen));
                }
                else if (opType == OpEmbeddedRaw)
                {
                    payload = br.ReadBytes(checked((int)storedLen));
                }
                else if (opType == OpEmbeddedLz4)
                {
                    byte[] comp = br.ReadBytes(checked((int)storedLen));
                    payload = DecompressLz4Block(comp, checked((int)rawLen));
                }
                else
                {
                    throw new InvalidDataException($"Unknown IE4P opcode {opType}.");
                }

                uint actualCrc = ComputeCrc32(payload);
                if (actualCrc != expectedCrc)
                {
                    throw new InvalidDataException(
                        $"CRC32 mismatch on entry #{fileIndex} (op={opType}): 0x{actualCrc:X8} != 0x{expectedCrc:X8}.");
                }

                outFs.Write(payload, 0, payload.Length);
                uint remPad = padLen;
                while (remPad > 0)
                {
                    int chunk = (int)Math.Min(remPad, (uint)zeroPad.Length);
                    outFs.Write(zeroPad, 0, chunk);
                    remPad -= (uint)chunk;
                }
            }

            outFs.Flush();
            if ((ulong)outFs.Length != expectedSize)
            {
                throw new InvalidDataException(
                    $"Reconstructed ie_a.fa length {outFs.Length} != expected {expectedSize}.");
            }
        }

        public static byte[] CompressLz4Block(ReadOnlySpan<byte> src)
        {
            if (src.IsEmpty)
                return Array.Empty<byte>();

            // Standard raw LZ4 block encoder (16-bit hash table, 4-byte minimum match, last 5 bytes literals)
            int srcLen = src.Length;
            using var ms = new MemoryStream(srcLen + (srcLen / 255) + 16);
            int[] hashTable = new int[1 << 16];
            Array.Fill(hashTable, -1);

            int anchor = 0;
            int ip = 0;
            int matchLimit = srcLen - 5;
            int mflimit = srcLen - 12;

            while (ip <= mflimit)
            {
                uint seq = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(ip, 4));
                int h = (int)((seq * 2654435761U) >> 16);
                int refPos = hashTable[h];
                hashTable[h] = ip;

                if (refPos < 0 || (ip - refPos) > 0xFFFF ||
                    BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(refPos, 4)) != seq)
                {
                    ip++;
                    continue;
                }

                // Found a 4-byte match at refPos..ip
                int litLen = ip - anchor;
                int matchLen = 4;
                while (ip + matchLen < matchLimit && src[refPos + matchLen] == src[ip + matchLen])
                {
                    matchLen++;
                }

                int tokenLit = Math.Min(litLen, 15);
                int tokenMatch = Math.Min(matchLen - 4, 15);
                ms.WriteByte((byte)((tokenLit << 4) | tokenMatch));

                if (litLen >= 15)
                {
                    int rem = litLen - 15;
                    while (rem >= 255)
                    {
                        ms.WriteByte(255);
                        rem -= 255;
                    }
                    ms.WriteByte((byte)rem);
                }

                ms.Write(src.Slice(anchor, litLen));

                int offset = ip - refPos;
                ms.WriteByte((byte)(offset & 0xFF));
                ms.WriteByte((byte)((offset >> 8) & 0xFF));

                if (matchLen - 4 >= 15)
                {
                    int rem = (matchLen - 4) - 15;
                    while (rem >= 255)
                    {
                        ms.WriteByte(255);
                        rem -= 255;
                    }
                    ms.WriteByte((byte)rem);
                }

                ip += matchLen;
                anchor = ip;
            }

            // Trailing literals
            int lastLit = srcLen - anchor;
            int lastTokenLit = Math.Min(lastLit, 15);
            ms.WriteByte((byte)(lastTokenLit << 4));
            if (lastLit >= 15)
            {
                int rem = lastLit - 15;
                while (rem >= 255)
                {
                    ms.WriteByte(255);
                    rem -= 255;
                }
                ms.WriteByte((byte)rem);
            }
            ms.Write(src.Slice(anchor, lastLit));

            return ms.ToArray();
        }

        public static byte[] DecompressLz4Block(ReadOnlySpan<byte> src, int uncompressedLength)
        {
            if (uncompressedLength == 0)
                return Array.Empty<byte>();

            byte[] dst = new byte[uncompressedLength];
            int sp = 0;
            int dp = 0;

            while (sp < src.Length)
            {
                byte token = src[sp++];
                int litLen = token >> 4;
                if (litLen == 15)
                {
                    byte s;
                    do
                    {
                        s = src[sp++];
                        litLen += s;
                    } while (s == 255);
                }

                src.Slice(sp, litLen).CopyTo(dst.AsSpan(dp, litLen));
                sp += litLen;
                dp += litLen;

                if (sp >= src.Length)
                    break;

                int offset = src[sp] | (src[sp + 1] << 8);
                sp += 2;
                if (offset <= 0 || offset > dp)
                    throw new InvalidDataException($"Invalid LZ4 match offset {offset} at dp={dp}.");

                int matchLen = (token & 0x0F) + 4;
                if ((token & 0x0F) == 15)
                {
                    byte s;
                    do
                    {
                        s = src[sp++];
                        matchLen += s;
                    } while (s == 255);
                }

                int matchSrc = dp - offset;
                for (int i = 0; i < matchLen; i++)
                {
                    dst[dp++] = dst[matchSrc + i];
                }
            }

            if (dp != uncompressedLength)
                throw new InvalidDataException($"LZ4 decompressed {dp} bytes, expected {uncompressedLength}.");

            return dst;
        }

        private static ParsedArc0Index ParseArc0Index(FileStream stream)
        {
            EncodingInitializer.EnsureRegistered();
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");

            stream.Seek(0, SeekOrigin.Begin);
            var reader = new BinaryDataReader(stream);
            var header = reader.ReadStruct<ARC0Support.Header>();
            if (header.Magic != Arc0SafeArchive.Arc0Magic)
                throw new InvalidDataException($"Invalid ARC0 magic 0x{header.Magic:X8}.");

            reader.Seek((uint)header.DirectoryEntriesOffset);
            byte[] dirComp = reader.GetSection(header.DirectoryHashOffset - header.DirectoryEntriesOffset);
            byte[] dirRaw = Compressor.Decompress(dirComp);
            var dirReader = new BinaryDataReader(dirRaw);
            var dirEntries = dirReader.ReadMultipleStruct<ARC0Support.DirectoryEntry>(header.DirectoryEntriesCount);

            reader.Seek((uint)header.FileEntriesOffset);
            byte[] fileComp = reader.GetSection(header.NameOffset - header.FileEntriesOffset);
            byte[] fileRaw = Compressor.Decompress(fileComp);
            var fileReader = new BinaryDataReader(fileRaw);
            var fileEntries = fileReader.ReadMultipleStruct<ARC0Support.FileEntry>(header.FileEntriesCount);

            reader.Seek((uint)header.NameOffset);
            byte[] nameComp = reader.GetSection(header.DataOffset - header.NameOffset);
            byte[] nameRaw = Compressor.Decompress(nameComp);
            var nameReader = new BinaryDataReader(nameRaw);

            var indexed = new IndexedArc0Entry[fileEntries.Length];
            foreach (var dir in dirEntries)
            {
                nameReader.Seek((uint)dir.DirectoryNameStartOffset);
                string rawDirName = (nameReader.ReadString(shiftJis) ?? string.Empty).Replace('\\', '/').Trim('/');
                string prefix = string.IsNullOrEmpty(rawDirName) ? string.Empty : rawDirName + "/";

                int first = dir.FirstFileIndex;
                int end = Math.Min(fileEntries.Length, first + dir.FileCount);
                for (int idx = first; idx < end; idx++)
                {
                    var fe = fileEntries[idx];
                    nameReader.Seek((uint)(dir.FileNameStartOffset + fe.NameOffsetInFolder));
                    string fileName = nameReader.ReadString(shiftJis) ?? string.Empty;
                    indexed[idx] = new IndexedArc0Entry
                    {
                        FileIndex = idx,
                        VirtualPath = prefix + fileName,
                        FileOffsetInData = fe.FileOffset,
                        FileSize = fe.FileSize,
                        AbsoluteOffset = (long)header.DataOffset + fe.FileOffset
                    };
                }
            }

            for (int i = 0; i < indexed.Length; i++)
            {
                if (indexed[i] == null)
                {
                    var fe = fileEntries[i];
                    indexed[i] = new IndexedArc0Entry
                    {
                        FileIndex = i,
                        VirtualPath = $"__unreferenced__/{i}",
                        FileOffsetInData = fe.FileOffset,
                        FileSize = fe.FileSize,
                        AbsoluteOffset = (long)header.DataOffset + fe.FileOffset
                    };
                }
            }

            byte[] prefixBytes = ReadSlice(stream, 0, header.DataOffset);
            return new ParsedArc0Index
            {
                Header = header,
                FileLength = stream.Length,
                PrefixBytes = prefixBytes,
                EntriesByIndex = indexed
            };
        }

        private static byte[] ReadSlice(FileStream fs, long offset, int length)
        {
            if (length == 0)
                return Array.Empty<byte>();
            byte[] buf = new byte[length];
            fs.Seek(offset, SeekOrigin.Begin);
            ReadExact(fs, buf, 0, length);
            return buf;
        }

        private static void ReadExact(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r <= 0)
                    throw new EndOfStreamException($"Unexpected EOF after {total}/{count} bytes.");
                total += r;
            }
        }

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? (0xEDB88320U ^ (c >> 1)) : (c >> 1);
                table[i] = c;
            }
            return table;
        }

        public static uint ComputeCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFU;
            for (int i = 0; i < data.Length; i++)
                crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFU;
        }
    }
}
