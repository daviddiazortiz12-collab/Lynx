using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Modules;
using Lynx.Core.Games;
using Lynx.Core.RomFs.Cia;

namespace IEGORandomizer.Engine.Cia
{
    public sealed class CiaMergeExecutionReport
    {
        public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
        public string SourceCiaPath { get; init; } = string.Empty;
        public string ModifiedRomFsDirectory { get; init; } = string.Empty;
        public string OutputCiaPath { get; init; } = string.Empty;
        public bool PreValidatedIeAFa { get; init; }
        public CiaRebuildTelemetry RebuildTelemetry { get; init; }
        public CiaVerificationResult Verification { get; init; }
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// High-level orchestrator for Nintendo 3DS <c>.cia</c> workflows in v1.1.0:
    /// 1) Local non-destructive <c>.cia</c> inspection &amp; ROM-free diagnostic export.
    /// 2) Main Mode: <c>Original .cia -&gt; untouched 5-module RandomizerCoordinator -&gt; new verified .cia</c>.
    /// 3) Second Mode: <c>Original .cia + pre-modified RomFS folder -&gt; new verified .cia</c> (preserving all RomFS files).
    /// </summary>
    public sealed class CiaWorkflowService
    {
        private readonly RandomizerCoordinator _coordinator;

        public CiaWorkflowService(RandomizerCoordinator coordinator = null)
        {
            _coordinator = coordinator ?? new RandomizerCoordinator();
        }

        public CiaInspectionReport InspectLocalCia(
            string ciaPath,
            string aesKeysPath = null,
            bool computeFullSha256 = false)
        {
            var keyStore = CtrKeyStore.LoadAvailableLocalKeys(aesKeysPath);
            return CiaFormatInspector.InspectFile(ciaPath, keyStore, computeFullSha256);
        }

        public void SaveRomFreeDiagnosticJson(CiaInspectionReport report, string outputJsonPath)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(outputJsonPath))
                throw new ArgumentException("Ruta de salida JSON vacía.", nameof(outputJsonPath));

            string fullOut = Path.GetFullPath(outputJsonPath);
            string dir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sanitizedPayload = new
            {
                DiagnosticSchema = "IEGORandomizer-LocalCiaDiagnostic-v1.1.0",
                InspectedAtUtc = DateTime.UtcNow,
                ContainsRomBytes = false,
                ContainsProprietaryKeys = false,
                FileNameOnly = Path.GetFileName(report.FilePath),
                report.FileSizeBytes,
                report.Sha256Hex,
                report.IsValidCiaStructure,
                EncryptionKind = report.EncryptionKind.ToString(),
                report.CanProcessNow,
                report.UsedExternalAesKeys,
                CiaLayout = new
                {
                    report.HeaderSize,
                    report.CertChainOffset,
                    report.CertChainSize,
                    report.TicketOffset,
                    report.TicketSize,
                    report.TmdOffset,
                    report.TmdSize,
                    report.ContentOffset,
                    report.ContentSize,
                    report.MetaOffset,
                    report.MetaSize
                },
                TitleMetadata = new
                {
                    report.TmdTitleIdHex,
                    report.TicketTitleIdHex,
                    report.TmdTitleVersionRaw,
                    report.TmdTitleVersionFormatted,
                    report.IsBaseRevisionV000,
                    report.SaveDataSizeBytes,
                    report.ContentCount,
                    report.TmdInternalHashesValid,
                    report.TicketCommonKeyIndex,
                    report.IsTicketPersonalized,
                    report.SmdhBannerTitle
                },
                MainExecutableNcch = new
                {
                    report.Ncch0HeaderReadable,
                    report.NcchProgramIdHex,
                    report.ProductCode,
                    DetectedEdition = report.DetectedEdition.ToString(),
                    report.NcchFormatVersion,
                    report.NcchSecondaryKeySlot,
                    report.NcchNoCryptoFlag,
                    report.NcchFixedKeyFlag,
                    report.NcchSeedCryptoFlag,
                    report.ExHeaderRemasterVersion
                },
                RomFsSummary = new
                {
                    report.RomFsReadable,
                    report.RomFsDirectoryCount,
                    report.RomFsFileCount,
                    report.HasIeAFa,
                    report.IeAFaSizeBytes,
                    report.HasFixXr,
                    report.HasBannerIcn,
                    report.RomFsSoundFilesCount,
                    report.RomFsMovieFilesCount
                },
                Contents = report.ContentChunks,
                report.Findings,
                report.LimitationsAndWarnings
            };

            string json = JsonSerializer.Serialize(sanitizedPayload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(fullOut, json);
        }

        /// <summary>
        /// Main Mode: <c>Original .cia -&gt; untouched v1.0.2 RandomizerCoordinator -&gt; new verified .cia</c>.
        /// Preserves 100% of the original <c>.cia</c>'s RomFS files, secondary contents, SaveDataSize, and SMDH metadata.
        /// </summary>
        public RandomizationExecutionReport RandomizeCiaToCia(
            string sourceCiaPath,
            string outputCiaPath,
            RandomizerOptions options,
            IProgress<RandomizerProgressStep> progress = null)
        {
            if (string.IsNullOrWhiteSpace(sourceCiaPath))
                throw new ArgumentException("Debe seleccionar el archivo .cia original.", nameof(sourceCiaPath));
            if (string.IsNullOrWhiteSpace(outputCiaPath))
                throw new ArgumentException("Debe indicar la ruta del nuevo archivo .cia de salida.", nameof(outputCiaPath));

            string fullSource = Path.GetFullPath(sourceCiaPath);
            string fullOutput = EnsureCiaExtension(Path.GetFullPath(outputCiaPath), Path.GetFileNameWithoutExtension(fullSource) + "_Randomized.cia");

            if (string.Equals(fullSource, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "El archivo .cia de salida no puede tener la misma ruta que el .cia original.");
            }

            options ??= new RandomizerOptions();
            var keyStore = CtrKeyStore.LoadAvailableLocalKeys(options.AesKeysPath);

            Report(progress, 2, "Inspeccionando .cia original en modo de solo lectura...");
            var preInspect = CiaFormatInspector.InspectFile(fullSource, keyStore, computeFullFileSha256: false);
            if (!preInspect.IsValidCiaStructure || !preInspect.CanProcessNow)
            {
                throw new InvalidOperationException(
                    preInspect.LimitationsAndWarnings.Count > 0
                        ? string.Join(" ", preInspect.LimitationsAndWarnings)
                        : "El archivo .cia original no puede procesarse.");
            }

            string tempRunDir = SelectStagingDirectoryWithMostSpace(fullOutput, "iego_cia_build_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRunDir);

            try
            {
                Report(progress, 3, "Calculando huella SHA-256 inicial del .cia original...");
                string sourcePreSha256 = CiaFormatInspector.ComputeStreamingFileSha256Hex(fullSource);

                // Clone options so our internal staging export uses clean Citra romfs layout without mutating caller's options
                string optJson = JsonSerializer.Serialize(options);
                var stageOptions = JsonSerializer.Deserialize<RandomizerOptions>(optJson) ?? new RandomizerOptions();
                stageOptions.Export.ExportCitraModStructure = true;
                stageOptions.Export.ExportLuma3dsLayeredFsStructure = false;
                stageOptions.Export.ExportStandaloneIeAFa = false;
                stageOptions.Export.DirectCitraModsRootPath = string.Empty;

                var engineProgress = new Progress<RandomizerProgressStep>(step =>
                {
                    int mapped = 4 + (step.Percent * 56) / 100;
                    Report(progress, mapped, step.Stage);
                });

                RandomizationExecutionReport report = _coordinator.GenerateRun(
                    fullSource,
                    tempRunDir,
                    stageOptions,
                    engineProgress);

                options.Seed = report.Seed;

                // Locate the clean staged romfs directory generated by ModPackageExporter
                string stagedIea = report.ExportedPaths.FirstOrDefault(p => p.EndsWith("ie_a.fa", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException("No se encontró el contenedor ie_a.fa generado en el área temporal.");
                string stagedRomFsDir = Path.GetDirectoryName(stagedIea)
                    ?? throw new InvalidOperationException("Carpeta temporal romfs inválida.");

                // Rebuild the complete multi-content .cia incorporating stagedRomFsDir over 100% of original RomFS
                var (telemetry, verification) = CiaPackageRebuilder.RebuildFromRomFsDirectoryOverlay(
                    fullSource,
                    stagedRomFsDir,
                    fullOutput,
                    keyStore,
                    (pct, msg) =>
                    {
                        int mapped = 60 + (pct * 38) / 100;
                        Report(progress, mapped, msg);
                    },
                    precomputedSourceSha256: sourcePreSha256);

                report.CiaBuildTelemetry = telemetry;
                report.CiaVerification = verification;
                report.ExportedPaths.Clear();
                report.ExportedPaths.Add(fullOutput);

                foreach (string check in verification.ChecksPassed)
                {
                    report.ValidationMessages.Add("[CIA] " + check);
                }

                foreach (string note in preInspect.LimitationsAndWarnings)
                {
                    if (!report.Warnings.Contains(note))
                        report.Warnings.Add(note);
                }

                // Write companion manifest and (if BlindPlayMode == false) spoiler logs next to output .cia
                string outBase = Path.Combine(
                    Path.GetDirectoryName(fullOutput) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(fullOutput));

                string companionManifest = outBase + ".cia-manifest.json";
                WriteCompanionCiaManifest(companionManifest, report);
                report.ExportedPaths.Add(companionManifest);

                if (!options.BlindPlayMode)
                {
                    string tempSpoilerTxt = Path.Combine(tempRunDir, "spoiler_log.txt");
                    if (File.Exists(tempSpoilerTxt))
                    {
                        string destTxt = outBase + ".spoiler_log.txt";
                        File.Copy(tempSpoilerTxt, destTxt, overwrite: true);
                        report.ExportedPaths.Add(destTxt);
                    }
                }

                Report(progress, 100, "Nuevo archivo .cia randomizado y verificado con éxito.");
                return report;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempRunDir))
                        Directory.Delete(tempRunDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup of temp staging directory
                }
            }
        }

        /// <summary>
        /// Second Mode: <c>Original .cia + Pre-Modified RomFS Directory -&gt; New Verified .cia</c>.
        /// Incorporates ALL files and subfolders from <paramref name="modifiedRomFsDirectory"/> (not only <c>ie_a.fa</c>)
        /// while preserving 100% of untouched files from the original <c>.cia</c>'s RomFS.
        /// </summary>
        public CiaMergeExecutionReport MergeModifiedRomFsIntoCia(
            string sourceCiaPath,
            string modifiedRomFsDirectory,
            string outputCiaPath,
            string aesKeysPath = null,
            IProgress<RandomizerProgressStep> progress = null)
        {
            if (string.IsNullOrWhiteSpace(sourceCiaPath))
                throw new ArgumentException("Debe seleccionar el archivo .cia original.", nameof(sourceCiaPath));
            if (string.IsNullOrWhiteSpace(modifiedRomFsDirectory))
                throw new ArgumentException("Debe seleccionar la carpeta RomFS modificada.", nameof(modifiedRomFsDirectory));
            if (string.IsNullOrWhiteSpace(outputCiaPath))
                throw new ArgumentException("Debe indicar la ruta del nuevo archivo .cia de salida.", nameof(outputCiaPath));

            string fullSource = Path.GetFullPath(sourceCiaPath);
            string resolvedRomFsRoot = ResolveRomFsFolderRoot(Path.GetFullPath(modifiedRomFsDirectory));
            string fullOutput = EnsureCiaExtension(
                Path.GetFullPath(outputCiaPath),
                Path.GetFileNameWithoutExtension(fullSource) + "_RomFsMerged.cia");

            if (string.Equals(fullSource, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "El archivo .cia de salida no puede coincidir con el .cia original.");
            }

            var keyStore = CtrKeyStore.LoadAvailableLocalKeys(aesKeysPath);
            var preInspect = CiaFormatInspector.InspectFile(fullSource, keyStore, computeFullFileSha256: false);
            if (!preInspect.IsValidCiaStructure || !preInspect.CanProcessNow)
            {
                throw new InvalidOperationException(
                    preInspect.LimitationsAndWarnings.Count > 0
                        ? string.Join(" ", preInspect.LimitationsAndWarnings)
                        : "El archivo .cia original no puede procesarse.");
            }

            // Pre-validate ie_a.fa inside the modified RomFS directory if present (case-insensitively on all OSes)
            bool preValidatedIea = false;
            string candidateIea = Directory.EnumerateFiles(resolvedRomFsRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => Path.GetFileName(f).Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(candidateIea) && File.Exists(candidateIea))
            {
                Report(progress, 5, "Validando integridad del contenedor ARC0 'ie_a.fa' en la carpeta RomFS modificada...");
                using var gameCheck = new SafeGOGame(candidateIea, "es");
                var players = gameCheck.GetCharaparams();
                var skills = gameCheck.GetSkillConfigs();
                if (players == null || players.Length == 0 || skills == null || skills.Length == 0)
                {
                    throw new InvalidDataException(
                        "El archivo 'ie_a.fa' de la carpeta RomFS modificada está corrupto o no contiene tablas válidas de Inazuma Eleven GO.");
                }
                preValidatedIea = true;
            }

            var (telemetry, verification) = CiaPackageRebuilder.RebuildFromRomFsDirectoryOverlay(
                fullSource,
                resolvedRomFsRoot,
                fullOutput,
                keyStore,
                (pct, msg) =>
                {
                    int mapped = 8 + (pct * 90) / 100;
                    Report(progress, mapped, msg);
                });

            var mergeReport = new CiaMergeExecutionReport
            {
                SourceCiaPath = fullSource,
                ModifiedRomFsDirectory = resolvedRomFsRoot,
                OutputCiaPath = fullOutput,
                PreValidatedIeAFa = preValidatedIea,
                RebuildTelemetry = telemetry,
                Verification = verification,
                Warnings = preInspect.LimitationsAndWarnings.ToList()
            };

            string companionManifest = Path.Combine(
                Path.GetDirectoryName(fullOutput) ?? string.Empty,
                Path.GetFileNameWithoutExtension(fullOutput) + ".cia-merge-manifest.json");

            string json = JsonSerializer.Serialize(mergeReport, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(companionManifest, json);

            Report(progress, 100, "Fusión de carpeta RomFS + .cia original completada y verificada.");
            return mergeReport;
        }

        private static string ResolveRomFsFolderRoot(string dir)
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"No existe la carpeta RomFS: {dir}");

            if (Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileName(f).Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase)))
            {
                return dir;
            }

            // Support passing Citra/Luma parent folders (e.g. output/Citra/load/mods/<TitleID>/ or output/Luma3DS/titles/<TitleID>/)
            foreach (string sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(sub).Equals("romfs", StringComparison.OrdinalIgnoreCase))
                    return sub;
            }

            string[] nestedIea = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nestedIea.Length == 1)
            {
                string parent = Path.GetDirectoryName(nestedIea[0])!;
                if (Path.GetFileName(parent).Equals("romfs", StringComparison.OrdinalIgnoreCase))
                    return parent;
            }

            return dir;
        }

        private static string SelectStagingDirectoryWithMostSpace(string fullOutputCiaPath, string tempFolderName)
        {
            string sysTempBase = Path.GetTempPath();
            string outParent = Path.GetDirectoryName(fullOutputCiaPath);

            try
            {
                long sysFree = GetDriveFreeBytes(sysTempBase);
                long outFree = !string.IsNullOrEmpty(outParent) ? GetDriveFreeBytes(outParent) : -1;

                // If system %TEMP% has < 2.5 GB free and output drive has significantly more space, stage next to output
                if (sysFree > 0 && sysFree < 2_500_000_000L && outFree > sysFree && !string.IsNullOrEmpty(outParent))
                {
                    Directory.CreateDirectory(outParent);
                    return Path.Combine(outParent, "." + tempFolderName);
                }
            }
            catch
            {
                // Fallback to Path.GetTempPath()
            }

            return Path.Combine(sysTempBase, tempFolderName);
        }

        private static long GetDriveFreeBytes(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root))
                    return -1;
                var d = new DriveInfo(root);
                return d.IsReady ? d.AvailableFreeSpace : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string EnsureCiaExtension(string outputPath, string defaultFileName)
        {
            if (Directory.Exists(outputPath) ||
                outputPath.EndsWith(Path.DirectorySeparatorChar) ||
                outputPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return Path.Combine(outputPath, defaultFileName);
            }

            if (!outputPath.EndsWith(".cia", StringComparison.OrdinalIgnoreCase))
                return outputPath + ".cia";

            return outputPath;
        }

        private static void WriteCompanionCiaManifest(string manifestPath, RandomizationExecutionReport report)
        {
            var payload = new
            {
                Generator = "IEGO Randomizer v1.1.0 (.cia Native Pipeline)",
                report.GeneratedAtUtc,
                report.Seed,
                report.SeedFingerprint,
                Edition = report.EffectiveEdition.ToString(),
                report.ResolvedTitleId,
                report.BlindPlayMode,
                report.SourceImmutabilityVerified,
                report.OutputIntegrityVerified,
                report.CiaBuildTelemetry,
                report.CiaVerification,
                report.PersistedDiffTelemetry
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(manifestPath, json);
        }

        private static void Report(IProgress<RandomizerProgressStep> progress, int pct, string stage)
        {
            progress?.Report(new RandomizerProgressStep
            {
                Percent = Math.Clamp(pct, 0, 100),
                Stage = stage
            });
        }
    }
}
