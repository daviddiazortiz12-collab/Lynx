using System;
using System.IO;
using System.Text;
using System.Text.Json;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Modules;
using Lynx.Core.Games;
using Lynx.Core.RomFs;

namespace IEGORandomizer.Engine.Export
{
    /// <summary>
    /// Step 11: Exports the randomized <c>ie_a.fa</c> into a clean output folder without touching
    /// the original source file or interacting with physical console hardware.
    /// Supports:
    /// - Standalone <c>ie_a.fa</c>
    /// - Citra emulator mod layout (<c>Citra/load/mods/{TitleID}/romfs/ie_a.fa</c>)
    /// - Luma3DS LayeredFS file layout (<c>Luma3DS/luma/titles/{TitleID}/romfs/ie_a.fa</c>)
    /// </summary>
    public static class ModPackageExporter
    {
        public static void Export(
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
                game.SaveCopyTo(standalonePath, progress);
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
                    game.SaveCopyTo(citraTarget, progress);
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
                    game.SaveCopyTo(lumaTarget, progress);
                    primaryArchivePath = lumaTarget;
                }
                else
                {
                    File.Copy(primaryArchivePath, lumaTarget, overwrite: true);
                }

                report.ExportedPaths.Add(lumaTarget);
                WriteLayeredFsReadme(Path.Combine(fullOutputDir, "Luma3DS", "LEEME_LAYEREDFS.txt"), titleId, report);
            }

            // Write Blind-Play-safe manifest JSON (contains seed + verification hash + counters, ZERO spoilers)
            string manifestPath = Path.Combine(fullOutputDir, "run_manifest.json");
            var manifestPayload = new
            {
                generator = "IEGO Randomizer (Luz y Sombra)",
                seed = report.Seed,
                verificationFingerprint = report.SeedFingerprint,
                edition = report.EffectiveEdition.ToString(),
                titleId = report.ResolvedTitleId,
                blindPlayMode = report.BlindPlayMode,
                generatedAtUtc = report.GeneratedAtUtc,
                sourceImmutabilityVerified = report.SourceImmutabilityVerified,
                outputIntegrityVerified = report.OutputIntegrityVerified,
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
                ? $"Nota para Sombra (EUR): Si tu copia usa el TitleID alternativo {TitleIdCatalog.ShadowEurAltTitleId}, renombra la carpeta '{titleId}' a '{TitleIdCatalog.ShadowEurAltTitleId}'."
                : string.Empty;

            string text =
$@"IEGO RANDOMIZER — PAQUETE DE MOD PARA CITRA
============================================
Semilla (Seed): {report.Seed}
Huella de verificación: {report.SeedFingerprint}
Edición: {report.EffectiveEdition}
Title ID configurado: {titleId}

Cómo cargar en Citra (PC):
1. Abre Citra en tu PC.
2. Haz clic derecho sobre Inazuma Eleven GO: Luz / Sombra -> 'Abrir ubicación de mods' (Open Mod Location).
3. Copia la carpeta 'romfs' ubicada en:
   load/mods/{titleId}/romfs
   directamente dentro de la carpeta que abrió Citra.
4. Inicia el juego normalmente. Tu archivo original del juego NO ha sido modificado.
{shadowAltNote}
";
            File.WriteAllText(readmePath, text.Trim() + Environment.NewLine, Encoding.UTF8);
        }

        private static void WriteLayeredFsReadme(string readmePath, string titleId, RandomizationExecutionReport report)
        {
            string shadowAltNote = report.EffectiveEdition == GameEdition.Shadow
                ? $"Nota para Sombra (EUR): Algunas versiones digitales/físicas europeas emplean '{TitleIdCatalog.ShadowEurAltTitleId}' en lugar de '{TitleIdCatalog.ShadowEurTitleId}'."
                : string.Empty;

            string text =
$@"IEGO RANDOMIZER — ESTRUCTURA PREPARADA PARA LAYEREDFS (LUMA3DS)
================================================================
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
