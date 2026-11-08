using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Lynx.Core.RomFs
{
    /// <summary>
    /// Metadata inspected from a Nintendo 3DS container (.3ds NCSD, .cxi NCCH, or RomFS image).
    /// </summary>
    public sealed class RomContainerMetadata
    {
        public GameInputKind Kind { get; init; }
        public string TitleIdHex { get; init; } = string.Empty;
        public string ProductCode { get; init; } = string.Empty;
        public GameEdition DetectedEdition { get; init; } = GameEdition.Auto;
        public bool IsEncrypted { get; init; }
        public long RomFsByteOffset { get; init; }
        public long RomFsByteLength { get; init; }
        public long IeAFaByteOffset { get; init; } = -1;
        public long IeAFaByteLength { get; init; } = -1;
        public IReadOnlyList<string> RomFsFiles { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Pure C# read-only parser for Nintendo 3DS NCSD (.3ds/.cci), NCCH (.cxi/.app),
    /// and IVFC / Level-3 RomFS containers. Never writes to the input file.
    /// </summary>
    public static class Nintendo3dsRomReader
    {
        public const uint NcsdMagic = 0x4453434E; // "NCSD" at 0x100
        public const uint NcchMagic = 0x4843434E; // "NCCH" at +0x100
        public const uint IvfcMagic = 0x43465649; // "IVFC" at RomFS + 0x00
        public const long MediaUnitSize = 0x200;  // 512 bytes
        public const uint RomFsLevel3HeaderSize = 0x28;

        public static RomContainerMetadata Inspect(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("ROM file not found.", filePath);

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return InspectStream(fs);
        }

        public static RomContainerMetadata InspectStream(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));

            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            // Check direct ARC0 first
            if (stream.Length >= 0x48)
            {
                stream.Seek(0, SeekOrigin.Begin);
                uint magic0 = reader.ReadUInt32();
                if (magic0 == Arc0SafeArchive.Arc0Magic)
                {
                    return new RomContainerMetadata
                    {
                        Kind = GameInputKind.Arc0File,
                        IeAFaByteOffset = 0,
                        IeAFaByteLength = stream.Length,
                        RomFsFiles = new[] { "/ie_a.fa" }
                    };
                }

                if (magic0 == IvfcMagic || magic0 == RomFsLevel3HeaderSize)
                {
                    return ParseRomFsAtOffset(stream, reader, GameInputKind.RomFsImage, 0, stream.Length, string.Empty, string.Empty, false);
                }
            }

            if (stream.Length < 0x200)
            {
                throw new InvalidDataException("Input file is too small to be a valid Nintendo 3DS ROM or ARC0 archive.");
            }

            stream.Seek(0x100, SeekOrigin.Begin);
            uint headerMagic = reader.ReadUInt32();

            long ncchOffset;
            GameInputKind kind;

            if (headerMagic == NcsdMagic)
            {
                kind = GameInputKind.Nintendo3dsRom;
                // Partition 0 entry in NCSD partition table at 0x120: { uint OffsetMU, uint LengthMU }
                stream.Seek(0x120, SeekOrigin.Begin);
                uint part0OffsetMu = reader.ReadUInt32();
                uint part0LengthMu = reader.ReadUInt32();
                ncchOffset = part0OffsetMu * MediaUnitSize;
                long part0Len = part0LengthMu * MediaUnitSize;
                if (part0Len <= 0 || ncchOffset + 0x200 > stream.Length)
                {
                    throw new InvalidDataException("NCSD Partition 0 (Game Executable NCCH) is missing or truncated.");
                }
            }
            else if (headerMagic == NcchMagic)
            {
                kind = GameInputKind.NcchContainer;
                ncchOffset = 0;
            }
            else
            {
                throw new InvalidDataException(
                    $"Unrecognized container magic 0x{headerMagic:X8} at +0x100 (expected 'NCSD', 'NCCH', 'IVFC', or 'ARC0').");
            }

            // Validate NCCH header
            stream.Seek(ncchOffset + 0x100, SeekOrigin.Begin);
            uint ncchMagic = reader.ReadUInt32();
            if (ncchMagic != NcchMagic)
            {
                throw new InvalidDataException(
                    $"Partition 0 at 0x{ncchOffset:X} does not contain a valid NCCH header (found 0x{ncchMagic:X8}).");
            }

            stream.Seek(ncchOffset + 0x118, SeekOrigin.Begin);
            ulong programId = reader.ReadUInt64();
            string titleIdHex = programId.ToString("X16");

            stream.Seek(ncchOffset + 0x150, SeekOrigin.Begin);
            byte[] prodBytes = reader.ReadBytes(16);
            int nullIdx = Array.IndexOf(prodBytes, (byte)0);
            string productCode = Encoding.ASCII.GetString(prodBytes, 0, nullIdx >= 0 ? nullIdx : prodBytes.Length).Trim();

            stream.Seek(ncchOffset + 0x18F, SeekOrigin.Begin);
            byte ncchFlags7 = reader.ReadByte();
            bool noCryptoFlag = (ncchFlags7 & 0x04) != 0;

            stream.Seek(ncchOffset + 0x1B0, SeekOrigin.Begin);
            uint romFsOffsetMu = reader.ReadUInt32();
            uint romFsSizeMu = reader.ReadUInt32();

            long romFsByteOffset = ncchOffset + (romFsOffsetMu * MediaUnitSize);
            long romFsByteLength = romFsSizeMu * MediaUnitSize;

            if (romFsSizeMu == 0 || romFsByteOffset + 0x60 > stream.Length)
            {
                throw new InvalidDataException("NCCH partition does not contain a valid RomFS section.");
            }

            stream.Seek(romFsByteOffset, SeekOrigin.Begin);
            uint romFsMagic = reader.ReadUInt32();
            bool hasPlaintextRomFs = romFsMagic == IvfcMagic || romFsMagic == RomFsLevel3HeaderSize;
            bool isEncrypted = !hasPlaintextRomFs && !noCryptoFlag;

            GameEdition detectedEdition = TitleIdCatalog.DetectEdition(titleIdHex, productCode);

            if (!hasPlaintextRomFs)
            {
                return new RomContainerMetadata
                {
                    Kind = kind,
                    TitleIdHex = titleIdHex,
                    ProductCode = productCode,
                    DetectedEdition = detectedEdition,
                    IsEncrypted = true,
                    RomFsByteOffset = romFsByteOffset,
                    RomFsByteLength = romFsByteLength
                };
            }

            return ParseRomFsAtOffset(
                stream,
                reader,
                kind,
                romFsByteOffset,
                romFsByteLength,
                titleIdHex,
                productCode,
                isEncrypted);
        }

        /// <summary>
        /// Extracts <c>ie_a.fa</c> from a decrypted .3ds / .cxi / .romfs file into a destination path
        /// without modifying the input file. Falls back to external <c>3dstool</c>/<c>ctrtool</c> on PATH if needed.
        /// </summary>
        public static RomContainerMetadata ExtractIeAFaToFile(
            string romFilePath,
            string destinationIeAFaPath,
            IProgress<int> progress = null)
        {
            if (string.IsNullOrWhiteSpace(romFilePath))
                throw new ArgumentException("ROM path cannot be empty.", nameof(romFilePath));
            if (string.IsNullOrWhiteSpace(destinationIeAFaPath))
                throw new ArgumentException("Destination path cannot be empty.", nameof(destinationIeAFaPath));
            if (Arc0SafeArchive.PathsEqual(romFilePath, destinationIeAFaPath))
                throw new InvalidOperationException("Destination path cannot equal the source ROM path.");

            var metadata = Inspect(romFilePath);

            if (metadata.IsEncrypted)
            {
                if (TryExtractWithExternalCli(romFilePath, destinationIeAFaPath))
                {
                    return metadata;
                }

                throw new InvalidOperationException(
                    $"El archivo '{Path.GetFileName(romFilePath)}' (TitleID {metadata.TitleIdHex}, código {metadata.ProductCode}) " +
                    "está cifrado con criptografía NCCH de Nintendo 3DS y no se encontró una herramienta externa compatible en PATH. " +
                    "Utiliza una copia desencriptada (.3ds para Citra), una carpeta RomFS extraída o el archivo ie_a.fa directamente.");
            }

            if (metadata.IeAFaByteOffset < 0 || metadata.IeAFaByteLength <= 0)
            {
                throw new FileNotFoundException(
                    $"No se encontró '/ie_a.fa' dentro del RomFS de '{Path.GetFileName(romFilePath)}'.");
            }

            string outDir = Path.GetDirectoryName(Path.GetFullPath(destinationIeAFaPath));
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            using var inStream = new FileStream(romFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var outStream = new FileStream(destinationIeAFaPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            inStream.Seek(metadata.IeAFaByteOffset, SeekOrigin.Begin);
            byte[] buffer = new byte[81920];
            long remaining = metadata.IeAFaByteLength;
            long total = remaining;
            int lastPct = -1;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = inStream.Read(buffer, 0, toRead);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of stream while extracting ie_a.fa from RomFS.");

                outStream.Write(buffer, 0, read);
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

            return metadata;
        }

        private static RomContainerMetadata ParseRomFsAtOffset(
            Stream stream,
            BinaryReader reader,
            GameInputKind kind,
            long romFsOffset,
            long romFsLength,
            string titleIdHex,
            string productCode,
            bool isEncrypted)
        {
            stream.Seek(romFsOffset, SeekOrigin.Begin);
            uint magic = reader.ReadUInt32();

            long level3Offset;
            if (magic == IvfcMagic)
            {
                uint magicNum = reader.ReadUInt32(); // 0x00010000 for RomFS
                uint masterHashSize = reader.ReadUInt32();
                // Standard 3DS RomFS IVFC places Level 3 header at AlignUp(0x60 + masterHashSize, 0x1000)
                long rawEnd = 0x60L + masterHashSize;
                level3Offset = romFsOffset + AlignUp(rawEnd, 0x1000L);
            }
            else if (magic == RomFsLevel3HeaderSize)
            {
                level3Offset = romFsOffset;
            }
            else
            {
                throw new InvalidDataException($"Invalid RomFS IVFC / Level-3 header magic 0x{magic:X8}.");
            }

            stream.Seek(level3Offset, SeekOrigin.Begin);
            uint l3HeaderLen = reader.ReadUInt32();
            if (l3HeaderLen != RomFsLevel3HeaderSize)
            {
                throw new InvalidDataException(
                    $"Unexpected RomFS Level-3 header length 0x{l3HeaderLen:X} at 0x{level3Offset:X} (expected 0x28).");
            }

            uint dirHashOff = reader.ReadUInt32();
            uint dirHashLen = reader.ReadUInt32();
            uint dirMetaOff = reader.ReadUInt32();
            uint dirMetaLen = reader.ReadUInt32();
            uint fileHashOff = reader.ReadUInt32();
            uint fileHashLen = reader.ReadUInt32();
            uint fileMetaOff = reader.ReadUInt32();
            uint fileMetaLen = reader.ReadUInt32();
            uint fileDataOff = reader.ReadUInt32();

            _ = dirHashOff;
            _ = dirHashLen;
            _ = fileHashOff;
            _ = fileHashLen;

            // Parse directories so we can reconstruct full RomFS paths
            stream.Seek(level3Offset + dirMetaOff, SeekOrigin.Begin);
            byte[] dirMeta = reader.ReadBytes((int)dirMetaLen);
            var dirPaths = ParseDirectoryTable(dirMeta);

            // Parse file metadata entries
            stream.Seek(level3Offset + fileMetaOff, SeekOrigin.Begin);
            byte[] fileMeta = reader.ReadBytes((int)fileMetaLen);

            var files = new List<string>();
            long ieaOffset = -1;
            long ieaLength = -1;

            int pos = 0;
            while (pos + 0x20 <= fileMeta.Length)
            {
                uint parentDirOff = BitConverter.ToUInt32(fileMeta, pos + 0x00);
                ulong dataOff = BitConverter.ToUInt64(fileMeta, pos + 0x08);
                ulong dataLen = BitConverter.ToUInt64(fileMeta, pos + 0x10);
                uint nameByteLen = BitConverter.ToUInt32(fileMeta, pos + 0x1C);

                if (pos + 0x20 + nameByteLen > fileMeta.Length)
                    break;

                string fileName = nameByteLen > 0
                    ? Encoding.Unicode.GetString(fileMeta, pos + 0x20, (int)nameByteLen)
                    : string.Empty;

                if (!string.IsNullOrEmpty(fileName))
                {
                    string parentPath = dirPaths.TryGetValue(parentDirOff, out string p) ? p : string.Empty;
                    string fullPath = string.IsNullOrEmpty(parentPath) ? "/" + fileName : parentPath + "/" + fileName;
                    files.Add(fullPath);

                    if (string.Equals(fileName, "ie_a.fa", StringComparison.OrdinalIgnoreCase))
                    {
                        ieaOffset = level3Offset + fileDataOff + (long)dataOff;
                        ieaLength = (long)dataLen;
                    }
                }

                int step = 0x20 + (int)AlignUp(nameByteLen, 4);
                if (step <= 0)
                    break;
                pos += step;
            }

            return new RomContainerMetadata
            {
                Kind = kind,
                TitleIdHex = titleIdHex,
                ProductCode = productCode,
                DetectedEdition = TitleIdCatalog.DetectEdition(titleIdHex, productCode),
                IsEncrypted = isEncrypted,
                RomFsByteOffset = romFsOffset,
                RomFsByteLength = romFsLength,
                IeAFaByteOffset = ieaOffset,
                IeAFaByteLength = ieaLength,
                RomFsFiles = files
            };
        }

        private static Dictionary<uint, string> ParseDirectoryTable(byte[] dirMeta)
        {
            var rawEntries = new Dictionary<uint, (uint Parent, string Name)>();
            int pos = 0;
            while (pos + 0x18 <= dirMeta.Length)
            {
                uint entryOff = (uint)pos;
                uint parentOff = BitConverter.ToUInt32(dirMeta, pos + 0x00);
                uint nameByteLen = BitConverter.ToUInt32(dirMeta, pos + 0x14);
                if (pos + 0x18 + nameByteLen > dirMeta.Length)
                    break;

                string dirName = nameByteLen > 0
                    ? Encoding.Unicode.GetString(dirMeta, pos + 0x18, (int)nameByteLen)
                    : string.Empty;

                rawEntries[entryOff] = (parentOff, dirName);
                int step = 0x18 + (int)AlignUp(nameByteLen, 4);
                if (step <= 0)
                    break;
                pos += step;
            }

            var resolved = new Dictionary<uint, string>();
            foreach (uint key in rawEntries.Keys)
            {
                resolved[key] = ResolveDirPath(key, rawEntries, 0);
            }
            return resolved;
        }

        private static string ResolveDirPath(
            uint offset,
            Dictionary<uint, (uint Parent, string Name)> table,
            int depth)
        {
            if (offset == 0 || depth > 32 || !table.TryGetValue(offset, out var entry))
                return string.Empty;
            if (string.IsNullOrEmpty(entry.Name))
                return string.Empty;

            string parent = ResolveDirPath(entry.Parent, table, depth + 1);
            return string.IsNullOrEmpty(parent) ? "/" + entry.Name : parent + "/" + entry.Name;
        }

        private static bool TryExtractWithExternalCli(string romFilePath, string destinationIeAFaPath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_ctr_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                string romFsDir = Path.Combine(tempDir, "romfs");

                // Attempt ctrtool if installed locally on user's PC
                if (TryRunProcess("ctrtool", $"--romfsdir=\"{romFsDir}\" \"{romFilePath}\""))
                {
                    string candidate = Path.Combine(romFsDir, "ie_a.fa");
                    if (File.Exists(candidate))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationIeAFaPath))!);
                        File.Copy(candidate, destinationIeAFaPath, overwrite: true);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        private static bool TryRunProcess(string exeName, string arguments)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = exeName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc == null)
                    return false;
                proc.WaitForExit(120_000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static long AlignUp(long value, long alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }
    }
}
