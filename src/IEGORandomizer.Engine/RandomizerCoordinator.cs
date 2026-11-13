using System;
using System.IO;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Export;
using IEGORandomizer.Engine.Modules;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Games;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;
using Lynx.Core.Scripts;

namespace IEGORandomizer.Engine
{
    public sealed class RandomizerProgressStep
    {
        public int Percent { get; init; }
        public string Stage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Top-level stateless orchestrator for IEGO Randomizer.
    /// Opens the source game strictly read-only, builds the Story Protection &amp; Visual Coherence
    /// profiles, executes enabled randomizer subsystems using isolated deterministic PRNG streams,
    /// validates referential &amp; anti-softlock integrity, exports Citra / LayeredFS packages to a
    /// separate output directory, reopens the saved output archive to confirm round-trip validity,
    /// and verifies the original input file's SHA-256 digest remained untouched.
    /// </summary>
    public sealed class RandomizerCoordinator
    {
        public StoryProtectionService ProtectionService { get; } = new();

        public RandomizationExecutionReport GenerateRun(
            string inputPath,
            string outputDirectory,
            RandomizerOptions options,
            IProgress<RandomizerProgressStep> progress = null)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
                throw new ArgumentException("Debe seleccionar un archivo de entrada (.3ds, carpeta RomFS o ie_a.fa).", nameof(inputPath));
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Debe seleccionar una carpeta de salida.", nameof(outputDirectory));

            options ??= new RandomizerOptions();
            var seedEngine = new SeedEngine(options.Seed);
            options.Seed = seedEngine.CanonicalSeed;

            ReportStep(progress, 5, "Abriendo entrada en modo de solo lectura y calculando huella SHA-256...");
            using var workspace = GameWorkspace.Open(
                inputPath,
                options.Edition,
                options.PreferredLanguageCode,
                computeSourceSha256: true,
                aesKeysPath: options.AesKeysPath);

            var report = new RandomizationExecutionReport
            {
                Seed = seedEngine.CanonicalSeed,
                SeedFingerprint = seedEngine.VerificationFingerprint,
                EffectiveEdition = workspace.EffectiveEdition,
                ResolvedTitleId = workspace.ResolvedTitleId,
                BlindPlayMode = options.BlindPlayMode
            };

            ReportStep(progress, 15, "Auditando coherencia visual 3D, retratos y límites de modelos...");
            VisualCoherenceProfile visualProfile = VisualCoherenceValidator.AnalyzeArchive(workspace.Game);
            report.PlayablePoolCount = visualProfile.CoherentPlayableParamHashes.Count;
            report.QuarantinedModelsCount = visualProfile.QuarantinedEntityWarnings.Count;

            ReportStep(progress, 25, "Construyendo perfil de protección de historia, objetos clave y scripts .nutb...");
            StoryProtectionContext protection = ProtectionService.BuildContext(
                workspace.Game,
                workspace.EffectiveEdition,
                options.Protection);

            report.ProtectedStoryCharactersCount = protection.ProtectedStoryPlayerParamHashes.Count;
            report.ProtectedKeyItemsCount = protection.ProtectedItemHashes.Count;
            report.ScannedNutbScriptsCount = protection.NutbAudit.ScannedScriptCount;

            ProtectedRewardsBaseline baseline = ArchiveIntegrityValidator.CaptureBaseline(workspace.Game, protection);

            ReportStep(progress, 38, "Randomizando subsistema A: Jugadores, estadísticas, Espíritus Guerreros y PalPack...");
            PlayerRandomizerModule.Execute(
                workspace.Game,
                seedEngine,
                options.Players,
                protection,
                visualProfile,
                report);

            ReportStep(progress, 52, "Randomizando subsistema B: Supertécnicas, talentos, técnica inicial garantizada y compatibilidad de PT...");
            SkillRandomizerModule.Execute(
                workspace.Game,
                seedEngine,
                options.Skills,
                protection,
                visualProfile,
                report);

            ReportStep(progress, 62, "Randomizando subsistema C: Equipos rivales, dorsales, tácticas y pachangas...");
            TeamRandomizerModule.Execute(
                workspace.Game,
                seedEngine,
                options.Teams,
                protection,
                report);

            ReportStep(progress, 70, "Randomizando subsistema D: Cofres del mapa, cadenas de partidos y drops...");
            RewardRandomizerModule.Execute(
                workspace.Game,
                seedEngine,
                options.Rewards,
                options.Uniforms,
                protection,
                visualProfile,
                report);

            ReportStep(progress, 78, "Randomizando subsistema E: Equipaciones con validación de recursos 3D...");
            UniformRandomizerModule.Execute(
                workspace.Game,
                seedEngine,
                options.Uniforms,
                protection,
                visualProfile,
                report);

            ReportStep(progress, 82, "Randomizando subsistema F: Entrenadores, Gerentes y NPCs de historia y mapa...");
            NpcAndCoachRandomizerModule.Execute(
                workspace.Game,
                seedEngine,
                options.Npcs,
                protection,
                visualProfile,
                report);

            if ((options.Skills.Enabled && options.Skills.EnableDynamicStoryTechniqueSwap && protection.StorySkillSlotRemaps.Count > 0) ||
                (options.Npcs.Enabled && options.Npcs.RandomizeCoaches && protection.StoryDirectorRemaps.Count > 0))
            {
                ReportStep(progress, 85, "Sincronizando bytecode Squirrel (.nutb) para técnicas dinámicas de historia y entrenadores...");
                var nutbPatchSummary = NutbBytecodeCodec.PatchArchiveStorySkillsAndDirectors(
                    workspace.Game.IE_A.Directory,
                    options.Skills.Enabled && options.Skills.EnableDynamicStoryTechniqueSwap
                        ? protection.StorySkillSlotRemaps
                        : null,
                    options.Npcs.Enabled && options.Npcs.RandomizeCoaches
                        ? protection.StoryDirectorRemaps
                        : null,
                    injectTemporarySkillForBattleUse: true);

                report.PatchedNutbScriptsCount = nutbPatchSummary.PatchedFilesCount;
                report.PatchedNutbSkillCallSitesCount =
                    nutbPatchSummary.PatchedUnlockCallsCount +
                    nutbPatchSummary.PatchedBattleLearnCallsCount +
                    nutbPatchSummary.PatchedBattleForgetCallsCount +
                    nutbPatchSummary.PatchedBattleBanCallsCount +
                    nutbPatchSummary.InjectedTemporaryBattleSwapsCount;
                report.InjectedTemporaryBattleSkillNodesCount = nutbPatchSummary.InjectedTemporaryBattleSwapsCount;
            }

            // Enforce Blind-Play Mode guarantee before exporting
            if (options.BlindPlayMode)
            {
                report.SpoilerLogEntries.Clear();
            }

            ReportStep(progress, 88, "Validando integridad referencial y protección anti-softlock en memoria...");
            IntegrityValidationResult inMemoryCheck = ArchiveIntegrityValidator.Validate(
                workspace.Game,
                protection,
                baseline);

            if (!inMemoryCheck.IsValid)
            {
                throw new InvalidOperationException(
                    "La validación de integridad detectó inconsistencias y canceló la exportación:\n- " +
                    string.Join("\n- ", inMemoryCheck.Errors));
            }

            ReportStep(progress, 90, "Exportando paquetes (Standalone / Citra / LayeredFS) preservando layout retail ARC0...");
            ModPackageExporter.ExportArchives(
                workspace.Game,
                workspace.OriginalInputPath,
                outputDirectory,
                options,
                report,
                new Progress<int>(p => ReportStep(progress, 90 + (p * 5 / 100), $"Escribiendo archivo ARC0 ({p}%)...")));

            ReportStep(progress, 96, "Reabriendo archivo exportado, verificando persistencia binaria y comprobando SHA-256 original...");
            string exportedFa = report.ExportedPaths.FirstOrDefault(p => p.EndsWith("ie_a.fa", StringComparison.OrdinalIgnoreCase));
            if (exportedFa == null || !File.Exists(exportedFa))
            {
                throw new FileNotFoundException("No se encontró el archivo ie_a.fa exportado tras finalizar el proceso.");
            }

            using (var reopened = new SafeGOGame(exportedFa, options.PreferredLanguageCode))
            {
                IntegrityValidationResult diskCheck = ArchiveIntegrityValidator.Validate(reopened, protection, baseline);
                if (!diskCheck.IsValid)
                {
                    throw new InvalidOperationException(
                        "El archivo exportado falló la verificación al reabrirse:\n- " +
                        string.Join("\n- ", diskCheck.Errors));
                }

                report.ValidationMessages.AddRange(diskCheck.ChecksPassed);
            }

            report.PersistedDiffTelemetry = PostSaveArchiveVerifier.VerifyPersistedMutations(
                workspace.ActiveIeAFaPath,
                exportedFa,
                options.PreferredLanguageCode,
                report);

            report.OutputIntegrityVerified = true;
            report.ValidationMessages.Add(
                $"Persistencia binaria ARC0 verificada en disco: {report.PersistedDiffTelemetry.PersistedModifiedFilesCount} archivos internos modificados " +
                $"(jugadores={report.PersistedDiffTelemetry.PersistedChangedCharaParamsCount}, técnicas={report.PersistedDiffTelemetry.PersistedChangedSkillTableEntriesCount}, " +
                $"equipos={report.PersistedDiffTelemetry.PersistedChangedStoryTeamsCount + report.PersistedDiffTelemetry.PersistedChangedPachangaTeamsCount}, " +
                $"equipaciones/params={report.PersistedDiffTelemetry.PersistedChangedTeamParamsCount}).");

            if (!string.IsNullOrWhiteSpace(options.Export.Export2dsDeltaPatchPackPath))
            {
                ReportStep(progress, 98, "Generando paquete ligero independiente IE4P para actualizador .CIA de 2DS...");
                var deltaRes = Arc0DeltaPatchPackBuilder.BuildPack(
                    workspace.ActiveIeAFaPath,
                    exportedFa,
                    options.Export.Export2dsDeltaPatchPackPath,
                    options.Export.AssumePriorRandomizedInstallForDeltaPack);

                report.ExportedPaths.Add(deltaRes.OutputPackPath);
                report.ValidationMessages.Add(
                    $"[2DS-IE4P] Paquete delta ligero generado ({deltaRes.PackSizeBytes / (1024.0 * 1024.0):F2} MB): " +
                    $"{deltaRes.ReusedInstalledFiles} archivos reutilizados del juego instalado en 2DS ({deltaRes.ReusedInstalledBytes / (1024.0 * 1024.0):F1} MB), " +
                    $"{deltaRes.EmbeddedPatchFiles} archivos embebidos ({deltaRes.EmbeddedStoredBytes / (1024.0 * 1024.0):F2} MB comprimidos LZ4).");
            }

            report.SourceImmutabilityVerified = workspace.VerifySourceUnchanged();
            if (!report.SourceImmutabilityVerified)
            {
                throw new InvalidOperationException(
                    "ERROR CRÍTICO: La verificación SHA-256 del archivo original no coincidió.");
            }
            report.ValidationMessages.Add("Archivo original verificado intacto byte a byte (SHA-256 idéntico).");

            ModPackageExporter.WriteManifestAndLogs(outputDirectory, options, report);

            ReportStep(progress, 100, "Generación completada con éxito.");
            return report;
        }

        private static void ReportStep(IProgress<RandomizerProgressStep> progress, int pct, string stage)
        {
            progress?.Report(new RandomizerProgressStep
            {
                Percent = Math.Clamp(pct, 0, 100),
                Stage = stage
            });
        }
    }
}
