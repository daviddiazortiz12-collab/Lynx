using System;
using System.IO;
using System.Security.Cryptography;
using Lynx.Core.Games;

namespace Lynx.Core.RomFs
{
    /// <summary>
    /// Manages a strictly read-only session over any supported user input:
    /// - standalone <c>ie_a.fa</c> ARC0 file
    /// - extracted <c>RomFS</c> directory containing <c>ie_a.fa</c>
    /// - decrypted Nintendo 3DS <c>.3ds</c> / <c>.cci</c> / <c>.cxi</c> / <c>.romfs</c> image
    /// Tracks the cryptographic SHA-256 hash and file metadata of the original source so tests
    /// and runtime validation can prove that the original input was never modified.
    /// </summary>
    public sealed class GameWorkspace : IDisposable
    {
        private string _tempStagingDir;

        public string OriginalInputPath { get; }
        public string ProtectedPrimaryFilePath { get; }
        public string ActiveIeAFaPath { get; }
        public GameInputKind InputKind { get; }
        public GameEdition EffectiveEdition { get; }
        public string ResolvedTitleId { get; }
        public string ProductCode { get; }
        public string SourceSha256Hex { get; }
        public long SourceFileSizeBytes { get; }
        public SafeGOGame Game { get; }

        private GameWorkspace(
            string originalInputPath,
            string protectedPrimaryFilePath,
            string activeIeAFaPath,
            string tempStagingDir,
            GameInputKind inputKind,
            GameEdition effectiveEdition,
            string resolvedTitleId,
            string productCode,
            string sourceSha256Hex,
            long sourceFileSizeBytes,
            SafeGOGame game)
        {
            OriginalInputPath = originalInputPath;
            ProtectedPrimaryFilePath = protectedPrimaryFilePath;
            ActiveIeAFaPath = activeIeAFaPath;
            _tempStagingDir = tempStagingDir;
            InputKind = inputKind;
            EffectiveEdition = effectiveEdition;
            ResolvedTitleId = resolvedTitleId;
            ProductCode = productCode;
            SourceSha256Hex = sourceSha256Hex;
            SourceFileSizeBytes = sourceFileSizeBytes;
            Game = game;
        }

        public static GameWorkspace Open(
            string inputPath,
            GameEdition requestedEdition = GameEdition.Auto,
            string preferredLanguageCode = null,
            bool computeSourceSha256 = true)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
                throw new ArgumentException("La ruta de entrada no puede estar vacía.", nameof(inputPath));

            string fullInput = Path.GetFullPath(inputPath);

            // Case 1: Extracted RomFS directory
            if (Directory.Exists(fullInput))
            {
                string ieaCandidate = ResolveIeAFaInDirectory(fullInput);
                if (ieaCandidate == null)
                {
                    throw new FileNotFoundException(
                        $"No se encontró 'ie_a.fa' dentro de la carpeta RomFS '{fullInput}'.");
                }

                var (sha, len) = InspectFileIntegrity(ieaCandidate, computeSourceSha256);
                GameEdition hinted = TitleIdCatalog.DetectEditionFromPathHint(fullInput);
                GameEdition edition = requestedEdition != GameEdition.Auto
                    ? requestedEdition
                    : (hinted != GameEdition.Auto ? hinted : GameEdition.Light);
                string titleId = TitleIdCatalog.GetDefaultTitleId(edition);
                var game = new SafeGOGame(ieaCandidate, preferredLanguageCode);

                return new GameWorkspace(
                    fullInput,
                    ieaCandidate,
                    ieaCandidate,
                    tempStagingDir: null,
                    GameInputKind.RomFsDirectory,
                    edition,
                    titleId,
                    string.Empty,
                    sha,
                    len,
                    game);
            }

            if (!File.Exists(fullInput))
            {
                throw new FileNotFoundException($"No existe el archivo o carpeta de entrada '{fullInput}'.", fullInput);
            }

            var (sourceSha, sourceLen) = InspectFileIntegrity(fullInput, computeSourceSha256);
            RomContainerMetadata meta = Nintendo3dsRomReader.Inspect(fullInput);

            // Case 2: Direct ie_a.fa ARC0 file
            if (meta.Kind == GameInputKind.Arc0File)
            {
                GameEdition hinted = TitleIdCatalog.DetectEditionFromPathHint(fullInput);
                GameEdition edition = requestedEdition != GameEdition.Auto
                    ? requestedEdition
                    : (hinted != GameEdition.Auto ? hinted : GameEdition.Light);
                string titleId = TitleIdCatalog.GetDefaultTitleId(edition);
                var game = new SafeGOGame(fullInput, preferredLanguageCode);

                return new GameWorkspace(
                    fullInput,
                    fullInput,
                    fullInput,
                    tempStagingDir: null,
                    GameInputKind.Arc0File,
                    edition,
                    titleId,
                    string.Empty,
                    sourceSha,
                    sourceLen,
                    game);
            }

            // Case 3: .3ds / .cxi / .romfs container -> extract ie_a.fa into isolated temporary folder
            string stagingDir = Path.Combine(Path.GetTempPath(), "iego_randomizer_stage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);
            try
            {
                string stagedIeaPath = Path.Combine(stagingDir, "ie_a.fa");
                Nintendo3dsRomReader.ExtractIeAFaToFile(fullInput, stagedIeaPath);

                GameEdition edition = requestedEdition != GameEdition.Auto
                    ? requestedEdition
                    : (meta.DetectedEdition != GameEdition.Auto ? meta.DetectedEdition : GameEdition.Light);

                string titleId = !string.IsNullOrWhiteSpace(meta.TitleIdHex)
                    ? meta.TitleIdHex
                    : TitleIdCatalog.GetDefaultTitleId(edition);

                var game = new SafeGOGame(stagedIeaPath, preferredLanguageCode);
                return new GameWorkspace(
                    fullInput,
                    fullInput,
                    stagedIeaPath,
                    stagingDir,
                    meta.Kind,
                    edition,
                    titleId,
                    meta.ProductCode,
                    sourceSha,
                    sourceLen,
                    game);
            }
            catch
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Verifies that the original source file on disk has the exact same length and SHA-256 digest
        /// as when the workspace was opened.
        /// </summary>
        public bool VerifySourceUnchanged()
        {
            if (!File.Exists(ProtectedPrimaryFilePath))
                return false;

            var info = new FileInfo(ProtectedPrimaryFilePath);
            if (info.Length != SourceFileSizeBytes)
                return false;

            if (!string.IsNullOrEmpty(SourceSha256Hex))
            {
                string currentSha = ComputeSha256Hex(ProtectedPrimaryFilePath);
                return string.Equals(currentSha, SourceSha256Hex, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static string ResolveIeAFaInDirectory(string rootDir)
        {
            string direct = Path.Combine(rootDir, "ie_a.fa");
            if (File.Exists(direct))
                return direct;

            string nestedRomFs = Path.Combine(rootDir, "romfs", "ie_a.fa");
            if (File.Exists(nestedRomFs))
                return nestedRomFs;

            return null;
        }

        private static (string Sha256Hex, long Length) InspectFileIntegrity(string filePath, bool computeHash)
        {
            var info = new FileInfo(filePath);
            string sha = computeHash ? ComputeSha256Hex(filePath) : string.Empty;
            return (sha, info.Length);
        }

        public static string ComputeSha256Hex(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(fs);
            return Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            Game?.Dispose();

            if (!string.IsNullOrEmpty(_tempStagingDir) && Directory.Exists(_tempStagingDir))
            {
                try { Directory.Delete(_tempStagingDir, recursive: true); } catch { }
                _tempStagingDir = null;
            }
        }
    }
}
