using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Level5.Compression.LZ10;
using StudioElevenLib.Level5.Compression.NoCompression;
using StudioElevenLib.Tools;

namespace Lynx.Core.RomFs
{
    public sealed class ModifiedArchiveEntryInfo
    {
        public string VirtualPath { get; init; } = string.Empty;
        public long OriginalSizeBytes { get; init; }
        public long NewSizeBytes { get; init; }
    }

    /// <summary>
    /// Forensic telemetry describing how an <c>ARC0</c> (<c>ie_a.fa</c>) archive was read and written.
    /// </summary>
    public sealed class Arc0SaveSummary
    {
        public bool PreservedRetailTables { get; init; }
        public long OriginalArchiveSizeBytes { get; init; }
        public long WrittenArchiveSizeBytes { get; init; }
        public int DetectedDataAlignment { get; init; }
        public int DirectoryEntriesCount { get; init; }
        public int DirectoryHashesCount { get; init; }
        public int FileEntriesCount { get; init; }
        public int ModifiedFilesCount => ModifiedFiles.Count;
        public string DirectoryEntriesCompression { get; init; } = string.Empty;
        public string DirectoryHashesCompression { get; init; } = string.Empty;
        public string FileEntriesCompression { get; init; } = string.Empty;
        public string NameTableCompression { get; init; } = string.Empty;
        public List<ModifiedArchiveEntryInfo> ModifiedFiles { get; init; } = new();
    }

    public sealed class Arc0ArchiveInspection
    {
        public long ArchiveSizeBytes { get; init; }
        public uint Magic { get; init; }
        public int DirectoryEntriesCount { get; init; }
        public int DirectoryHashesCount { get; init; }
        public int FileEntriesCount { get; init; }
        public int TableChunkSize { get; init; }
        public long DataOffset { get; init; }
        public int DetectedDataAlignment { get; init; }
        public string DirectoryEntriesCompression { get; init; } = string.Empty;
        public string DirectoryHashesCompression { get; init; } = string.Empty;
        public string FileEntriesCompression { get; init; } = string.Empty;
        public string NameTableCompression { get; init; } = string.Empty;
        public bool HasOriginalRetailMetadata { get; init; }
    }

    /// <summary>
    /// Cross-platform, non-destructive Level-5 3DS <c>ARC0</c> (<c>ie_a.fa</c>) reader and bit-preserving patch writer.
    /// <para>
    /// Why this exists instead of <c>StudioElevenLib.Level5.Archive.ARC0.ARC0Writer</c>:
    /// <list type="number">
    ///   <item><c>StudioElevenLib.ARC0Writer</c> writes <c>N</c> <c>directoryHashes</c> (including <c>0xFFFFFFFF</c> for root)
    ///         sorted by own <c>Crc32</c> instead of the retail <c>N - 1</c> non-root directory hash table.</item>
    ///   <item><c>StudioElevenLib.ARC0Writer</c> compresses metadata tables with <c>Huffman4</c>/<c>Huffman8</c>/<c>RLE</c>
    ///         via <c>Compressor.Compress()</c>, whereas 3DS <c>CArchiveArc0</c> uses <c>NoCompression</c> (<c>0</c>) or <c>LZ10</c> (<c>1</c>).</item>
    ///   <item><c>StudioElevenLib.ARC0Writer</c> discards original file alignment and omits trailing alignment padding.</item>
    /// </list>
    /// When saving an existing retail <c>ie_a.fa</c> where only internal file contents changed,
    /// <see cref="SaveCopyTo"/> preserves the exact original <c>DirectoryEntries</c>, <c>DirectoryHashes</c>,
    /// <c>NameTable</c>, <c>FileEntries</c> ordering/CRC32s, header flags, table compression type, and file alignment,
    /// streaming untouched files verbatim from the source stream.
    /// </para>
    /// </summary>
    public static class Arc0SafeArchive
    {
        public const uint Arc0Magic = 0x30435241; // "ARC0"

        private static readonly ConditionalWeakTable<ARC0, Arc0OriginalLayout> OriginalLayouts = new();

        private sealed class Arc0IndexedFileSlot
        {
            public int FileIndex { get; init; }
            public string VirtualPath { get; init; } = string.Empty;
            public ARC0Support.FileEntry OriginalEntry { get; init; }
            public long OriginalAbsoluteOffset { get; init; }
        }

        private sealed class Arc0OriginalLayout
        {
            public long OriginalStreamLength { get; init; }
            public ARC0Support.Header Header { get; init; }
            public byte[] RawDirEntriesCompressed { get; init; } = Array.Empty<byte>();
            public byte[] RawDirHashesCompressed { get; init; } = Array.Empty<byte>();
            public byte[] RawFileEntriesCompressed { get; init; } = Array.Empty<byte>();
            public byte[] RawNameTableCompressed { get; init; } = Array.Empty<byte>();
            public int DecompressedNameTableLength { get; init; }
            public int DirEntriesCompressionMethod { get; init; }
            public int DirHashesCompressionMethod { get; init; }
            public int FileEntriesCompressionMethod { get; init; }
            public int NameTableCompressionMethod { get; init; }
            public int DetectedDataAlignment { get; init; }
            public ARC0Support.DirectoryEntry[] DirectoryEntries { get; init; } = Array.Empty<ARC0Support.DirectoryEntry>();
            public ARC0Support.FileEntry[] FileEntries { get; init; } = Array.Empty<ARC0Support.FileEntry>();
            public Arc0IndexedFileSlot[] FileSlots { get; init; } = Array.Empty<Arc0IndexedFileSlot>();
            public HashSet<string> KnownVirtualPaths { get; init; } = new(StringComparer.Ordinal);
        }

        public static ARC0 OpenReadOnly(string archiveFilePath)
        {
            if (string.IsNullOrWhiteSpace(archiveFilePath))
                throw new ArgumentException("Archive path cannot be empty.", nameof(archiveFilePath));
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException("ARC0 archive file not found.", archiveFilePath);

            var stream = new FileStream(
                archiveFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                FileOptions.RandomAccess);

            try
            {
                return OpenFromStream(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public static ARC0 OpenFromStream(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("ARC0 stream must be readable and seekable.", nameof(stream));

            EncodingInitializer.EnsureRegistered();
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");

            var data = new BinaryDataReader(stream);
            data.Seek(0);
            var header = data.ReadStruct<ARC0Support.Header>();
            if (header.Magic != Arc0Magic)
            {
                throw new InvalidDataException(
                    $"Invalid ARC0 header magic 0x{header.Magic:X8} (expected 0x{Arc0Magic:X8} 'ARC0').");
            }

            data.Seek((uint)header.DirectoryEntriesOffset);
            byte[] dirEntriesComp = data.GetSection(header.DirectoryHashOffset - header.DirectoryEntriesOffset);
            var dirEntries = DecompressStructs<ARC0Support.DirectoryEntry>(dirEntriesComp, header.DirectoryEntriesCount);

            data.Seek((uint)header.DirectoryHashOffset);
            byte[] dirHashesComp = data.GetSection(header.FileEntriesOffset - header.DirectoryHashOffset);

            data.Seek((uint)header.FileEntriesOffset);
            byte[] fileEntriesComp = data.GetSection(header.NameOffset - header.FileEntriesOffset);
            var fileEntries = DecompressStructs<ARC0Support.FileEntry>(fileEntriesComp, header.FileEntriesCount);

            data.Seek((uint)header.NameOffset);
            byte[] nameTableComp = data.GetSection(header.DataOffset - header.NameOffset);
            byte[] nameTableBytes = Compressor.Decompress(nameTableComp);
            var nameReader = new BinaryDataReader(nameTableBytes);

            var root = new VirtualDirectory("");
            var slots = new Arc0IndexedFileSlot[fileEntries.Length];
            var knownPaths = new HashSet<string>(fileEntries.Length, StringComparer.Ordinal);

            foreach (var dirEntry in dirEntries)
            {
                nameReader.Seek((uint)dirEntry.DirectoryNameStartOffset);
                string rawDirName = nameReader.ReadString(shiftJis) ?? string.Empty;
                VirtualDirectory targetDir = EnsureDirectoryPath(root, rawDirName);
                string normFolder = NormalizeFolderPrefix(rawDirName);

                int firstIdx = dirEntry.FirstFileIndex;
                int endIdx = Math.Min(fileEntries.Length, firstIdx + dirEntry.FileCount);
                for (int idx = firstIdx; idx < endIdx; idx++)
                {
                    var file = fileEntries[idx];
                    nameReader.Seek((uint)(dirEntry.FileNameStartOffset + file.NameOffsetInFolder));
                    string fileName = nameReader.ReadString(shiftJis);
                    long absOffset = (long)header.DataOffset + file.FileOffset;

                    var subStream = new SubMemoryStream(stream, absOffset, file.FileSize);
                    targetDir.AddFile(fileName, subStream);

                    string virtualPath = normFolder + fileName;
                    slots[idx] = new Arc0IndexedFileSlot
                    {
                        FileIndex = idx,
                        VirtualPath = virtualPath,
                        OriginalEntry = file,
                        OriginalAbsoluteOffset = absOffset
                    };
                    knownPaths.Add(virtualPath);
                }
            }

            root.SortAlphabetically();

            var archive = new ARC0
            {
                Directory = root
            };

            typeof(ARC0).GetProperty(nameof(ARC0.BaseStream))?.SetValue(archive, stream);
            typeof(ARC0).GetProperty(nameof(ARC0.Header))?.SetValue(archive, header);

            int detectedAlign = DetectArchiveAlignment(header, fileEntries);
            var layout = new Arc0OriginalLayout
            {
                OriginalStreamLength = stream.Length,
                Header = header,
                RawDirEntriesCompressed = dirEntriesComp,
                RawDirHashesCompressed = dirHashesComp,
                RawFileEntriesCompressed = fileEntriesComp,
                RawNameTableCompressed = nameTableComp,
                DecompressedNameTableLength = nameTableBytes.Length,
                DirEntriesCompressionMethod = ReadCompressionMethodId(dirEntriesComp),
                DirHashesCompressionMethod = ReadCompressionMethodId(dirHashesComp),
                FileEntriesCompressionMethod = ReadCompressionMethodId(fileEntriesComp),
                NameTableCompressionMethod = ReadCompressionMethodId(nameTableComp),
                DetectedDataAlignment = detectedAlign,
                DirectoryEntries = dirEntries,
                FileEntries = fileEntries,
                FileSlots = slots,
                KnownVirtualPaths = knownPaths
            };

            OriginalLayouts.AddOrUpdate(archive, layout);
            return archive;
        }

        public static Arc0ArchiveInspection InspectArchive(ARC0 archive)
        {
            if (archive == null)
                throw new ArgumentNullException(nameof(archive));

            if (OriginalLayouts.TryGetValue(archive, out Arc0OriginalLayout layout))
            {
                return new Arc0ArchiveInspection
                {
                    ArchiveSizeBytes = layout.OriginalStreamLength,
                    Magic = layout.Header.Magic,
                    DirectoryEntriesCount = layout.Header.DirectoryEntriesCount,
                    DirectoryHashesCount = layout.Header.DirectoryHashCount,
                    FileEntriesCount = layout.Header.FileEntriesCount,
                    TableChunkSize = layout.Header.TableChunkSize,
                    DataOffset = layout.Header.DataOffset,
                    DetectedDataAlignment = layout.DetectedDataAlignment,
                    DirectoryEntriesCompression = DescribeCompressionMethod(layout.DirEntriesCompressionMethod),
                    DirectoryHashesCompression = DescribeCompressionMethod(layout.DirHashesCompressionMethod),
                    FileEntriesCompression = DescribeCompressionMethod(layout.FileEntriesCompressionMethod),
                    NameTableCompression = DescribeCompressionMethod(layout.NameTableCompressionMethod),
                    HasOriginalRetailMetadata = true
                };
            }

            int totalFolders = CountAllFolders(archive.Directory);
            int totalFiles = CountAllFiles(archive.Directory);
            return new Arc0ArchiveInspection
            {
                ArchiveSizeBytes = archive.BaseStream?.Length ?? 0,
                Magic = archive.Header.Magic != 0 ? archive.Header.Magic : Arc0Magic,
                DirectoryEntriesCount = totalFolders,
                DirectoryHashesCount = Math.Max(0, totalFolders - 1),
                FileEntriesCount = totalFiles,
                TableChunkSize = archive.Header.TableChunkSize,
                DataOffset = archive.Header.DataOffset,
                DetectedDataAlignment = 4,
                DirectoryEntriesCompression = "NoCompression",
                DirectoryHashesCompression = "NoCompression",
                FileEntriesCompression = "NoCompression",
                NameTableCompression = "NoCompression",
                HasOriginalRetailMetadata = false
            };
        }

        /// <summary>
        /// Saves a modified <see cref="ARC0"/> archive to a brand-new output file, strictly refusing
        /// to overwrite <paramref name="protectedSourcePath"/>.
        /// </summary>
        public static Arc0SaveSummary SaveCopyTo(
            ARC0 archive,
            string destinationFilePath,
            string protectedSourcePath,
            IProgress<int> progress = null)
        {
            if (archive == null)
                throw new ArgumentNullException(nameof(archive));
            if (string.IsNullOrWhiteSpace(destinationFilePath))
                throw new ArgumentException("Destination path cannot be empty.", nameof(destinationFilePath));

            string fullDest = Path.GetFullPath(destinationFilePath);
            if (!string.IsNullOrWhiteSpace(protectedSourcePath))
            {
                string fullSource = Path.GetFullPath(protectedSourcePath);
                if (PathsEqual(fullDest, fullSource))
                {
                    throw new InvalidOperationException(
                        $"Refusing to overwrite original game file '{fullSource}'. Select a separate output directory.");
                }
            }

            string parentDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            string tempFile = fullDest + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                Arc0SaveSummary summary;
                if (OriginalLayouts.TryGetValue(archive, out Arc0OriginalLayout layout) &&
                    CanUsePreservingLayout(archive.Directory, layout, out Dictionary<string, SubMemoryStream> liveFiles))
                {
                    summary = SavePreservingRetailLayout(layout, liveFiles, tempFile, progress);
                }
                else
                {
                    summary = SaveSpecCompliantFromScratch(archive, tempFile, progress);
                }

                if (File.Exists(fullDest))
                {
                    File.Delete(fullDest);
                }

                File.Move(tempFile, fullDest);
                return summary;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* ignore cleanup error */ }
                }
            }
        }

        private static bool CanUsePreservingLayout(
            VirtualDirectory root,
            Arc0OriginalLayout layout,
            out Dictionary<string, SubMemoryStream> liveFiles)
        {
            liveFiles = new Dictionary<string, SubMemoryStream>(layout.FileSlots.Length, StringComparer.Ordinal);
            if (root == null || layout.FileSlots.Length == 0)
                return false;

            // Only preserve raw header tables if they strictly conform to 3DS CArchiveArc0 rules
            // (NoCompression 0x00 or LZ10 0x01, and DirectoryHashCount < DirectoryEntriesCount).
            // If an input archive was previously saved by StudioElevenLib (Huffman/RLE or N hashes),
            // automatically self-heal it via SaveSpecCompliantFromScratch!
            if ((layout.DirEntriesCompressionMethod != 0 && layout.DirEntriesCompressionMethod != 1) ||
                (layout.DirHashesCompressionMethod != 0 && layout.DirHashesCompressionMethod != 1) ||
                (layout.FileEntriesCompressionMethod != 0 && layout.FileEntriesCompressionMethod != 1) ||
                (layout.NameTableCompressionMethod != 0 && layout.NameTableCompressionMethod != 1))
            {
                return false;
            }

            if (layout.Header.DirectoryEntriesCount > 1 &&
                layout.Header.DirectoryHashCount >= layout.Header.DirectoryEntriesCount)
            {
                return false;
            }

            CollectAllFiles(root, "", liveFiles);
            if (liveFiles.Count != layout.FileSlots.Length)
                return false;

            for (int i = 0; i < layout.FileSlots.Length; i++)
            {
                var slot = layout.FileSlots[i];
                if (slot == null || !liveFiles.ContainsKey(slot.VirtualPath))
                    return false;
            }

            return true;
        }

        private static Arc0SaveSummary SavePreservingRetailLayout(
            Arc0OriginalLayout layout,
            Dictionary<string, SubMemoryStream> liveFiles,
            string outputFilePath,
            IProgress<int> progress)
        {
            int alignment = Math.Max(4, layout.DetectedDataAlignment);
            int fileCount = layout.FileSlots.Length;
            var updatedEntries = new ARC0Support.FileEntry[fileCount];
            var modifiedList = new List<ModifiedArchiveEntryInfo>();

            long runningDataOffset = 0;
            for (int i = 0; i < fileCount; i++)
            {
                Arc0IndexedFileSlot slot = layout.FileSlots[i];
                SubMemoryStream sub = liveFiles[slot.VirtualPath];
                ARC0Support.FileEntry entry = slot.OriginalEntry;

                long newLen = sub.ByteContent != null ? sub.ByteContent.Length : entry.FileSize;
                if (newLen < 0 || newLen > uint.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Tamaño de archivo inválido ({newLen}) para '{slot.VirtualPath}'.");
                }

                if (runningDataOffset > uint.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"El desplazamiento acumulado excede el límite de 4 GiB de ARC0 en '{slot.VirtualPath}'.");
                }

                if (sub.ByteContent != null)
                {
                    modifiedList.Add(new ModifiedArchiveEntryInfo
                    {
                        VirtualPath = slot.VirtualPath,
                        OriginalSizeBytes = entry.FileSize,
                        NewSizeBytes = newLen
                    });
                }

                entry.FileOffset = (uint)runningDataOffset;
                entry.FileSize = (uint)newLen;
                updatedEntries[i] = entry;

                runningDataOffset = AlignUp(runningDataOffset + newLen, alignment);
            }

            // Preserve exact retail compressed tables for DirectoryEntries, DirectoryHashes, and NameTable!
            byte[] dirEntriesComp = TrimTrailingPaddingIfNeeded(layout.RawDirEntriesCompressed);
            byte[] dirHashesComp = TrimTrailingPaddingIfNeeded(layout.RawDirHashesCompressed);
            byte[] nameTableComp = TrimTrailingPaddingIfNeeded(layout.RawNameTableCompressed);

            byte[] rawUpdatedFileEntries = SerializeStructArray(updatedEntries);
            byte[] fileEntriesComp = CompressTableSafeFor3ds(
                rawUpdatedFileEntries,
                layout.FileEntriesCompressionMethod,
                out int actualFileEntriesMethod);

            using var outStream = new FileStream(
                outputFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 262144);
            using var writer = new BinaryDataWriter(outStream);

            ARC0Support.Header header = layout.Header;
            header.Magic = Arc0Magic;

            writer.Seek(0x48); //sizeof(ARC0Support.Header) == 0x48 (72 bytes)
            int headerEnd = Marshal.SizeOf<ARC0Support.Header>();
            writer.Seek((uint)headerEnd);

            header.DirectoryEntriesOffset = (int)writer.Position;
            writer.Write(dirEntriesComp);
            writer.WriteAlignment(4);

            header.DirectoryHashOffset = (int)writer.Position;
            writer.Write(dirHashesComp);
            writer.WriteAlignment(4);

            header.FileEntriesOffset = (int)writer.Position;
            writer.Write(fileEntriesComp);
            writer.WriteAlignment(4);

            header.NameOffset = (int)writer.Position;
            writer.Write(nameTableComp);
            writer.WriteAlignment(alignment);

            header.DataOffset = (int)writer.Position;

            // CRITICAL FOR REAL NINTENDO 2DS / 3DS HARDWARE (CArchiveArc0 ARM11 allocator):
            // TableChunkSize is the DECOMPRESSED heap allocation size for DirectoryEntries + DirectoryHashes +
            // FileEntries + NameTable (+ 0x20 alignment slack), NOT the LZ10-compressed size on disk!
            // Shrinking TableChunkSize to (DataOffset - headerEnd) when LZ10 is active overflows the ARM11 heap on boot.
            int decompressedTableChunkSize = ((layout.DirectoryEntries.Length * 20 +
                                               header.DirectoryHashCount * 4 +
                                               fileCount * 16 +
                                               layout.DecompressedNameTableLength +
                                               0x20 + 3) & ~3);
            header.TableChunkSize = Math.Max(layout.Header.TableChunkSize, decompressedTableChunkSize);

            byte[] copyBuffer = new byte[65536];
            byte[] zeroPad = new byte[alignment];
            int lastReportedPct = -1;

            for (int i = 0; i < fileCount; i++)
            {
                Arc0IndexedFileSlot slot = layout.FileSlots[i];
                SubMemoryStream sub = liveFiles[slot.VirtualPath];
                uint expectedRelOffset = updatedEntries[i].FileOffset;
                long expectedAbsPos = (long)header.DataOffset + expectedRelOffset;

                if (outStream.Position != expectedAbsPos)
                {
                    throw new InvalidOperationException(
                        $"Desalineación interna al escribir ARC0 en '{slot.VirtualPath}': pos={outStream.Position}, esperado={expectedAbsPos}.");
                }

                WriteSubMemoryStreamContent(sub, slot.OriginalAbsoluteOffset, slot.OriginalEntry.FileSize, outStream, copyBuffer);

                long mod = outStream.Position % alignment;
                if (mod != 0)
                {
                    int padBytes = (int)(alignment - mod);
                    outStream.Write(zeroPad, 0, padBytes);
                }

                if (progress != null && fileCount > 0)
                {
                    int pct = (int)((i + 1L) * 100L / fileCount);
                    if (pct != lastReportedPct)
                    {
                        lastReportedPct = pct;
                        progress.Report(pct);
                    }
                }
            }

            long finalSize = outStream.Position;
            writer.Seek(0);
            writer.WriteStruct(header);
            outStream.Flush();

            return new Arc0SaveSummary
            {
                PreservedRetailTables = true,
                OriginalArchiveSizeBytes = layout.OriginalStreamLength,
                WrittenArchiveSizeBytes = finalSize,
                DetectedDataAlignment = alignment,
                DirectoryEntriesCount = header.DirectoryEntriesCount,
                DirectoryHashesCount = header.DirectoryHashCount,
                FileEntriesCount = header.FileEntriesCount,
                DirectoryEntriesCompression = DescribeCompressionMethod(layout.DirEntriesCompressionMethod),
                DirectoryHashesCompression = DescribeCompressionMethod(layout.DirHashesCompressionMethod),
                FileEntriesCompression = DescribeCompressionMethod(actualFileEntriesMethod),
                NameTableCompression = DescribeCompressionMethod(layout.NameTableCompressionMethod),
                ModifiedFiles = modifiedList
            };
        }

        /// <summary>
        /// Spec-compliant ARC0 builder used when building brand-new archives from scratch (e.g. unit tests
        /// or mods that inject newly created virtual files). Uses Shift-JIS CRC32, excludes <c>0xFFFFFFFF</c>
        /// from <c>DirectoryHashes</c>, groups child hashes per parent folder, uses 3DS-safe <c>NoCompression</c>
        /// on all 4 metadata tables, and pads every file (including the last) to 4-byte alignment.
        /// </summary>
        public static Arc0SaveSummary SaveSpecCompliantFromScratch(
            ARC0 archive,
            string outputFilePath,
            IProgress<int> progress = null)
        {
            EncodingInitializer.EnsureRegistered();
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");

            var folderMap = new List<(string PathWithSlash, VirtualDirectory Dir)>();
            CollectFoldersRecursive(archive.Directory, "", folderMap);

            using var nameStream = new MemoryStream();
            using var nameWriter = new BinaryDataWriter(nameStream);

            var tempDirs = new List<(
                string PathKey,
                string ParentKey,
                ARC0Support.DirectoryEntry Entry,
                List<(string FileName, SubMemoryStream Stream, ARC0Support.FileEntry FileRec)> Files)>();

            foreach (var (pathKey, dir) in folderMap)
            {
                string trimmedNoLeading = pathKey.TrimStart('/');
                byte[] dirNameBytes = shiftJis.GetBytes(trimmedNoLeading);
                uint dirCrc = string.IsNullOrEmpty(trimmedNoLeading)
                    ? 0xFFFFFFFFu
                    : Crc32.Compute(dirNameBytes);

                int dirNameOffset = (int)nameStream.Position;
                nameWriter.Write(dirNameBytes);
                nameWriter.Write((byte)0);

                int fileNameStartOffset = (int)nameStream.Position;
                var sortedFiles = dir.Files
                    .Select(kvp =>
                    {
                        byte[] fBytes = shiftJis.GetBytes(kvp.Key);
                        uint fCrc = Crc32.Compute(fBytes);
                        return (Name: kvp.Key, NameBytes: fBytes, Crc: fCrc, Stream: kvp.Value);
                    })
                    .OrderBy(x => x.Crc)
                    .ToList();

                var fileTuples = new List<(string FileName, SubMemoryStream Stream, ARC0Support.FileEntry FileRec)>(sortedFiles.Count);
                int nameOffsetInFolder = 0;
                foreach (var f in sortedFiles)
                {
                    nameWriter.Write(f.NameBytes);
                    nameWriter.Write((byte)0);

                    long fSize = f.Stream.ByteContent != null ? f.Stream.ByteContent.Length : f.Stream.Size;
                    fileTuples.Add((f.Name, f.Stream, new ARC0Support.FileEntry
                    {
                        Crc32 = f.Crc,
                        NameOffsetInFolder = (uint)nameOffsetInFolder,
                        FileOffset = 0,
                        FileSize = (uint)fSize
                    }));

                    nameOffsetInFolder += f.NameBytes.Length + 1;
                }

                string parentKey = GetParentFolderKey(pathKey);
                var dEntry = new ARC0Support.DirectoryEntry
                {
                    Crc32 = dirCrc,
                    DirectoryCount = (short)dir.Folders.Count,
                    FirstDirectoryIndex = 0,
                    FileCount = (short)sortedFiles.Count,
                    FirstFileIndex = 0,
                    DirectoryNameStartOffset = dirNameOffset,
                    FileNameStartOffset = fileNameStartOffset
                };

                tempDirs.Add((pathKey, parentKey, dEntry, fileTuples));
            }

            // Sort directories by CRC32 ascending (matching Level-5 ARC0 convention; 0xFFFFFFFF root sits at end)
            var orderedDirs = tempDirs.OrderBy(d => d.Entry.Crc32).ToList();

            // Group direct child hashes per parent directory so [FirstDirectoryIndex .. +DirectoryCount]
            // slices the exact child CRC32s of each parent folder (total N - 1 hashes, excluding root 0xFFFFFFFF)
            var dirHashes = new List<uint>(Math.Max(0, orderedDirs.Count - 1));
            var orderedFileStreams = new List<(string VirtualPath, SubMemoryStream Stream)>();
            var finalFileEntries = new List<ARC0Support.FileEntry>();
            var finalDirEntries = new ARC0Support.DirectoryEntry[orderedDirs.Count];
            var modifiedFiles = new List<ModifiedArchiveEntryInfo>();

            long runningDataOffset = 0;
            for (int i = 0; i < orderedDirs.Count; i++)
            {
                var item = orderedDirs[i];
                ARC0Support.DirectoryEntry d = item.Entry;

                var children = orderedDirs
                    .Where(c => c.Entry.Crc32 != 0xFFFFFFFFu &&
                                string.Equals(c.ParentKey, item.PathKey, StringComparison.Ordinal))
                    .OrderBy(c => c.Entry.Crc32)
                    .ToList();

                d.DirectoryCount = (short)children.Count;
                d.FirstDirectoryIndex = (ushort)dirHashes.Count;
                foreach (var child in children)
                {
                    dirHashes.Add(child.Entry.Crc32);
                }

                d.FirstFileIndex = (ushort)finalFileEntries.Count;
                d.FileCount = (short)item.Files.Count;

                foreach (var f in item.Files)
                {
                    ARC0Support.FileEntry fe = f.FileRec;
                    fe.FileOffset = (uint)runningDataOffset;
                    finalFileEntries.Add(fe);

                    string vPath = (item.PathKey == "/" ? "/" : "/" + item.PathKey.Trim('/')) +
                                   (item.PathKey == "/" ? f.FileName : "/" + f.FileName);
                    orderedFileStreams.Add((vPath, f.Stream));

                    if (f.Stream.ByteContent != null)
                    {
                        modifiedFiles.Add(new ModifiedArchiveEntryInfo
                        {
                            VirtualPath = vPath,
                            OriginalSizeBytes = f.Stream.Size,
                            NewSizeBytes = f.Stream.ByteContent.Length
                        });
                    }

                    runningDataOffset = AlignUp(runningDataOffset + fe.FileSize, 4);
                }

                finalDirEntries[i] = d;
            }

            var noComp = new NoCompression();
            byte[] dirEntriesComp = noComp.Compress(SerializeStructArray(finalDirEntries));
            byte[] dirHashesComp = noComp.Compress(SerializeStructArray(dirHashes.ToArray()));
            byte[] fileEntriesComp = noComp.Compress(SerializeStructArray(finalFileEntries.ToArray()));
            byte[] nameTableComp = noComp.Compress(nameStream.ToArray());

            using var outStream = new FileStream(
                outputFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 262144);
            using var writer = new BinaryDataWriter(outStream);

            int headerSize = Marshal.SizeOf<ARC0Support.Header>();
            writer.Seek((uint)headerSize);

            ARC0Support.Header header = archive.Header;
            header.Magic = Arc0Magic;
            header.DirectoryEntriesCount = (short)finalDirEntries.Length;
            header.DirectoryHashCount = (short)dirHashes.Count;
            header.FileEntriesCount = finalFileEntries.Count;
            header.DirectoryCount = finalDirEntries.Length;
            header.FileCount = finalFileEntries.Count;

            header.DirectoryEntriesOffset = (int)writer.Position;
            writer.Write(dirEntriesComp);
            writer.WriteAlignment(4);

            header.DirectoryHashOffset = (int)writer.Position;
            writer.Write(dirHashesComp);
            writer.WriteAlignment(4);

            header.FileEntriesOffset = (int)writer.Position;
            writer.Write(fileEntriesComp);
            writer.WriteAlignment(4);

            header.NameOffset = (int)writer.Position;
            writer.Write(nameTableComp);
            writer.WriteAlignment(4);

            header.DataOffset = (int)writer.Position;
            header.TableChunkSize = ((finalDirEntries.Length * 20 +
                                      dirHashes.Count * 4 +
                                      finalFileEntries.Count * 16 +
                                      (int)nameStream.Length + 0x20 + 3) & ~3);

            byte[] copyBuf = new byte[65536];
            byte[] pad4 = new byte[4];
            for (int i = 0; i < orderedFileStreams.Count; i++)
            {
                SubMemoryStream sub = orderedFileStreams[i].Stream;
                WriteSubMemoryStreamContent(sub, sub.Offset, sub.Size, outStream, copyBuf);

                long mod = outStream.Position % 4;
                if (mod != 0)
                {
                    outStream.Write(pad4, 0, (int)(4 - mod));
                }

                progress?.Report((int)((i + 1L) * 100L / Math.Max(1, orderedFileStreams.Count)));
            }

            long finalLen = outStream.Position;
            writer.Seek(0);
            writer.WriteStruct(header);
            outStream.Flush();

            return new Arc0SaveSummary
            {
                PreservedRetailTables = false,
                OriginalArchiveSizeBytes = archive.BaseStream?.Length ?? 0,
                WrittenArchiveSizeBytes = finalLen,
                DetectedDataAlignment = 4,
                DirectoryEntriesCount = header.DirectoryEntriesCount,
                DirectoryHashesCount = header.DirectoryHashCount,
                FileEntriesCount = header.FileEntriesCount,
                DirectoryEntriesCompression = "NoCompression",
                DirectoryHashesCompression = "NoCompression",
                FileEntriesCompression = "NoCompression",
                NameTableCompression = "NoCompression",
                ModifiedFiles = modifiedFiles
            };
        }

        private static void WriteSubMemoryStreamContent(
            SubMemoryStream sub,
            long fallbackBaseOffset,
            long fallbackBaseLength,
            Stream destination,
            byte[] scratchBuffer)
        {
            if (sub.ByteContent != null)
            {
                destination.Write(sub.ByteContent, 0, sub.ByteContent.Length);
                return;
            }

            if (sub.BaseStream == null)
                throw new InvalidOperationException("SubMemoryStream has neither ByteContent nor BaseStream.");

            sub.BaseStream.Seek(fallbackBaseOffset, SeekOrigin.Begin);
            long remaining = fallbackBaseLength;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(scratchBuffer.Length, remaining);
                int read = sub.BaseStream.Read(scratchBuffer, 0, toRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"Fin inesperado del stream ARC0 de origen (faltaban {remaining} bytes).");
                }
                destination.Write(scratchBuffer, 0, read);
                remaining -= read;
            }
        }

        public static void SetSubStreamContent(SubMemoryStream subStream, byte[] newBytes)
        {
            if (subStream == null)
                throw new ArgumentNullException(nameof(subStream));
            if (newBytes == null)
                throw new ArgumentNullException(nameof(newBytes));

            // Detach BaseStream and reset Offset so StudioElevenLib's lazy Read()/CopyTo()
            // never overwrites ByteContent from the original backing FileStream.
            subStream.ByteContent = newBytes;
            subStream.Size = newBytes.Length;
            subStream.Offset = 0;
            subStream.BaseStream = null;
        }

        public static void UpdateFileInDirectory(VirtualDirectory dir, string fileName, byte[] content)
        {
            if (dir == null)
                throw new ArgumentNullException(nameof(dir));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("File name cannot be empty.", nameof(fileName));
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            string clean = fileName.TrimStart('/');
            if (dir.Files.TryGetValue(clean, out SubMemoryStream existing) && existing != null)
            {
                SetSubStreamContent(existing, content);
            }
            else
            {
                dir.AddFile(clean, new SubMemoryStream(content));
            }
        }

        public static VirtualDirectory EnsureDirectoryPath(VirtualDirectory root, string relativePath)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == "/")
                return root;

            string[] segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            VirtualDirectory current = root;
            foreach (string segment in segments)
            {
                VirtualDirectory next = current.GetFolder(segment);
                if (next == null)
                {
                    next = new VirtualDirectory(segment);
                    current.AddFolder(next);
                }
                current = next;
            }
            return current;
        }

        public static byte[] TryReadFileFromPath(VirtualDirectory root, string fullVirtualPath)
        {
            if (root == null || string.IsNullOrWhiteSpace(fullVirtualPath))
                return null;

            string normalized = fullVirtualPath.Replace('\\', '/');
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
                normalized = "/" + normalized;

            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            VirtualDirectory current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = current.GetFolder(parts[i]);
                if (current == null)
                    return null;
            }

            string fileName = parts[^1];
            if (!current.Files.TryGetValue(fileName, out SubMemoryStream sub) || sub == null)
                return null;

            if (sub.ByteContent != null)
                return sub.ByteContent;

            sub.Read();
            return sub.ByteContent;
        }

        public static void WriteFileAtPath(VirtualDirectory root, string fullVirtualPath, byte[] content)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrWhiteSpace(fullVirtualPath))
                throw new ArgumentException("Virtual path cannot be empty.", nameof(fullVirtualPath));
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            string normalized = fullVirtualPath.Replace('\\', '/');
            int lastSlash = normalized.LastIndexOf('/');
            string folderPart = lastSlash > 0 ? normalized.Substring(0, lastSlash) : string.Empty;
            string fileName = lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;

            VirtualDirectory dir = EnsureDirectoryPath(root, folderPart);
            UpdateFileInDirectory(dir, fileName, content);
        }

        public static bool PathsEqual(string pathA, string pathB)
        {
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison);
        }

        private static byte[] CompressTableSafeFor3ds(byte[] rawBytes, int preferredMethod, out int usedMethod)
        {
            // Method 1 = LZ10; Method 0 = NoCompression. Never emit Huffman4/Huffman8/RLE on ARC0 header tables.
            if (preferredMethod == 1)
            {
                byte[] lz = new LZ10().Compress(rawBytes);
                if (lz.Length < rawBytes.Length + 4)
                {
                    usedMethod = 1;
                    return lz;
                }
            }

            usedMethod = 0;
            return new NoCompression().Compress(rawBytes);
        }

        private static byte[] TrimTrailingPaddingIfNeeded(byte[] sectionBytes)
        {
            if (sectionBytes == null || sectionBytes.Length < 4)
                return sectionBytes ?? Array.Empty<byte>();

            uint compHeader = BitConverter.ToUInt32(sectionBytes, 0);
            int method = (int)(compHeader & 0x7);
            int decompressedSize = (int)(compHeader >> 3);

            // If uncompressed (method 0), exact payload length is 4 + decompressedSize
            if (method == 0 && 4 + decompressedSize <= sectionBytes.Length && 4 + decompressedSize >= 4)
            {
                if (4 + decompressedSize == sectionBytes.Length)
                    return sectionBytes;

                byte[] exact = new byte[4 + decompressedSize];
                Buffer.BlockCopy(sectionBytes, 0, exact, 0, exact.Length);
                return exact;
            }

            return sectionBytes;
        }

        private static int ReadCompressionMethodId(byte[] compressedBlock)
        {
            if (compressedBlock == null || compressedBlock.Length < 4)
                return 0;
            uint hdr = BitConverter.ToUInt32(compressedBlock, 0);
            return (int)(hdr & 0x7);
        }

        public static string DescribeCompressionMethod(int methodId) => methodId switch
        {
            0 => "NoCompression",
            1 => "LZ10",
            2 => "Huffman4",
            3 => "Huffman8",
            4 => "RLE",
            _ => $"Method({methodId})"
        };

        private static int DetectArchiveAlignment(ARC0Support.Header header, ARC0Support.FileEntry[] fileEntries)
        {
            int[] candidates = { 128, 64, 32, 16, 8, 4 };
            foreach (int candidate in candidates)
            {
                if (header.DataOffset % candidate != 0)
                    continue;

                bool allAligned = true;
                int nonZeroChecked = 0;
                for (int i = 0; i < fileEntries.Length; i++)
                {
                    uint off = fileEntries[i].FileOffset;
                    if (off == 0)
                        continue;

                    nonZeroChecked++;
                    if (off % candidate != 0)
                    {
                        allAligned = false;
                        break;
                    }
                }

                if (allAligned && nonZeroChecked > 0)
                    return candidate;
            }

            return 4;
        }

        private static byte[] SerializeStructArray<T>(T[] items) where T : struct
        {
            if (items == null || items.Length == 0)
                return Array.Empty<byte>();

            using var ms = new MemoryStream(items.Length * Marshal.SizeOf<T>());
            using var bw = new BinaryDataWriter(ms);
            bw.WriteMultipleStruct(items);
            return ms.ToArray();
        }

        private static T[] DecompressStructs<T>(byte[] compressedBlock, int count)
        {
            if (count <= 0)
                return Array.Empty<T>();
            byte[] decompressed = Compressor.Decompress(compressedBlock);
            var reader = new BinaryDataReader(decompressed);
            return reader.ReadMultipleStruct<T>(count);
        }

        private static string NormalizeFolderPrefix(string rawDirName)
        {
            if (string.IsNullOrWhiteSpace(rawDirName) || rawDirName == "/")
                return "/";
            string clean = rawDirName.Replace('\\', '/').Trim('/');
            return "/" + clean + "/";
        }

        private static void CollectAllFiles(
            VirtualDirectory dir,
            string currentPathPrefix,
            Dictionary<string, SubMemoryStream> target)
        {
            string prefix = string.IsNullOrEmpty(currentPathPrefix) ? "/" : currentPathPrefix + "/";
            foreach (var kvp in dir.Files)
            {
                target[prefix + kvp.Key] = kvp.Value;
            }

            foreach (VirtualDirectory sub in dir.Folders)
            {
                string nextPrefix = string.IsNullOrEmpty(currentPathPrefix)
                    ? "/" + sub.Name
                    : currentPathPrefix + "/" + sub.Name;
                CollectAllFiles(sub, nextPrefix, target);
            }
        }

        private static void CollectFoldersRecursive(
            VirtualDirectory dir,
            string currentKey,
            List<(string PathWithSlash, VirtualDirectory Dir)> target)
        {
            string key = string.IsNullOrEmpty(currentKey) ? "/" : currentKey + "/";
            target.Add((key, dir));
            foreach (VirtualDirectory child in dir.Folders.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string childKey = string.IsNullOrEmpty(currentKey)
                    ? child.Name
                    : currentKey + "/" + child.Name;
                CollectFoldersRecursive(child, childKey, target);
            }
        }

        private static string GetParentFolderKey(string pathKey)
        {
            if (pathKey == "/")
                return string.Empty;
            string trimmed = pathKey.Trim('/');
            int lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash < 0)
                return "/";
            return trimmed.Substring(0, lastSlash) + "/";
        }

        private static int CountAllFolders(VirtualDirectory dir)
        {
            if (dir == null) return 0;
            int count = 1;
            foreach (var f in dir.Folders)
                count += CountAllFolders(f);
            return count;
        }

        private static int CountAllFiles(VirtualDirectory dir)
        {
            if (dir == null) return 0;
            int count = dir.Files.Count;
            foreach (var f in dir.Folders)
                count += CountAllFiles(f);
            return count;
        }

        private static long AlignUp(long value, int alignment)
        {
            long rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }

        private static int AlignUpInt(int value, int alignment)
        {
            int rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }
    }
}
