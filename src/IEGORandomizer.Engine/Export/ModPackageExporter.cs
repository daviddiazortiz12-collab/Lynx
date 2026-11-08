using System;
using System.IO;
using System.Text;
using System.Text.Json;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Diagnostics;
using IEGORandomizer.Engine.Modules;
using Lynx.Core.Games;
using Lynx.Core.RomFs;

namespace IEGORandomizer.Engine.Export
{
    /// <summary>
    /// Exports the randomized <c>ie_a.fa</c> into a clean output folder without touching
    /// the original source file or interacting with physical console hardware.
    /// Supports:
    /// - Standalone <c>ie_a.fa</c>
    /// - Citra emulator mod layout (<c>Citra/load/mods/{TitleID}/romfs/ie_a.fa</c>) + optional direct Citra install
    /// - Luma3DS LayeredFS file layout (<c>Luma3DS/luma/titles/{TitleID}/romfs/ie_a.fa</c>)
    /// </summary>
    public static class ModPackageExporter
    {
        public static void ExportArchives(
            SafeGOGame game,
            string protectedSourcePath,
            string outputDirectory,
            RandomizerOptions options,
            RandomizationExecutionReport report,
            IProgress<int> progress = null)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Debe especificarse una carpeta de salida válida.", nameof(outputDirectory));

            string fullOutputDir = Path.GetFullPath(outputDirectory);
            if (!string.IsNullOrWhiteSpace(protectedSourcePath))
            {
                string sourceDir = Directory.Exists(protectedSourcePath)
                    ? Path.GetFullPath(protectedSourcePath)
                    : Path.GetDirectoryName(Path.GetFullPath(protectedSourcePath)) ?? string.Empty;

                if (Arc0SafeArchive.PathsEqual(fullOutputDir, protectedSourcePath))
                {
                    throw new InvalidOperationException(
                        "La carpeta de salida no puede coincidir con la ruta del juego original.");
                }

                // Prevent overwriting ie_a.fa inside an extracted RomFS input folder
                if (Directory.Exists(protectedSourcePath) &&
                    Arc0SafeArchive.PathsEqual(fullOutputDir, sourceDir))
                {
                    throw new InvalidOperationException(
                        "La carpeta de salida debe ser distinta a la carpeta RomFS de entrada para proteger los archivos originales.");
                }
            }

            Directory.CreateDirectory(fullOutputDir);

            string titleId = !string.IsNullOrWhiteSpace(options.Export.CustomTitleIdOverride)
                ? options.Export.CustomTitleIdOverride.Trim().ToUpperInvariant()
                : report.ResolvedTitleId;

            if (string.IsNullOrWhiteSpace(titleId))
            {
                titleId = TitleIdCatalog.GetDefaultTitleId(report.EffectiveEdition);
            }
            report.ResolvedTitleId = titleId;

            // Build the randomized ie_a.fa once into the primary target, then copy fast to secondary layouts
            string primaryArchivePath = null;

            if (options.Export.ExportStandaloneIeAFa ||
                (!options.Export.ExportCitraModStructure && !options.Export.ExportLuma3dsLayeredFsStructure))
            {
                string standalonePath = Path.Combine(fullOutputDir, "Standalone", "ie_a.fa");
                report.Arc0WriteTelemetry = game.SaveCopyTo(standalonePath, progress);
                primaryArchivePath = standalonePath;
                report.ExportedPaths.Add(standalonePath);
            }

            if (options.Export.ExportCitraModStructure)
            {
                string citraRomFsDir = Path.Combine(fullOutputDir, "Citra", "load", "mods", titleId, "romfs");
                Directory.CreateDirectory(citraRomFsDir);
                string citraTarget = Path.Combine(citraRomFsDir, "ie_a.fa");

                if (primaryArchivePath == null)
                {
                    report.Arc0WriteTelemetry = game.SaveCopyTo(citraTarget, progress);
                    primaryArchivePath = citraTarget;
                }
                else
                {
                    File.Copy(primaryArchivePath, citraTarget, overwrite: true);
                }

                report.ExportedPaths.Add(citraTarget);
                WriteCitraReadme(Path.Combine(fullOutputDir, "Citra", "LEEME_CITRA.txt"), titleId, report);
            }

            if (options.Export.ExportLuma3dsLayeredFsStructure)
            {
                string lumaRomFsDir = Path.Combine(fullOutputDir, "Luma3DS", "luma", "titles", titleId, "romfs");
                Directory.CreateDirectory(lumaRomFsDir);
                string lumaTarget = Path.Combine(lumaRomFsDir, "ie_a.fa");

                if (primaryArchivePath == null)
                {
                    report.Arc0WriteTelemetry = game.SaveCopyTo(lumaTarget, progress);
                    primaryArchivePath = lumaTarget;
                }
                else
                {
                    File.Copy(primaryArchivePath, lumaTarget, overwrite: true);
                }

                report.ExportedPaths.Add(lumaTarget);
                WriteLayeredFsReadme(Path.Combine(fullOutputDir, "Luma3DS", "LEEME_LAYEREDFS.txt"), titleId, report);
            }

            // Optional direct installation into user's local Citra/Azahar/Lime3DS mods folder
            if (!string.IsNullOrWhiteSpace(options.Export.DirectCitraModsRootPath) && primaryArchivePath != null)
            {
                var installResult = LocalDiagnosticService.InstallIeAFaToCitraMods(
                    primaryArchivePath,
                    report.EffectiveEdition,
                    options.Export.CustomTitleIdOverride,
                    options.Export.DirectCitraModsRootPath);

                report.DirectCitraInstalledPath = installResult.InstalledIeAFaPath;
                report.ExportedPaths.Add(installResult.InstalledIeAFaPath);
                foreach (string msg in installResult.Messages)
                {
                    report.ValidationMessages.Add(msg);
                }
            }
        }

        public static void WriteManifestAndLogs(
            string outputDirectory,
            RandomizerOptions options,
            RandomizationExecutionReport report)
        {
            string fullOutputDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutputDir);

            // Write Blind-Play-safe manifest JSON AFTER OutputIntegrityVerified & SourceImmutabilityVerified are finalized
            string manifestPath = Path.Combine(fullOutputDir, "run_manifest.json");
            var manifestPayload = new
            {
                generator = "IEGO Randomizer (Luz y Sombra) v1.0.2",
                seed = report.Seed,
                verificationFingerprint = report.SeedFingerprint,
                edition = report.EffectiveEdition.ToString(),
                titleId = report.ResolvedTitleId,
                blindPlayMode = report.BlindPlayMode,
                generatedAtUtc = report.GeneratedAtUtc,
                sourceImmutabilityVerified = report.SourceImmutabilityVerified,
                outputIntegrityVerified = report.OutputIntegrityVerified,
                directCitraInstalledPath = report.DirectCitraInstalledPath,
                options,
                summary = new
                {
                    playablePool = report.PlayablePoolCount,
                    protectedStoryCharacters = report.ProtectedStoryCharactersCount,
                    protectedKeyItems = report.ProtectedKeyItemsCount,
                    randomizedTeamSlots = report.RandomizedTeamPlayerSlotsCount,
                    randomizedPachangaSlots = report.RandomizedPachangaPlayerSlotsCount,
                    randomizedPalpackCards = report.RandomizedPalpackCardsCount,
                    randomizedSkills = report.RandomizedSkillSlotsCount,
                    randomizedTeams = report.RandomizedTeamsCount + report.RandomizedPachangaTeamsCount,
                    randomizedOverworldChests = report.RandomizedOverworldChestsCount,
                    randomizedRouteChests = report.RandomizedRouteChestsCount,
                    randomizedDrops = report.RandomizedMatchDropsCount,
                    randomizedUniforms = report.RandomizedTeamUniformsCount
                },
                arc0Telemetry = report.Arc0WriteTelemetry == null ? null : new
                {
                    preservedRetailTables = report.Arc0WriteTelemetry.PreservedRetailTables,
                    originalArchiveSizeBytes = report.Arc0WriteTelemetry.OriginalArchiveSizeBytes,
                    writtenArchiveSizeBytes = report.Arc0WriteTelemetry.WrittenArchiveSizeBytes,
                    detectedDataAlignment = report.Arc0WriteTelemetry.DetectedDataAlignment,
                    directoryEntriesCount = report.Arc0WriteTelemetry.DirectoryEntriesCount,
                    directoryHashesCount = report.Arc0WriteTelemetry.DirectoryHashesCount,
                    fileEntriesCount = report.Arc0WriteTelemetry.FileEntriesCount,
                    modifiedArchiveEntriesCount = report.Arc0WriteTelemetry.ModifiedFilesCount
                },
                persistedVerification = report.PersistedDiffTelemetry == null ? null : new
                {
                    persistedModifiedFilesCount = report.PersistedDiffTelemetry.PersistedModifiedFilesCount,
                    persistedChangedCharaParamsCount = report.PersistedDiffTelemetry.PersistedChangedCharaParamsCount,
                    persistedChangedSkillTableEntriesCount = report.PersistedDiffTelemetry.PersistedChangedSkillTableEntriesCount,
                    persistedChangedStoryTeamsCount = report.PersistedDiffTelemetry.PersistedChangedStoryTeamsCount,
                    persistedChangedPachangaTeamsCount = report.PersistedDiffTelemetry.PersistedChangedPachangaTeamsCount,
                    persistedChangedTeamParamsCount = report.PersistedDiffTelemetry.PersistedChangedTeamParamsCount
                },
                validation = report.ValidationMessages,
                exportedFiles = report.ExportedPaths
            };

            string json = JsonSerializer.Serialize(manifestPayload, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
            report.ExportedPaths.Add(manifestPath);

            // Only emit spoiler log when the user explicitly disabled Blind-Play Mode
            if (!options.BlindPlayMode && report.SpoilerLogEntries.Count > 0)
            {
                string spoilerPath = Path.Combine(fullOutputDir, "spoiler_log.txt");
                File.WriteAllLines(spoilerPath, report.SpoilerLogEntries, Encoding.UTF8);
                report.ExportedPaths.Add(spoilerPath);
            }
        }

        private static void WriteCitraReadme(string readmePath, string titleId, RandomizationExecutionReport report)
        {
            string shadowAltNote = report.EffectiveEdition == GameEdition.Shadow
                ? $"Nota para Sombra (EUR): Si al abrir ubicación de mods Citra abre '{TitleIdCatalog.ShadowEurAltTitleId}' en vez de '{titleId}', copia la carpeta 'romfs' dentro de esa carpeta abierta por Citra."
                : string.Empty;

            string text =
$@"IEGO RANDOMIZER v1.0.2 — PAQUETE DE MOD PARA CITRA / AZAHAR / LIME3DS
=====================================================================
Semilla (Seed): {report.Seed}
Huella de verificación: {report.SeedFingerprint}
Edición: {report.EffectiveEdition}
Title ID configurado: {titleId}

Cómo instalar en Citra (PC) SIN ERRORES DE CARPETAS:
1. Abre Citra y haz clic derecho sobre tu juego (.3ds / .cci / .cia) -> 'Abrir ubicación de mods' (Open Mod Location).
   Citra abrirá una carpeta terminada en \{titleId}\
2. Entra aquí en:
     Citra\load\mods\{titleId}\
   y copia ÚNICAMENTE la carpeta 'romfs' dentro de la ventana que abrió Citra.
   Ruta exacta final que debe quedar en tu PC:
     %APPDATA%\Citra\load\mods\{titleId}\romfs\ie_a.fa
   (¡NO copies la carpeta 'Citra' entera dentro de '{titleId}', o quedará anidada dos veces y Citra no cargará el mod!)
3. También puedes pulsar el botón 'Instalar directo en Citra' dentro de IEGO Randomizer para que lo coloque automáticamente.
4. IMPORTANTE SOBRE PARTIDAS GUARDADAS (.ie):
   - Los equipos rivales de historia, pachangas 5v5, equipaciones, escudos y cofres cambian de inmediato.
   - Para ver las nuevas supertécnicas iniciales en los jugadores iniciales del Raimon, inicia 'PARTIDA NUEVA' (los saves existentes guardan en caché las técnicas de los jugadores ya reclutados).
{shadowAltNote}
";
            File.WriteAllText(readmePath, text.Trim() + Environment.NewLine, Encoding.UTF8);
        }

        private static void WriteLayeredFsReadme(string readmePath, string titleId, RandomizationExecutionReport report)
        {
            string shadowAltNote = report.EffectiveEdition == GameEdition.Shadow
                ? $"Nota para Sombra (EUR): Algunas versiones emplean '{TitleIdCatalog.ShadowEurAltTitleId}' en lugar de '{TitleIdCatalog.ShadowEurTitleId}'."
                : string.Empty;

            string text =
$@"IEGO RANDOMIZER v1.0.2 — ESTRUCTURA PREPARADA PARA LAYEREDFS (LUMA3DS)
======================================================================
Semilla (Seed): {report.Seed}
Huella de verificación: {report.SeedFingerprint}
Edición: {report.EffectiveEdition}
Title ID configurado: {titleId}

Estructura generada en esta carpeta:
  luma/titles/{titleId}/romfs/ie_a.fa

Uso opcional (solo preparación de archivos en PC; este programa NUNCA accede a consolas):
- La carpeta 'luma' sigue la jerarquía estándar de game patching de Luma3DS.
- Requiere tener 'Enable game patching' activado en la configuración de Luma3DS si algún día decides copiarla manualmente.
{shadowAltNote}
";
            File.WriteAllText(readmePath, text.Trim() + Environment.NewLine, Encoding.UTF8);
        }
    }
}
