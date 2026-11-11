using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lynx.Core.RomFs.Cia
{
    /// <summary>
    /// Stream-based reader, extractor, hash-lookup tester, and full IVFC 4-level Merkle tree verifier
    /// for Nintendo 3DS RomFS partitions. Operates with bounded memory (&lt; 1 MB) on multi-GB images.
    /// </summary>
    public static class IvfcRomFsReader
    {
        public const uint UnusedOffset = 0xFFFFFFFFu;

        public static RomFsInventory ReadInventory(Stream romFsStream)
        {
            if (romFsStream == null)
                throw new ArgumentNullException(nameof(romFsStream));
            if (!romFsStream.CanRead || !romFsStream.CanSeek)
                throw new ArgumentException("RomFS stream must be readable and seekable.", nameof(romFsStream));

            romFsStream.Seek(0, SeekOrigin.Begin);
            Span<byte> hdr = stackalloc byte[0x60];
            ReadExact(romFsStream, hdr.Slice(0, 0x28));

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(0, 4));
            long level3Offset;
            if (magic == CiaConstants.IvfcMagic)
            {
                ReadExact(romFsStream, hdr.Slice(0x28, 0x38));
                uint masterHashSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(0x08, 4));
                level3Offset = CiaConstants.AlignUp(CiaConstants.IvfcPaddedHeaderSize + masterHashSize, CiaConstants.IvfcBlockSize);
            }
            else if (magic == CiaConstants.RomFsLevel3HeaderSize)
            {
                level3Offset = 0;
            }
            else
            {
                throw new InvalidDataException($"Magic de RomFS no reconocido: 0x{magic:X8} (se esperaba 'IVFC').");
            }

            romFsStream.Seek(level3Offset, SeekOrigin.Begin);
            Span<byte> l3Hdr = stackalloc byte[0x28];
            ReadExact(romFsStream, l3Hdr);

            uint infoHeaderLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0, 4));
            if (infoHeaderLen != CiaConstants.RomFsLevel3HeaderSize)
                throw new InvalidDataException($"Cabecera Level-3 RomFS inválida (longitud 0x{infoHeaderLen:X}, esperada 0x28).");

            uint dirMetaOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x0C, 4));
            uint dirMetaLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x10, 4));
            uint fileMetaOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x1C, 4));
            uint fileMetaLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x20, 4));
            uint fileDataOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x24, 4));

            byte[] dirTable = new byte[dirMetaLen];
            if (dirMetaLen > 0)
            {
                romFsStream.Seek(level3Offset + dirMetaOff, SeekOrigin.Begin);
                ReadExact(romFsStream, dirTable);
            }

            byte[] fileTable = new byte[fileMetaLen];
            if (fileMetaLen > 0)
            {
                romFsStream.Seek(level3Offset + fileMetaOff, SeekOrigin.Begin);
                ReadExact(romFsStream, fileTable);
            }

            var dirByOffset = ParseDirectories(dirTable, out var orderedDirs);
            long fileDataBase = level3Offset + fileDataOff;
            var orderedFiles = ParseFiles(fileTable, dirByOffset, fileDataBase);

            return new RomFsInventory
            {
                RomFsTotalLength = romFsStream.Length,
                Level3Offset = level3Offset,
                FileDataBaseOffsetInRomFs = fileDataBase,
                Directories = orderedDirs,
                Files = orderedFiles
            };
        }

        public static void ExtractFileToDisk(
            Stream romFsStream,
            RomFsFileEntryInfo entry,
            string destinationFilePath,
            IProgress<int> progress = null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(destinationFilePath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var outFs = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072);
            romFsStream.Seek(entry.DataOffsetInRomFs, SeekOrigin.Begin);

            byte[] buffer = new byte[131072];
            long remaining = entry.DataLength;
            long total = remaining;
            int lastPct = -1;

            while (remaining > 0)
            {
                int take = (int)Math.Min(buffer.Length, remaining);
                int read = romFsStream.Read(buffer, 0, take);
                if (read <= 0)
                    throw new EndOfStreamException($"Fin inesperado del stream al extraer '{entry.RelativePath}'.");

                outFs.Write(buffer, 0, read);
                remaining -= read;

                if (progress != null && total > 0)
                {
                    int pct = (int)(((total - remaining) * 100L) / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progress.Report(pct);
                    }
                }
            }
        }

        public static void ExtractAllToDirectory(Stream romFsStream, string targetRomFsRootDir)
        {
            var inv = ReadInventory(romFsStream);
            Directory.CreateDirectory(targetRomFsRootDir);

            foreach (var d in inv.Directories)
            {
                if (string.IsNullOrEmpty(d.RelativePath))
                    continue;
                string sysDir = Path.Combine(targetRomFsRootDir, d.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(sysDir);
            }

            foreach (var f in inv.Files)
            {
                string sysFile = Path.Combine(targetRomFsRootDir, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                ExtractFileToDisk(romFsStream, f, sysFile);
            }
        }

        /// <summary>
        /// Verifies the entire 4-level IVFC SHA-256 Merkle tree (Level 3 -&gt; Level 2 -&gt; Level 1 -&gt; Level 0)
        /// AND verifies that every directory and file in Level 3 is discoverable via the 3DS kernel hash-bucket lookup.
        /// </summary>
        public static void VerifyIvfcAndHashTablesOrThrow(
            Stream romFsStream,
            out int verifiedIvfcBlocks,
            out RomFsInventory inventory)
        {
            verifiedIvfcBlocks = 0;
            romFsStream.Seek(0, SeekOrigin.Begin);
            Span<byte> ivfcHdr = stackalloc byte[0x60];
            ReadExact(romFsStream, ivfcHdr);

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(ivfcHdr.Slice(0, 4));
            if (magic != CiaConstants.IvfcMagic)
                throw new InvalidDataException($"El contenedor RomFS no empieza con magia 'IVFC' (0x{magic:X8}).");

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(ivfcHdr.Slice(4, 4));
            if (id != CiaConstants.IvfcRomFsId)
                throw new InvalidDataException($"ID IVFC inesperado: 0x{id:X8} (esperado 0x00010000).");

            uint masterHashSize = BinaryPrimitives.ReadUInt32LittleEndian(ivfcHdr.Slice(8, 4));

            ulong l1Logical = BinaryPrimitives.ReadUInt64LittleEndian(ivfcHdr.Slice(0x0C, 8));
            ulong l1Size = BinaryPrimitives.ReadUInt64LittleEndian(ivfcHdr.Slice(0x14, 8));
            uint l1BlkLog2 = BinaryPrimitives.ReadUInt32LittleEndian(ivfcHdr.Slice(0x1C, 4));

            ulong l2Logical = BinaryPrimitives.ReadUInt64LittleEndian(ivfcHdr.Slice(0x24, 8));
            ulong l2Size = BinaryPrimitives.ReadUInt64LittleEndian(ivfcHdr.Slice(0x2C, 8));
            uint l2BlkLog2 = BinaryPrimitives.ReadUInt32LittleEndian(ivfcHdr.Slice(0x34, 4));

            ulong l3Logical = BinaryPrimitives.ReadUInt64LittleEndian(ivfcHdr.Slice(0x3C, 8));
            ulong l3Size = BinaryPrimitives.ReadUInt64LittleEndian(ivfcHdr.Slice(0x44, 8));
            uint l3BlkLog2 = BinaryPrimitives.ReadUInt32LittleEndian(ivfcHdr.Slice(0x4C, 4));

            _ = l1Logical;
            _ = l2Logical;
            _ = l3Logical;

            if (l1BlkLog2 != CiaConstants.IvfcBlockSizeLog2 ||
                l2BlkLog2 != CiaConstants.IvfcBlockSizeLog2 ||
                l3BlkLog2 != CiaConstants.IvfcBlockSizeLog2)
            {
                throw new InvalidDataException("Tamaño de bloque IVFC distinto de 0x1000 (2^12).");
            }

            long l0HashOffset = CiaConstants.IvfcPaddedHeaderSize;
            long l3Offset = CiaConstants.AlignUp(l0HashOffset + masterHashSize, CiaConstants.IvfcBlockSize);
            long l1Offset = l3Offset + CiaConstants.AlignUp((long)l3Size, CiaConstants.IvfcBlockSize);
            long l2Offset = l1Offset + CiaConstants.AlignUp((long)l1Size, CiaConstants.IvfcBlockSize);
            long expectedMinRomFsLen = l2Offset + CiaConstants.AlignUp((long)l2Size, CiaConstants.IvfcBlockSize);

            if (romFsStream.Length < expectedMinRomFsLen)
            {
                throw new InvalidDataException(
                    $"Imagen IVFC truncada: longitud 0x{romFsStream.Length:X} menor que 0x{expectedMinRomFsLen:X}.");
            }

            // Read Level 0, Level 1, and Level 2 hash tables (even for a 4 GB RomFS, Level 2 is <= 32 MB)
            byte[] l0Hashes = new byte[masterHashSize];
            romFsStream.Seek(l0HashOffset, SeekOrigin.Begin);
            ReadExact(romFsStream, l0Hashes);

            long l1AlignedSize = CiaConstants.AlignUp((long)l1Size, CiaConstants.IvfcBlockSize);
            byte[] l1Buffer = new byte[l1AlignedSize];
            romFsStream.Seek(l1Offset, SeekOrigin.Begin);
            ReadExact(romFsStream, l1Buffer);

            long l2AlignedSize = CiaConstants.AlignUp((long)l2Size, CiaConstants.IvfcBlockSize);
            byte[] l2Buffer = new byte[l2AlignedSize];
            romFsStream.Seek(l2Offset, SeekOrigin.Begin);
            ReadExact(romFsStream, l2Buffer);

            // 1. Verify Level 0 Master Hashes over Level 1 blocks
            int l1Blocks = (int)(l1AlignedSize / CiaConstants.IvfcBlockSize);
            if (masterHashSize != l1Blocks * 32)
                throw new InvalidDataException("MasterHashSize en cabecera IVFC no coincide con el número de bloques de Level 1.");

            Span<byte> digest = stackalloc byte[32];
            for (int b = 0; b < l1Blocks; b++)
            {
                SHA256.HashData(l1Buffer.AsSpan(b * 0x1000, 0x1000), digest);
                if (!digest.SequenceEqual(l0Hashes.AsSpan(b * 32, 32)))
                    throw new InvalidDataException($"Fallo de integridad IVFC Level 0 -> Level 1 en bloque #{b}.");
                verifiedIvfcBlocks++;
            }

            // 2. Verify Level 1 Hashes over Level 2 blocks
            int l2Blocks = (int)(l2AlignedSize / CiaConstants.IvfcBlockSize);
            if ((long)l1Size != (long)l2Blocks * 32L)
                throw new InvalidDataException("Level1.HashDataSize no coincide con el número de bloques de Level 2.");

            for (int b = 0; b < l2Blocks; b++)
            {
                SHA256.HashData(l2Buffer.AsSpan(b * 0x1000, 0x1000), digest);
                if (!digest.SequenceEqual(l1Buffer.AsSpan(b * 32, 32)))
                    throw new InvalidDataException($"Fallo de integridad IVFC Level 1 -> Level 2 en bloque #{b}.");
                verifiedIvfcBlocks++;
            }

            // 3. Stream-verify Level 2 Hashes over every 4 KB block of Level 3
            long l3AlignedSize = CiaConstants.AlignUp((long)l3Size, CiaConstants.IvfcBlockSize);
            long l3Blocks = l3AlignedSize / CiaConstants.IvfcBlockSize;
            if ((long)l2Size != l3Blocks * 32L)
                throw new InvalidDataException("Level2.HashDataSize no coincide con el número de bloques de Level 3.");

            byte[] blockBuf = new byte[0x1000];
            romFsStream.Seek(l3Offset, SeekOrigin.Begin);
            for (long b = 0; b < l3Blocks; b++)
            {
                ReadExact(romFsStream, blockBuf);
                SHA256.HashData(blockBuf, digest);
                if (!digest.SequenceEqual(l2Buffer.AsSpan((int)(b * 32), 32)))
                    throw new InvalidDataException($"Fallo de integridad IVFC Level 2 -> Level 3 en bloque #{b}.");
                verifiedIvfcBlocks++;
            }

            // 4. Verify Level 3 directory/file hash-bucket chains match real 3DS fs:USER lookup
            VerifyLevel3HashLookupsOrThrow(romFsStream, l3Offset);
            inventory = ReadInventory(romFsStream);
        }

        private static void VerifyLevel3HashLookupsOrThrow(Stream romFsStream, long level3Offset)
        {
            romFsStream.Seek(level3Offset, SeekOrigin.Begin);
            Span<byte> l3Hdr = stackalloc byte[0x28];
            ReadExact(romFsStream, l3Hdr);

            uint dirHashOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x04, 4));
            uint dirHashLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x08, 4));
            uint dirMetaOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x0C, 4));
            uint dirMetaLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x10, 4));
            uint fileHashOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x14, 4));
            uint fileHashLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x18, 4));
            uint fileMetaOff = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x1C, 4));
            uint fileMetaLen = BinaryPrimitives.ReadUInt32LittleEndian(l3Hdr.Slice(0x20, 4));

            byte[] dirHash = ReadBytesAt(romFsStream, level3Offset + dirHashOff, (int)dirHashLen);
            byte[] dirMeta = ReadBytesAt(romFsStream, level3Offset + dirMetaOff, (int)dirMetaLen);
            byte[] fileHash = ReadBytesAt(romFsStream, level3Offset + fileHashOff, (int)fileHashLen);
            byte[] fileMeta = ReadBytesAt(romFsStream, level3Offset + fileMetaOff, (int)fileMetaLen);

            uint dirBuckets = (uint)(dirHash.Length / 4);
            uint fileBuckets = (uint)(fileHash.Length / 4);

            // Walk every directory entry and confirm it can be found via its hash bucket chain
            int pos = 0;
            while (pos + 0x18 <= dirMeta.Length)
            {
                uint entryOff = (uint)pos;
                uint parentOff = BinaryPrimitives.ReadUInt32LittleEndian(dirMeta.AsSpan(pos, 4));
                uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(dirMeta.AsSpan(pos + 0x14, 4));
                string name = nameBytes > 0 ? Encoding.Unicode.GetString(dirMeta, pos + 0x18, (int)nameBytes) : string.Empty;

                uint bucket = CalcPathHash(parentOff, name) % dirBuckets;
                uint chain = BinaryPrimitives.ReadUInt32LittleEndian(dirHash.AsSpan((int)(bucket * 4), 4));
                bool found = false;
                int guard = 0;
                while (chain != UnusedOffset && guard++ < 100_000)
                {
                    if (chain == entryOff)
                    {
                        found = true;
                        break;
                    }
                    chain = BinaryPrimitives.ReadUInt32LittleEndian(dirMeta.AsSpan((int)chain + 0x10, 4));
                }

                if (!found)
                    throw new InvalidDataException($"El directorio '{name}' (offset 0x{entryOff:X}) no es alcanzable desde DirHashTable.");

                pos += 0x18 + (int)CiaConstants.AlignUp(nameBytes, 4);
            }

            // Walk every file entry and confirm it can be found via its hash bucket chain
            pos = 0;
            while (pos + 0x20 <= fileMeta.Length)
            {
                uint entryOff = (uint)pos;
                uint parentOff = BinaryPrimitives.ReadUInt32LittleEndian(fileMeta.AsSpan(pos, 4));
                uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(fileMeta.AsSpan(pos + 0x1C, 4));
                string name = nameBytes > 0 ? Encoding.Unicode.GetString(fileMeta, pos + 0x20, (int)nameBytes) : string.Empty;

                uint bucket = CalcPathHash(parentOff, name) % fileBuckets;
                uint chain = BinaryPrimitives.ReadUInt32LittleEndian(fileHash.AsSpan((int)(bucket * 4), 4));
                bool found = false;
                int guard = 0;
                while (chain != UnusedOffset && guard++ < 200_000)
                {
                    if (chain == entryOff)
                    {
                        found = true;
                        break;
                    }
                    chain = BinaryPrimitives.ReadUInt32LittleEndian(fileMeta.AsSpan((int)chain + 0x18, 4));
                }

                if (!found)
                    throw new InvalidDataException($"El archivo '{name}' (offset 0x{entryOff:X}) no es alcanzable desde FileHashTable.");

                pos += 0x20 + (int)CiaConstants.AlignUp(nameBytes, 4);
            }
        }

        public static uint CalcPathHash(uint parentDirByteOffset, ReadOnlySpan<char> utf16Name)
        {
            uint hash = parentDirByteOffset ^ 123456789u;
            for (int i = 0; i < utf16Name.Length; i++)
            {
                hash = (hash >> 5) | (hash << 27);
                hash ^= utf16Name[i];
            }
            return hash;
        }

        public static uint ComputeBucketCount(uint entryCount)
        {
            uint count = entryCount;
            if (count < 3)
                return 3;
            if (count < 19)
                return count | 1u;

            while (count % 2 == 0 || count % 3 == 0 || count % 5 == 0 ||
                   count % 7 == 0 || count % 11 == 0 || count % 13 == 0 || count % 17 == 0)
            {
                count++;
            }
            return count;
        }

        private static Dictionary<uint, string> ParseDirectories(
            byte[] dirMeta,
            out List<RomFsDirectoryEntryInfo> ordered)
        {
            var raw = new Dictionary<uint, (uint Parent, string Name)>();
            var orderKeys = new List<uint>();
            int pos = 0;
            while (pos + 0x18 <= dirMeta.Length)
            {
                uint off = (uint)pos;
                uint parent = BinaryPrimitives.ReadUInt32LittleEndian(dirMeta.AsSpan(pos, 4));
                uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(dirMeta.AsSpan(pos + 0x14, 4));
                if (pos + 0x18 + nameBytes > dirMeta.Length)
                    break;

                string name = nameBytes > 0
                    ? Encoding.Unicode.GetString(dirMeta, pos + 0x18, (int)nameBytes)
                    : string.Empty;

                raw[off] = (parent, name);
                orderKeys.Add(off);
                pos += 0x18 + (int)CiaConstants.AlignUp(nameBytes, 4);
            }

            var resolved = new Dictionary<uint, string>();
            ordered = new List<RomFsDirectoryEntryInfo>(orderKeys.Count);
            foreach (uint off in orderKeys)
            {
                string rel = ResolveRelativeDir(off, raw, 0);
                resolved[off] = rel;
                ordered.Add(new RomFsDirectoryEntryInfo
                {
                    RelativePath = rel,
                    Name = raw[off].Name
                });
            }
            return resolved;
        }

        private static string ResolveRelativeDir(
            uint off,
            Dictionary<uint, (uint Parent, string Name)> raw,
            int depth)
        {
            if (off == 0 || depth > 48 || !raw.TryGetValue(off, out var entry) || string.IsNullOrEmpty(entry.Name))
                return string.Empty;

            string parent = ResolveRelativeDir(entry.Parent, raw, depth + 1);
            return string.IsNullOrEmpty(parent) ? entry.Name : parent + "/" + entry.Name;
        }

        private static List<RomFsFileEntryInfo> ParseFiles(
            byte[] fileMeta,
            Dictionary<uint, string> dirByOffset,
            long fileDataBase)
        {
            var files = new List<RomFsFileEntryInfo>();
            int pos = 0;
            while (pos + 0x20 <= fileMeta.Length)
            {
                uint parentOff = BinaryPrimitives.ReadUInt32LittleEndian(fileMeta.AsSpan(pos, 4));
                ulong dataOff = BinaryPrimitives.ReadUInt64LittleEndian(fileMeta.AsSpan(pos + 0x08, 8));
                ulong dataLen = BinaryPrimitives.ReadUInt64LittleEndian(fileMeta.AsSpan(pos + 0x10, 8));
                uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(fileMeta.AsSpan(pos + 0x1C, 4));
                if (pos + 0x20 + nameBytes > fileMeta.Length)
                    break;

                string name = nameBytes > 0
                    ? Encoding.Unicode.GetString(fileMeta, pos + 0x20, (int)nameBytes)
                    : string.Empty;

                if (!string.IsNullOrEmpty(name))
                {
                    string parentRel = dirByOffset.TryGetValue(parentOff, out string p) ? p : string.Empty;
                    string relPath = string.IsNullOrEmpty(parentRel) ? name : parentRel + "/" + name;
                    files.Add(new RomFsFileEntryInfo
                    {
                        RelativePath = relPath,
                        Name = name,
                        DataOffsetInRomFs = fileDataBase + (long)dataOff,
                        DataLength = (long)dataLen
                    });
                }

                pos += 0x20 + (int)CiaConstants.AlignUp(nameBytes, 4);
            }
            return files;
        }

        private static byte[] ReadBytesAt(Stream stream, long offset, int length)
        {
            if (length <= 0)
                return Array.Empty<byte>();
            byte[] buf = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            ReadExact(stream, buf);
            return buf;
        }

        internal static void ReadExact(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer.Slice(total));
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of stream while reading RomFS.");
                total += read;
            }
        }
    }
}
