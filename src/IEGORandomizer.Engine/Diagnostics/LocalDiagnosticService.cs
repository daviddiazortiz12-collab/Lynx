using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;

namespace IEGORandomizer.Engine.Diagnostics
{
    public sealed class CitraModSlotInspection
    {
        public string EmulatorName { get; init; } = string.Empty;
        public string ModsRootPath { get; init; } = string.Empty;
        public string TitleId { get; init; } = string.Empty;
        public string ExpectedIeAFaPath { get; init; } = string.Empty;
        public bool ExistsAtExpectedPath { get; init; }
        public long FileSizeBytes { get; init; }
        public string Sha256Hex { get; init; } = string.Empty;
        public bool MatchesSourceVanillaSha256 { get; init; }
        public int PersistedDiffFilesVsSource { get; init; }
        public List<string> DetectedMisnestedIeAFaPaths { get; init; } = new();
    }

    public sealed class LocalDiagnosticReport
    {
        public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
        public string InputPath { get; init; } = string.Empty;
        public bool InputExists { get; init; }
        public bool HasValidIeAFa { get; init; }
        public string InputKind { get; init; } = string.Empty;
        public string ActiveIeAFaPath { get; init; } = string.Empty;
        public long IeAFaSizeBytes { get; init; }
        public string IeAFaSha256Hex { get; init; } = string.Empty;
        public string DetectedEdition { get; init; } = string.Empty;
        public string ExpectedPrimaryTitleId { get; init; } = string.Empty;
        public List<string> CandidateTitleIds { get; init; } = new();
        public List<string> AvailableLanguages { get; init; } = new();

        // Sibling RomFS telemetry (e.g. fix.xr, banner.icn, snd/product/*.bcsar, movie/*.moflex)
        public bool HasFixXrSibling { get; init; }
        public long FixXrSizeBytes { get; init; }
        public bool HasBannerIcnSibling { get; init; }
        public string BannerTitleSummary { get; init; } = string.Empty;
        public int SoundArchivesFound { get; init; }
        public int MovieFilesFound { get; init; }

        // ARC0 & game table counts
        public Arc0ArchiveInspection Arc0Header { get; set; }
        public int CharaBaseCount { get; set; }
        public int CharaParamCount { get; set; }
        public int CoherentPlayablePlayersCount { get; set; }
        public int SkillConfigCount { get; set; }
        public int SkillTableCount { get; set; }
        public int StoryTeamsCount { get; set; }
        public int RandomizableRivalStoryTeamsCount { get; set; }
        public int PachangaTeamsCount { get; set; }
        public int TeamParamsCount { get; set; }
        public int ValidHomeKitsCount { get; set; }
        public int ValidGoalkeeperKitsCount { get; set; }
        public int ValidAwayKitsCount { get; set; }
        public int OverworldChestsCount { get; set; }
        public int OverworldPachangaSpawnsCount { get; set; }
        public int CompetitionRouteFilesCount { get; set; }
        public int ScannedNutbScriptsCount { get; set; }
        public int ProtectedStoryPlayersCount { get; set; }
        public int ProtectedKeyItemsCount { get; set; }

        // Citra / Azahar / Lime3DS Mod Folder Inspection
        public List<string> DiscoveredCitraModsRoots { get; init; } = new();
        public List<CitraModSlotInspection> CitraModInspections { get; init; } = new();
        public PersistedArchiveDiffSummary TargetOutputDiff { get; set; }

        public List<string> Findings { get; init; } = new();
        public List<string> ActionableWarnings { get; init; } = new();
    }

    public sealed class CitraDirectInstallResult
    {
        public string InstalledIeAFaPath { get; init; } = string.Empty;
        public string SecondaryInstalledPath { get; init; } = string.Empty;
        public string TitleId { get; init; } = string.Empty;
        public string CitraModsRoot { get; init; } = string.Empty;
        public long FileSizeBytes { get; init; }
        public List<string> Messages { get; init; } = new();
    }

    /// <summary>
    /// Non-spoiler local forensic diagnostic &amp; Citra LayeredFS verification service.
    /// Helps users verify their real <c>ie_a.fa</c> / <c>RomFS</c> / <c>.3ds</c> dump and pinpoints
    /// why Citra might not be loading a mod (extracted-RomFS trap, mis-nested <c>load/mods</c> folders,
    /// <c>0004000000112F00</c> vs <c>0004000000113000</c>/<c>000400000017C200</c> TitleID mismatch,
    /// or existing savegame party cache).
    /// </summary>
    public static class LocalDiagnosticService
    {
        public static LocalDiagnosticReport RunDiagnostics(
            string inputPath,
            GameEdition requestedEdition = GameEdition.Auto,
            string targetOutputOrCitraDir = null)
        {
            string fullInput = string.IsNullOrWhiteSpace(inputPath) ? string.Empty : Path.GetFullPath(inputPath);
            bool inputExists = !string.IsNullOrEmpty(fullInput) && (File.Exists(fullInput) || Directory.Exists(fullInput));

            var discoveredRoots = DiscoverCitraModsRoots(targetOutputOrCitraDir);
            var report = new LocalDiagnosticReport
            {
                InputPath = fullInput,
                InputExists = inputExists,
                DiscoveredCitraModsRoots = discoveredRoots
            };

            if (!inputExists)
            {
                report.ActionableWarnings.Add(
                    $"La ruta de entrada '{inputPath}' no existe en este equipo.");
                return report;
            }

            // Inspect sibling RomFS assets if input is a directory or sits inside an extracted RomFS folder
            string romFsDirCandidate = Directory.Exists(fullInput)
                ? fullInput
                : (Path.GetDirectoryName(fullInput) ?? string.Empty);

            bool hasFixXr = false;
            long fixXrSize = 0;
            bool hasBannerIcn = false;
            string bannerTitle = string.Empty;
            int bcsarCount = 0;
            int moflexCount = 0;

            if (!string.IsNullOrEmpty(romFsDirCandidate) && Directory.Exists(romFsDirCandidate))
            {
                string fixXrPath = Path.Combine(romFsDirCandidate, "fix.xr");
                if (File.Exists(fixXrPath))
                {
                    hasFixXr = true;
                    fixXrSize = new FileInfo(fixXrPath).Length;
                }

                string bannerPath = Path.Combine(romFsDirCandidate, "ctr", "exsave", "banner.icn");
                if (File.Exists(bannerPath))
                {
                    hasBannerIcn = true;
                    bannerTitle = TryReadSmdhBannerTitle(bannerPath);
                }

                string sndProdDir = Path.Combine(romFsDirCandidate, "snd", "product");
                if (Directory.Exists(sndProdDir))
                {
                    bcsarCount = Directory.GetFiles(sndProdDir, "*.bcsar", SearchOption.TopDirectoryOnly).Length;
                }

                string movieDir = Path.Combine(romFsDirCandidate, "movie");
                if (Directory.Exists(movieDir))
                {
                    moflexCount = Directory.GetFiles(movieDir, "*.moflex", SearchOption.TopDirectoryOnly).Length;
                }
            }

            // Check if directory is an extracted RomFS missing ie_a.fa
            if (Directory.Exists(fullInput) &&
                !File.Exists(Path.Combine(fullInput, "ie_a.fa")) &&
                !File.Exists(Path.Combine(fullInput, "romfs", "ie_a.fa")))
            {
                return new LocalDiagnosticReport
                {
                    InputPath = fullInput,
                    InputExists = true,
                    HasValidIeAFa = false,
                    InputKind = "RomFsDirectory (sin ie_a.fa)",
                    HasFixXrSibling = hasFixXr,
                    FixXrSizeBytes = fixXrSize,
                    HasBannerIcnSibling = hasBannerIcn,
                    BannerTitleSummary = bannerTitle,
                    SoundArchivesFound = bcsarCount,
                    MovieFilesFound = moflexCount,
                    DiscoveredCitraModsRoots = discoveredRoots,
                    ActionableWarnings =
                    {
                        $"La carpeta '{fullInput}' contiene archivos auxiliares de RomFS (fix.xr={hasFixXr}, banner.icn={hasBannerIcn}, bcsar={bcsarCount}, moflex={moflexCount}), pero falta 'ie_a.fa' (~855 MB).",
                        "Asegúrate de que 'ie_a.fa' esté completo dentro de la carpeta RomFS antes de randomizar."
                    }
                };
            }

            using var workspace = GameWorkspace.Open(fullInput, requestedEdition, "es", computeSourceSha256: true);
            SafeGOGame game = workspace.Game;

            var candidateTitleIds = new List<string>();
            if (workspace.EffectiveEdition == GameEdition.Shadow)
            {
                candidateTitleIds.Add(TitleIdCatalog.ShadowEurTitleId);
                candidateTitleIds.Add(TitleIdCatalog.ShadowEurAltTitleId);
            }
            else
            {
                candidateTitleIds.Add(TitleIdCatalog.LightEurTitleId);
                // Also inspect Shadow TitleIDs in Citra in case user selected Auto on a Shadow dump!
                candidateTitleIds.Add(TitleIdCatalog.ShadowEurTitleId);
                candidateTitleIds.Add(TitleIdCatalog.ShadowEurAltTitleId);
            }

            var langs = SafeGOGame.DetectAvailableLanguageCodes(game.IE_A).ToList();
            var arc0Header = Arc0SafeArchive.InspectArchive(game.IE_A);
            var visual = VisualCoherenceValidator.AnalyzeArchive(game);
            var protectionSvc = new StoryProtectionService();
            var protection = protectionSvc.BuildContext(game, workspace.EffectiveEdition, new ProtectionRandomizerOptions());

            var storyTeams = game.GetStoryTeamsOnly();
            int randomizableRivalTeams = storyTeams.Count(st =>
                st.TeamConfigID != 0 &&
                !protection.ScriptedTeamConfigHashes.Contains(st.TeamConfigID) &&
                !protection.RaimonCoreTeamConfigHashes.Contains(st.TeamConfigID));

            int routeFilesCount = 0;
            var soccerDir = game.GetDirectory("soccer");
            if (soccerDir != null)
            {
                foreach (string f in soccerDir.Files.Keys)
                {
                    if (f.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(f, "soccer_config.cfg.bin", StringComparison.OrdinalIgnoreCase) &&
                        RouteDataService.TryReadRouteFile(soccerDir, f, out _))
                    {
                        routeFilesCount++;
                    }
                }
            }

            var finalReport = new LocalDiagnosticReport
            {
                InputPath = fullInput,
                InputExists = true,
                HasValidIeAFa = true,
                InputKind = workspace.InputKind.ToString(),
                ActiveIeAFaPath = workspace.ActiveIeAFaPath,
                IeAFaSizeBytes = new FileInfo(workspace.ActiveIeAFaPath).Length,
                IeAFaSha256Hex = workspace.InputKind == GameInputKind.Arc0File || workspace.InputKind == GameInputKind.RomFsDirectory
                    ? workspace.SourceSha256Hex
                    : GameWorkspace.ComputeSha256Hex(workspace.ActiveIeAFaPath),
                DetectedEdition = workspace.EffectiveEdition.ToString(),
                ExpectedPrimaryTitleId = workspace.ResolvedTitleId,
                CandidateTitleIds = candidateTitleIds,
                AvailableLanguages = langs,
                HasFixXrSibling = hasFixXr,
                FixXrSizeBytes = fixXrSize,
                HasBannerIcnSibling = hasBannerIcn,
                BannerTitleSummary = bannerTitle,
                SoundArchivesFound = bcsarCount,
                MovieFilesFound = moflexCount,
                Arc0Header = arc0Header,
                CharaBaseCount = game.GetCharabase().Length,
                CharaParamCount = game.GetCharaparams().Length,
                CoherentPlayablePlayersCount = visual.CoherentPlayableParamHashes.Count,
                SkillConfigCount = game.GetSkillConfigs(false).Length,
                SkillTableCount = game.GetSkillTable().Length,
                StoryTeamsCount = storyTeams.Length,
                RandomizableRivalStoryTeamsCount = randomizableRivalTeams,
                PachangaTeamsCount = game.GetEncounterTeamsOnly().Length,
                TeamParamsCount = game.GetTeamParams().Length,
                ValidHomeKitsCount = visual.ValidKitNumbers.Count,
                ValidGoalkeeperKitsCount = visual.ValidGoalkeeperKitNumbers.Count,
                ValidAwayKitsCount = visual.ValidAwayKitNumbers.Count,
                OverworldChestsCount = MapDataService.LoadAllTreasureChests(game.IE_A.Directory).Count,
                OverworldPachangaSpawnsCount = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory).Count,
                CompetitionRouteFilesCount = routeFilesCount,
                ScannedNutbScriptsCount = protection.NutbAudit.ScannedScriptCount,
                ProtectedStoryPlayersCount = protection.ProtectedStoryPlayerParamHashes.Count,
                ProtectedKeyItemsCount = protection.ProtectedItemHashes.Count,
                DiscoveredCitraModsRoots = discoveredRoots
            };

            finalReport.Findings.Add(
                $"Archivo ARC0 válido: {finalReport.IeAFaSizeBytes:N0} bytes, {arc0Header.FileEntriesCount} archivos internos, alineación {arc0Header.DetectedDataAlignment}B, compresión de tablas [{arc0Header.DirectoryEntriesCompression} / {arc0Header.FileEntriesCompression}].");
            finalReport.Findings.Add(
                $"Edición resuelta: {finalReport.DetectedEdition} (Title ID principal: {finalReport.ExpectedPrimaryTitleId}; idiomas en ie_a.fa: {string.Join(", ", langs)}).");
            finalReport.Findings.Add(
                $"Tablas leídas: {finalReport.CharaParamCount} jugadores ({finalReport.CoherentPlayablePlayersCount} aptos para sorteo), {finalReport.StoryTeamsCount} equipos de historia/cadena ({finalReport.RandomizableRivalStoryTeamsCount} equipos rivales randomizables), {finalReport.PachangaTeamsCount} pachangas 5v5, {finalReport.ValidHomeKitsCount} equipaciones 3D válidas ({finalReport.ValidGoalkeeperKitsCount} con modelo de portero uzb).");

            if (workspace.InputKind == GameInputKind.RomFsDirectory)
            {
                finalReport.ActionableWarnings.Add(
                    "AVISO CLAVE DE CITRA (Carpeta RomFS extraída): Si en Citra inicias el juego desde un archivo .3ds / .cci / .cia, Citra IGNORA tu carpeta 'RomFS' extraída del disco duro. DEBES instalar 'romfs/ie_a.fa' dentro de la carpeta de mods de Citra (%APPDATA%\\Citra\\load\\mods\\" +
                    finalReport.ExpectedPrimaryTitleId + "\\romfs\\ie_a.fa) usando el botón 'Instalar directo en Citra'.");
            }

            // Inspect Citra mod folders and/or explicit target output folder
            foreach ((string emuName, string modsRoot) in EnumerateNamedCitraRoots(targetOutputOrCitraDir))
            {
                foreach (string tid in candidateTitleIds)
                {
                    var slot = InspectCitraTitleSlot(emuName, modsRoot, tid, workspace.ActiveIeAFaPath, finalReport.IeAFaSha256Hex);
                    if (slot.ExistsAtExpectedPath || slot.DetectedMisnestedIeAFaPaths.Count > 0 || tid == finalReport.ExpectedPrimaryTitleId)
                    {
                        finalReport.CitraModInspections.Add(slot);
                    }

                    foreach (string misnested in slot.DetectedMisnestedIeAFaPaths)
                    {
                        finalReport.ActionableWarnings.Add(
                            $"CARPETA MAL ANIDADA EN {emuName}: Se encontró '{misnested}', pero Citra exige EXACTAMENTE '{slot.ExpectedIeAFaPath}' (sin repetir 'Citra/load/mods/{tid}' dentro).");
                    }

                    if (slot.ExistsAtExpectedPath && slot.MatchesSourceVanillaSha256)
                    {
                        finalReport.ActionableWarnings.Add(
                            $"ARCHIVO SIN RANDOMIZAR EN {emuName}: '{slot.ExpectedIeAFaPath}' tiene exactamente el mismo SHA-256 que el juego original sin modificar.");
                    }
                    else if (slot.ExistsAtExpectedPath && slot.PersistedDiffFilesVsSource > 0)
                    {
                        finalReport.Findings.Add(
                            $"MOD ACTIVO VERIFICADO EN {emuName}: '{slot.ExpectedIeAFaPath}' contiene {slot.PersistedDiffFilesVsSource} archivos internos modificados respecto al original.");
                    }
                }
            }

            // If targetOutputOrCitraDir points directly to a generated output ie_a.fa or Randomizer output directory (not a Citra mods root), diff it too!
            if (!string.IsNullOrWhiteSpace(targetOutputOrCitraDir))
            {
                string trimmedLeaf = Path.GetFileName(Path.GetFullPath(targetOutputOrCitraDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                bool isModsRoot = string.Equals(trimmedLeaf, "mods", StringComparison.OrdinalIgnoreCase);
                string candidateOutFa = isModsRoot
                    ? string.Empty
                    : ResolveGeneratedIeAFa(targetOutputOrCitraDir, finalReport.ExpectedPrimaryTitleId);

                if (!string.IsNullOrEmpty(candidateOutFa) &&
                    File.Exists(candidateOutFa) &&
                    !Arc0SafeArchive.PathsEqual(candidateOutFa, workspace.ActiveIeAFaPath))
                {
                    try
                    {
                        finalReport.TargetOutputDiff = PostSaveArchiveVerifier.VerifyPersistedMutations(
                            workspace.ActiveIeAFaPath,
                            candidateOutFa,
                            "es",
                            report: null);

                        finalReport.Findings.Add(
                            $"Comparación contra salida '{candidateOutFa}': {finalReport.TargetOutputDiff.PersistedModifiedFilesCount} archivos internos distintos, " +
                            $"{finalReport.TargetOutputDiff.PersistedChangedCharaParamsCount} jugadores cambiados, " +
                            $"{finalReport.TargetOutputDiff.PersistedChangedSkillTableEntriesCount} huecos de técnicas cambiados, " +
                            $"{finalReport.TargetOutputDiff.PersistedChangedStoryTeamsCount} equipos rivales cambiados, " +
                            $"{finalReport.TargetOutputDiff.PersistedChangedPachangaTeamsCount} pachangas cambiadas, " +
                            $"{finalReport.TargetOutputDiff.PersistedChangedTeamParamsCount} parámetros/equipaciones de equipo cambiados.");
                    }
                    catch (Exception ex)
                    {
                        finalReport.ActionableWarnings.Add(
                            $"El archivo exportado '{candidateOutFa}' parece incompleto o dañado: {ex.Message}");
                    }
                }
            }

            bool anyActiveCitraMod = finalReport.CitraModInspections.Any(s =>
                s.TitleId == finalReport.ExpectedPrimaryTitleId && s.ExistsAtExpectedPath && !s.MatchesSourceVanillaSha256);

            if (!anyActiveCitraMod && finalReport.CitraModInspections.Count > 0)
            {
                finalReport.ActionableWarnings.Add(
                    $"No se detectó ningún 'romfs/ie_a.fa' randomizado activo para el Title ID {finalReport.ExpectedPrimaryTitleId} en las carpetas de mods de Citra detectadas.");
            }

            finalReport.Findings.Add(
                "Recordatorio de partida guardada (.ie): Las partidas ya empezadas guardan en la RAM del save las técnicas actuales de los jugadores ya reclutados; inicia 'Partida Nueva' para ver los sets iniciales nuevos en Raimon (los rivales, pachangas, cofres y equipaciones se ven de inmediato).");

            return finalReport;
        }

        public static CitraDirectInstallResult InstallIeAFaToCitraMods(
            string sourcePathOrOutputDir,
            GameEdition edition,
            string customTitleIdOverride = null,
            string explicitCitraModsRoot = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePathOrOutputDir))
                throw new ArgumentException("Debe indicar el archivo ie_a.fa o carpeta de salida generada.", nameof(sourcePathOrOutputDir));

            string titleId = !string.IsNullOrWhiteSpace(customTitleIdOverride)
                ? customTitleIdOverride.Trim().ToUpperInvariant()
                : TitleIdCatalog.GetDefaultTitleId(edition == GameEdition.Auto ? GameEdition.Light : edition);

            string sourceIeAFa = File.Exists(sourcePathOrOutputDir)
                ? Path.GetFullPath(sourcePathOrOutputDir)
                : ResolveGeneratedIeAFa(sourcePathOrOutputDir, titleId);

            if (string.IsNullOrEmpty(sourceIeAFa) || !File.Exists(sourceIeAFa))
            {
                throw new FileNotFoundException(
                    $"No se encontró un archivo 'ie_a.fa' exportado dentro de '{sourcePathOrOutputDir}'.");
            }

            string citraModsRoot = ResolveWritableCitraModsRoot(explicitCitraModsRoot);
            string primaryDest = Path.Combine(citraModsRoot, titleId, "romfs", "ie_a.fa");
            Directory.CreateDirectory(Path.GetDirectoryName(primaryDest)!);

            if (!Arc0SafeArchive.PathsEqual(sourceIeAFa, primaryDest))
            {
                File.Copy(sourceIeAFa, primaryDest, overwrite: true);
            }

            var messages = new List<string>
            {
                $"Instalado correctamente en '{primaryDest}' ({new FileInfo(primaryDest).Length:N0} bytes)."
            };

            // If installing EUR Shadow without custom override and the user's Citra already has a folder for 000400000017C200, sync it too
            string secondaryDest = string.Empty;
            if (string.IsNullOrWhiteSpace(customTitleIdOverride) && edition == GameEdition.Shadow)
            {
                string altSlotDir = Path.Combine(citraModsRoot, TitleIdCatalog.ShadowEurAltTitleId);
                if (Directory.Exists(altSlotDir))
                {
                    secondaryDest = Path.Combine(altSlotDir, "romfs", "ie_a.fa");
                    Directory.CreateDirectory(Path.GetDirectoryName(secondaryDest)!);
                    File.Copy(sourceIeAFa, secondaryDest, overwrite: true);
                    messages.Add($"Sincronizado también en el Title ID alternativo de Sombra '{secondaryDest}'.");
                }
            }

            return new CitraDirectInstallResult
            {
                InstalledIeAFaPath = primaryDest,
                SecondaryInstalledPath = secondaryDest,
                TitleId = titleId,
                CitraModsRoot = citraModsRoot,
                FileSizeBytes = new FileInfo(primaryDest).Length,
                Messages = messages
            };
        }

        public static List<string> DiscoverCitraModsRoots(string extraPath = null)
        {
            return EnumerateNamedCitraRoots(extraPath)
                .Select(x => x.ModsRoot)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
        }

        public static string ResolveDefaultCitraModsRoot()
        {
            var existing = DiscoverCitraModsRoots();
            if (existing.Count > 0)
                return existing[0];

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(appData, "Citra", "load", "mods");
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", "citra-emu", "load", "mods");
        }

        private static string ResolveWritableCitraModsRoot(string explicitRoot)
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot))
            {
                string full = Path.GetFullPath(explicitRoot.Trim());
                // If the user pasted ".../Citra/load/mods/0004000000112F00" or ".../Citra", normalize intelligently to ".../load/mods"!
                string leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (leaf.Length == 16 && leaf.StartsWith("00040000", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
                }
                if (string.Equals(leaf, "Citra", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leaf, "Azahar", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leaf, "Lime3DS", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(full, "load", "mods");
                }
                if (string.Equals(leaf, "load", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(full, "mods");
                }
                return full;
            }

            return ResolveDefaultCitraModsRoot();
        }

        private static IEnumerable<(string Emulator, string ModsRoot)> EnumerateNamedCitraRoots(string extraPath)
        {
            var candidates = new List<(string Emulator, string Path)>();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                candidates.Add(("Citra (AppData)", Path.Combine(appData, "Citra", "load", "mods")));
                candidates.Add(("Azahar (AppData)", Path.Combine(appData, "Azahar", "load", "mods")));
                candidates.Add(("Lime3DS (AppData)", Path.Combine(appData, "Lime3DS", "load", "mods")));
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidates.Add(("Citra (XDG)", Path.Combine(home, ".local", "share", "citra-emu", "load", "mods")));
                candidates.Add(("Azahar (XDG)", Path.Combine(home, ".local", "share", "azahar-emu", "load", "mods")));
                candidates.Add(("Lime3DS (XDG)", Path.Combine(home, ".local", "share", "lime3ds-emu", "load", "mods")));
            }

            if (!string.IsNullOrWhiteSpace(extraPath) && Directory.Exists(extraPath))
            {
                string fullExtra = Path.GetFullPath(extraPath);
                string normalizedRoot = ResolveWritableCitraModsRoot(fullExtra);
                if (Directory.Exists(normalizedRoot))
                {
                    candidates.Add(("Ruta indicada", normalizedRoot));
                }
            }

            foreach (var c in candidates)
            {
                if (Directory.Exists(c.Path))
                    yield return c;
            }
        }

        private static CitraModSlotInspection InspectCitraTitleSlot(
            string emuName,
            string modsRoot,
            string titleId,
            string sourceIeAFaPath,
            string sourceSha256)
        {
            string titleSlotDir = Path.Combine(modsRoot, titleId);
            string expectedPath = Path.Combine(titleSlotDir, "romfs", "ie_a.fa");
            bool existsExpected = File.Exists(expectedPath);
            long size = 0;
            string sha = string.Empty;
            bool sameSha = false;
            int diffCount = 0;
            var misnested = new List<string>();

            if (Directory.Exists(titleSlotDir))
            {
                try
                {
                    foreach (string found in Directory.EnumerateFiles(titleSlotDir, "ie_a.fa", SearchOption.AllDirectories))
                    {
                        if (!Arc0SafeArchive.PathsEqual(found, expectedPath))
                        {
                            misnested.Add(found);
                        }
                    }
                }
                catch
                {
                    // Ignore permission errors in unrelated folders
                }
            }

            if (existsExpected)
            {
                size = new FileInfo(expectedPath).Length;
                sha = GameWorkspace.ComputeSha256Hex(expectedPath);
                sameSha = !string.IsNullOrEmpty(sourceSha256) &&
                          string.Equals(sha, sourceSha256, StringComparison.OrdinalIgnoreCase);

                if (!sameSha && File.Exists(sourceIeAFaPath))
                {
                    try
                    {
                        var diff = PostSaveArchiveVerifier.VerifyPersistedMutations(
                            sourceIeAFaPath,
                            expectedPath,
                            "es",
                            report: null);
                        diffCount = diff.PersistedModifiedFilesCount;
                    }
                    catch
                    {
                        diffCount = -1;
                    }
                }
            }

            return new CitraModSlotInspection
            {
                EmulatorName = emuName,
                ModsRootPath = modsRoot,
                TitleId = titleId,
                ExpectedIeAFaPath = expectedPath,
                ExistsAtExpectedPath = existsExpected,
                FileSizeBytes = size,
                Sha256Hex = sha,
                MatchesSourceVanillaSha256 = sameSha,
                PersistedDiffFilesVsSource = diffCount,
                DetectedMisnestedIeAFaPaths = misnested
            };
        }

        private static string ResolveGeneratedIeAFa(string rootOrFile, string titleId)
        {
            string full = Path.GetFullPath(rootOrFile);
            if (File.Exists(full))
                return full;
            if (!Directory.Exists(full))
                return string.Empty;

            string[] candidates =
            {
                Path.Combine(full, "Standalone", "ie_a.fa"),
                Path.Combine(full, "Citra", "load", "mods", titleId, "romfs", "ie_a.fa"),
                Path.Combine(full, "Luma3DS", "luma", "titles", titleId, "romfs", "ie_a.fa"),
                Path.Combine(full, "romfs", "ie_a.fa"),
                Path.Combine(full, "ie_a.fa")
            };

            foreach (string c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }

            return Directory.EnumerateFiles(full, "ie_a.fa", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
        }

        public static string TryReadSmdhBannerTitle(string bannerIcnPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(bannerIcnPath);
                if (bytes.Length < 0x208 ||
                    bytes[0] != (byte)'S' || bytes[1] != (byte)'M' || bytes[2] != (byte)'D' || bytes[3] != (byte)'H')
                {
                    return string.Empty;
                }

                // Language index 5 = Spanish, 1 = English in 3DS SMDH
                foreach (int langIdx in new[] { 5, 1, 0 })
                {
                    int off = 8 + (langIdx * 0x200);
                    if (off + 0x200 <= bytes.Length)
                    {
                        string longTitle = System.Text.Encoding.Unicode
                            .GetString(bytes, off + 0x80, 0x100)
                            .TrimEnd('\0')
                            .Trim();
                        string publisher = System.Text.Encoding.Unicode
                            .GetString(bytes, off + 0x180, 0x80)
                            .TrimEnd('\0')
                            .Trim();
                        if (!string.IsNullOrWhiteSpace(longTitle))
                        {
                            return string.IsNullOrWhiteSpace(publisher)
                                ? longTitle
                                : $"{longTitle} ({publisher})";
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal
            }
            return string.Empty;
        }
    }
}
