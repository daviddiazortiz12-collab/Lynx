using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Tools;

namespace Lynx.Core.RomFs
{
    /// <summary>
    /// Cross-platform, non-destructive ARC0 archive reader and copy-only writer.
    /// Opens source archives strictly with <see cref="FileAccess.Read"/> and avoids the Windows-only
    /// backslash path assumption inside <c>StudioElevenLib.Tools.VirtualDirectory.Reorganize()</c>
    /// so archives open identically on Windows, Linux, and macOS.
    /// </summary>
    public static class Arc0SafeArchive
    {
        public const uint Arc0Magic = 0x30435241; // "ARC0"

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

            data.Seek((uint)header.FileEntriesOffset);
            byte[] fileEntriesComp = data.GetSection(header.NameOffset - header.FileEntriesOffset);
            var fileEntries = DecompressStructs<ARC0Support.FileEntry>(fileEntriesComp, header.FileEntriesCount);

            data.Seek((uint)header.NameOffset);
            byte[] nameTableBytes = Compressor.Decompress(data.GetSection(header.DataOffset - header.NameOffset));
            var nameReader = new BinaryDataReader(nameTableBytes);

            // Root directory uses empty string Name so GetAllFoldersAsDictionnary() emits "/" and "/data/..."
            // exactly as ARC0Writer expects on both Windows and Linux.
            var root = new VirtualDirectory("");

            foreach (var dirEntry in dirEntries)
            {
                nameReader.Seek((uint)dirEntry.DirectoryNameStartOffset);
                string rawDirName = nameReader.ReadString(shiftJis) ?? string.Empty;
                VirtualDirectory targetDir = EnsureDirectoryPath(root, rawDirName);

                var filesInDir = fileEntries.Skip(dirEntry.FirstFileIndex).Take(dirEntry.FileCount);
                foreach (var file in filesInDir)
                {
                    nameReader.Seek((uint)(dirEntry.FileNameStartOffset + file.NameOffsetInFolder));
                    string fileName = nameReader.ReadString(shiftJis);
                    var subStream = new SubMemoryStream(stream, header.DataOffset + file.FileOffset, file.FileSize);
                    targetDir.AddFile(fileName, subStream);
                }
            }

            root.SortAlphabetically();

            // Construct ARC0 without invoking ARC0Reader.Read() (which calls VirtualDirectory.Reorganize() with '\\').
            var archive = new ARC0
            {
                Directory = root
            };

            // Preserve BaseStream and Header via reflection so ARC0.Save() and ARC0.Close() retain original metadata.
            typeof(ARC0).GetProperty(nameof(ARC0.BaseStream))?.SetValue(archive, stream);
            typeof(ARC0).GetProperty(nameof(ARC0.Header))?.SetValue(archive, header);

            return archive;
        }

        /// <summary>
        /// Saves a modified <see cref="ARC0"/> archive to a brand-new output file, strictly refusing
        /// to overwrite <paramref name="protectedSourcePath"/>.
        /// </summary>
        public static void SaveCopyTo(
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
                var writer = new ARC0Writer(archive.Directory, archive.Header);
                writer.Save(tempFile, progress);

                if (File.Exists(fullDest))
                {
                    File.Delete(fullDest);
                }

                File.Move(tempFile, fullDest);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* ignore cleanup error */ }
                }
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

            if (!root.IsFullPathExists(normalized))
                return null;

            return root.GetFileFromFullPath(normalized);
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
            dir.Files[fileName] = new SubMemoryStream(content);
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

        private static T[] DecompressStructs<T>(byte[] compressedBlock, int count)
        {
            if (count <= 0)
                return Array.Empty<T>();
            byte[] decompressed = Compressor.Decompress(compressedBlock);
            var reader = new BinaryDataReader(decompressed);
            return reader.ReadMultipleStruct<T>(count);
        }
    }
}
