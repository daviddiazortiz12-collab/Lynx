using System;
using System.IO;
using System.Text.Json;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Cia;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Diagnostics;
using IEGORandomizer.Engine.OnConsole;
using IEGORandomizer.Engine.Repair;
using Lynx.Core.RomFs;

namespace IEGORandomizer.App
{
    /// <summary>
    /// Headless command-line runner for batch execution, local .cia workflows, local diagnostics, and automated testing.
    /// </summary>
    public static class CliRunner
    {
        public static int Run(string[] args)
        {
            string inputPath = null;
            string outputDir = null;
            string outputCiaPath = null;
            string inspectCiaPath = null;
            string mergeSourceCiaPath = null;
            string mergeRomFsDir = null;
            string repairInstalledPath = null;
            string cleanReferencePath = null;
            string buildSeedRecipePath = null;
            string exportDiagJsonPath = null;
            string configJsonPath = null;
            bool diagnoseMode = false;
            bool jsonOutput = false;
            var options = new RandomizerOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--cli":
                        break;
                    case "--diagnose":
                        diagnoseMode = true;
                        break;
                    case "--json":
                        jsonOutput = true;
                        break;
                    case "--input":
                    case "-i":
                        if (i + 1 < args.Length) inputPath = args[++i];
                        break;
                    case "--output":
                    case "-o":
                        if (i + 1 < args.Length) outputDir = args[++i];
                        break;
                    case "--output-cia":
                        if (i + 1 < args.Length) outputCiaPath = args[++i];
                        break;
                    case "--inspect-cia":
                        if (i + 1 < args.Length) inspectCiaPath = args[++i];
                        break;
                    case "--merge-cia":
                        if (i + 1 < args.Length) mergeSourceCiaPath = args[++i];
                        break;
                    case "--romfs":
                        if (i + 1 < args.Length) mergeRomFsDir = args[++i];
                        break;
                    case "--aes-keys":
                        if (i + 1 < args.Length) options.AesKeysPath = args[++i];
                        break;
                    case "--export-diag":
                        if (i + 1 < args.Length) exportDiagJsonPath = args[++i];
                        break;
                    case "--seed":
                    case "-s":
                        if (i + 1 < args.Length) options.Seed = args[++i];
                        break;
                    case "--edition":
                    case "-e":
                        if (i + 1 < args.Length)
                        {
                            string ed = args[++i].Trim().ToLowerInvariant();
                            options.Edition = ed switch
                            {
                                "light" or "luz" => GameEdition.Light,
                                "shadow" or "sombra" => GameEdition.Shadow,
                                _ => GameEdition.Auto
                            };
                        }
                        break;
                    case "--install-citra":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        {
                            options.Export.DirectCitraModsRootPath = args[++i];
                        }
                        else
                        {
                            options.Export.DirectCitraModsRootPath = LocalDiagnosticService.ResolveDefaultCitraModsRoot();
                        }
                        break;
                    case "--fbi-update":
                        options.Cia.BumpTitleVersionForFbiUpdate = true;
                        break;
                    case "--keep-cia-version":
                        options.Cia.BumpTitleVersionForFbiUpdate = false;
                        break;
                    case "--cia-version":
                        if (i + 1 < args.Length)
                        {
                            options.Cia.BumpTitleVersionForFbiUpdate = true;
                            options.Cia.CustomTitleVersionOverride = args[++i];
                        }
                        break;
                    case "--companion-luma":
                        options.Cia.ExportCompanionLuma3dsPatch = true;
                        break;
                    case "--dedup-es":
                        options.Cia.DeduplicateOtherLanguageStreamsToSpanish = true;
                        break;
                    case "--export-2ds-delta-pack":
                        if (i + 1 < args.Length)
                        {
                            options.Export.Export2dsDeltaPatchPackPath = args[++i];
                            options.Export.ExportStandaloneIeAFa = true;
                            options.Export.ExportCitraModStructure = false;
                            options.Export.ExportLuma3dsLayeredFsStructure = false;
                        }
                        break;
                    case "--delta-over-vanilla":
                        options.Export.AssumePriorRandomizedInstallForDeltaPack = false;
                        break;
                    case "--delta-against":
                        if (i + 1 < args.Length)
                            options.Export.DeltaPatchInstalledBaselinePath = args[++i];
                        break;
                    case "--repair-installed":
                        if (i + 1 < args.Length)
                            repairInstalledPath = args[++i];
                        break;
                    case "--clean-reference":
                        if (i + 1 < args.Length)
                            cleanReferencePath = args[++i];
                        break;
                    case "--build-2ds-seed-recipe":
                        if (i + 1 < args.Length)
                            buildSeedRecipePath = args[++i];
                        break;
                    case "--preset":
                        if (i + 1 < args.Length)
                        {
                            string preset = args[++i].Trim().ToLowerInvariant();
                            if (preset is "full" or "todo")
                            {
                                options.Players.Enabled = true;
                                options.Skills.Enabled = true;
                                options.Teams.Enabled = true;
                                options.Rewards.Enabled = true;
                                options.Uniforms.Enabled = true;
                                options.Npcs.Enabled = true;
                                options.Players.SwapStoryPlayerVisualIdentities = true;
                                options.Skills.RandomizeSkillParameters = true;
                                options.Teams.RandomizeLevels = true;
                                options.Rewards.RandomizeShopItems = true;
                            }
                            else if (preset is "players" or "jugadores")
                            {
                                options.Players.Enabled = true;
                                options.Skills.Enabled = false;
                                options.Teams.Enabled = false;
                                options.Rewards.Enabled = false;
                                options.Uniforms.Enabled = false;
                                options.Npcs.Enabled = false;
                            }
                            else
                            {
                                // "balanced" / "standard"
                                options.Players.Enabled = true;
                                options.Skills.Enabled = true;
                                options.Teams.Enabled = true;
                                options.Rewards.Enabled = true;
                                options.Uniforms.Enabled = true;
                                options.Npcs.Enabled = true;
                                options.Players.SwapStoryPlayerVisualIdentities = false;
                                options.Skills.RandomizeSkillParameters = false;
                                options.Teams.RandomizeLevels = false;
                                options.Rewards.RandomizeShopItems = false;
                            }
                        }
                        break;
                    case "--no-blind":
                        options.BlindPlayMode = false;
                        break;
                    case "--no-npcs":
                        options.Npcs.Enabled = false;
                        break;
                    case "--no-story-skill-swap":
                        options.Skills.EnableDynamicStoryTechniqueSwap = false;
                        break;
                    case "--no-starter-guarantee":
                        options.Skills.GuaranteeInitialMoveOrSpiritForAllPlayers = false;
                        options.Skills.GuaranteeProtagonistInitialSkill = false;
                        break;
                    case "--config":
                        if (i + 1 < args.Length) configJsonPath = args[++i];
                        break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                }
            }

            if (diagnoseMode)
            {
                return RunDiagnosticCli(inputPath, options.Edition, outputDir, jsonOutput);
            }

            if (!string.IsNullOrWhiteSpace(inspectCiaPath))
            {
                return RunInspectCiaCli(inspectCiaPath, options.AesKeysPath, exportDiagJsonPath, jsonOutput);
            }

            if (!string.IsNullOrWhiteSpace(mergeSourceCiaPath))
            {
                string targetMergeCia = !string.IsNullOrWhiteSpace(outputCiaPath) ? outputCiaPath : outputDir;
                return RunMergeCiaCli(mergeSourceCiaPath, mergeRomFsDir, targetMergeCia, options.AesKeysPath, options.Cia);
            }

            if (!string.IsNullOrWhiteSpace(repairInstalledPath))
            {
                if (string.IsNullOrWhiteSpace(cleanReferencePath) || string.IsNullOrWhiteSpace(outputDir))
                {
                    Console.Error.WriteLine("Error: --repair-installed requiere --clean-reference <base_limpia> y --output <repaired_ie_a.fa>.");
                    return 2;
                }
                try
                {
                    Console.WriteLine("================================================================");
                    Console.WriteLine("  IEGO RANDOMIZER v1.2.3 — REPARACIÓN QUIRÚRGICA DE CONSOLA 2DS");
                    Console.WriteLine("================================================================");
                    var rep = InstalledV122RepairService.RepairAndBuildDeltaPack(
                        repairInstalledPath,
                        cleanReferencePath,
                        outputDir,
                        options.Export.Export2dsDeltaPatchPackPath,
                        options.AesKeysPath);

                    Console.WriteLine($"  item_config ITEM_REF restaurado : {rep.RebuiltItemConfigPreservingRefs}");
                    Console.WriteLine($"  system_text 3-col restaurados   : {rep.RepairedSystemTextFilesCount}");
                    Console.WriteLine($"  NPCs/cinemáticas sincronizados  : {rep.RestoredMapNpcHeadsCount}");
                    Console.WriteLine($"  Caras waza cnXXXX sincronizadas : {rep.SyncedCnWazaFacesCount}");
                    Console.WriteLine($"  Rig cp0010 (Victor 903) fijado  : {rep.RepairedVictorCp0010BodyRig}");
                    Console.WriteLine($"  Ítems dummy de equipos limpiados: {rep.SanitizedDummyTeamItemsCount}");
                    if (rep.DeltaPack != null)
                    {
                        Console.WriteLine($"  Paquete ligero 2DS IE4P         : {rep.DeltaPack.OutputPackPath} ({rep.DeltaPack.PackSizeBytes / 1024.0:F1} KB)");
                        Console.WriteLine($"  Archivos reutilizados en 2DS    : {rep.DeltaPack.ReusedInstalledFiles} / {rep.DeltaPack.TotalFileEntries}");
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR REPARACIÓN] {ex}");
                    return 1;
                }
            }

            if (!string.IsNullOrWhiteSpace(buildSeedRecipePath))
            {
                if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(cleanReferencePath))
                {
                    Console.Error.WriteLine("Error: --build-2ds-seed-recipe requiere --input <instalado_cia_o_fa> y --clean-reference <base_limpia>.");
                    return 2;
                }
                try
                {
                    Console.WriteLine("================================================================");
                    Console.WriteLine("  IEGO RANDOMIZER v1.2.6 — RECETA DE SEMILLA EN CONSOLA 2DS");
                    Console.WriteLine("================================================================");
                    var rec = OnConsoleSeedRecipeBuilder.BuildRecipe(
                        inputPath,
                        cleanReferencePath,
                        buildSeedRecipePath,
                        options.AesKeysPath);

                    Console.WriteLine($"  Receta IE4RV100 generada        : {rec.OutputRecipePath}");
                    Console.WriteLine($"  Tamaño comprimido / descomprim. : {rec.CompressedSizeBytes / 1024.0:F1} KB / {rec.UncompressedSizeBytes / 1024.0:F1} KB");
                    Console.WriteLine($"  Slots reutilizados de consola   : {rec.ReusedInstalledSlotsCount} / {rec.TotalArc0Entries}");
                    Console.WriteLine($"  Plantillas mutables / blobs     : {rec.MutableTemplatesCount} / {rec.EmbeddedFallbackBlobsCount}");
                    Console.WriteLine($"  Identidades visuales swapables  : {rec.VisualSwapMembersCount}");
                    Console.WriteLine($"  Jugadores / Equipos 11v11 / 5v5 : {rec.PlayablePlayersCount} / {rec.StoryTeamsCount} / {rec.EncountTeamsCount}");
                    Console.WriteLine($"  Spawns mapa / PalPack / Cofres  : {rec.MapSpawnsCount} / {rec.PalpackCardsCount} / {rec.TreasureChestsCount}");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR RECETA 2DS] {ex}");
                    return 1;
                }
            }

            if (!string.IsNullOrWhiteSpace(configJsonPath) && File.Exists(configJsonPath))
            {
                string rawJson = File.ReadAllText(configJsonPath);
                string savedKeys = options.AesKeysPath;
                options = JsonSerializer.Deserialize<RandomizerOptions>(rawJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? options;
                if (!string.IsNullOrWhiteSpace(savedKeys))
                    options.AesKeysPath = savedKeys;
            }

            string resolvedOutCia = outputCiaPath;
            if (string.IsNullOrWhiteSpace(resolvedOutCia) &&
                !string.IsNullOrWhiteSpace(inputPath) &&
                inputPath.EndsWith(".cia", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(outputDir) &&
                outputDir.EndsWith(".cia", StringComparison.OrdinalIgnoreCase))
            {
                resolvedOutCia = outputDir;
            }

            if (!string.IsNullOrWhiteSpace(resolvedOutCia))
            {
                return RunRandomizeCiaCli(inputPath, resolvedOutCia, options);
            }

            if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputDir))
            {
                Console.Error.WriteLine("Error: Debe indicar --input <ruta> y --output <carpeta_salida>.");
                PrintHelp();
                return 2;
            }

            try
            {
                Console.WriteLine("================================================================");
                Console.WriteLine("  IEGO RANDOMIZER v1.2.2 — Inazuma Eleven GO: Luz y Sombra (CLI)");
                Console.WriteLine("================================================================");

                var coordinator = new RandomizerCoordinator();
                int lastPrintedPct = -1;
                string lastStage = string.Empty;
                var progress = new Progress<RandomizerProgressStep>(step =>
                {
                    if (step.Percent > lastPrintedPct || (step.Stage != lastStage && !step.Stage.StartsWith("Escribiendo archivo ARC0")))
                    {
                        lastPrintedPct = Math.Max(lastPrintedPct, step.Percent);
                        lastStage = step.Stage;
                        Console.WriteLine($"[{lastPrintedPct,3}%] {step.Stage}");
                    }
                });

                var report = coordinator.GenerateRun(inputPath, outputDir, options, progress);

                Console.WriteLine();
                Console.WriteLine("Generación finalizada correctamente:");
                Console.WriteLine($"  Semilla (Seed)          : {report.Seed}");
                Console.WriteLine($"  Huella de verificación  : {report.SeedFingerprint}");
                Console.WriteLine($"  Edición / Title ID      : {report.EffectiveEdition} ({report.ResolvedTitleId})");
                Console.WriteLine($"  Modo Partida a Ciegas   : {(report.BlindPlayMode ? "ACTIVADO (Sin spoilers)" : "Desactivado")}");
                Console.WriteLine($"  Original sin modificar  : {(report.SourceImmutabilityVerified ? "SÍ (SHA-256 idéntico)" : "NO")}");
                Console.WriteLine($"  Integridad verificada   : {(report.OutputIntegrityVerified ? "SÍ" : "NO")}");
                if (report.PersistedDiffTelemetry != null)
                {
                    var d = report.PersistedDiffTelemetry;
                    Console.WriteLine($"  Cambios reales en disco : {d.PersistedModifiedFilesCount} archivos internos modificados " +
                                      $"(jugadores={d.PersistedChangedCharaParamsCount}, técnicas={d.PersistedChangedSkillTableEntriesCount}, " +
                                      $"rivales={d.PersistedChangedStoryTeamsCount}, pachangas={d.PersistedChangedPachangaTeamsCount}, " +
                                      $"equipaciones/params={d.PersistedChangedTeamParamsCount})");
                }
                if (!string.IsNullOrEmpty(report.DirectCitraInstalledPath))
                {
                    Console.WriteLine($"  Instalado en Citra      : {report.DirectCitraInstalledPath}");
                }
                Console.WriteLine($"  Carpeta de exportación  : {Path.GetFullPath(outputDir)}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"[ERROR] {ex.Message}");
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static int RunDiagnosticCli(string inputPath, GameEdition edition, string targetOrCitraDir, bool jsonOutput)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Console.Error.WriteLine("Error: Debe indicar --input <ruta> junto con --diagnose.");
                return 2;
            }

            try
            {
                var diag = LocalDiagnosticService.RunDiagnostics(inputPath, edition, targetOrCitraDir);
                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                Console.WriteLine("================================================================");
                Console.WriteLine("  IEGO RANDOMIZER v1.0.2 — DIAGNÓSTICO LOCAL Y MODS DE CITRA");
                Console.WriteLine("================================================================");
                Console.WriteLine($"  Entrada analizada : {diag.InputPath}");
                Console.WriteLine($"  Tipo detectado    : {diag.InputKind}");
                if (!string.IsNullOrWhiteSpace(diag.BannerTitleSummary))
                {
                    Console.WriteLine($"  Banner SMDH 3DS   : {diag.BannerTitleSummary}");
                }
                if (diag.HasValidIeAFa)
                {
                    Console.WriteLine($"  SHA-256 ie_a.fa   : {diag.IeAFaSha256Hex}");
                    Console.WriteLine($"  Edición / TitleID : {diag.DetectedEdition} ({diag.ExpectedPrimaryTitleId})");
                }
                Console.WriteLine();
                foreach (string f in diag.Findings)
                {
                    Console.WriteLine($"  [OK] {f}");
                }
                foreach (string w in diag.ActionableWarnings)
                {
                    Console.WriteLine($"  [AVISO] {w}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR DIAGNÓSTICO] {ex.Message}");
                return 1;
            }
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Uso (IEGO Randomizer v1.2.2 — Edición Nintendo 2DS/3DS & PC):");
            Console.WriteLine("  IEGORandomizer                                                         Inicia la interfaz gráfica de escritorio (PC)");
            Console.WriteLine("  IEGORandomizer -i <original.cia> --output-cia <nuevo.cia> [opciones]   Modo Principal: .cia/.piratelegit.cia -> randomizar -> actualización FBI .cia");
            Console.WriteLine("  IEGORandomizer --merge-cia <original.cia> --romfs <dir> --output-cia <nuevo.cia>  Segundo Modo: .cia + carpeta RomFS -> nuevo .cia");
            Console.WriteLine("  IEGORandomizer --inspect-cia <original.cia> [--export-diag <out.json>] Inspección local de solo lectura del .cia");
            Console.WriteLine("  IEGORandomizer --cli -i <entrada> -o <carpeta_salida> [opciones]       Modo Clásico (LayeredFS / Citra / Luma)");
            Console.WriteLine("  IEGORandomizer --diagnose -i <entrada> [-o <carpeta_salida_o_citra>] [--json]");
            Console.WriteLine();
            Console.WriteLine("Opciones CLI:");
            Console.WriteLine("  -i, --input <ruta>           Ruta al archivo .cia / .piratelegit.cia, .3ds desencriptado, carpeta RomFS o ie_a.fa");
            Console.WriteLine("  --output-cia <nuevo.cia>     Ruta del nuevo archivo .cia verificado de salida (listo para FBI en 2DS/3DS)");
            Console.WriteLine("  --fbi-update                 (Por defecto) Incrementa TitleVersion TMD (V.1.2.2) para actualizar encima en FBI conservando partida");
            Console.WriteLine("  --cia-version <X.Y.Z>        Fija una versión TMD explícita para FBI (ej. 1.2.0 o 1056)");
            Console.WriteLine("  --keep-cia-version           Conserva exactamente el TitleVersion original (ej. V.0.0.0)");
            Console.WriteLine("  --companion-luma             Exporta también Luma3DS/titles/<TitleID>/romfs/ie_a.fa junto al .cia");
            Console.WriteLine("  --no-npcs                    Desactiva randomización de Entrenadores, Gerentes y NPCs");
            Console.WriteLine("  --no-story-skill-swap        Desactiva intercambio dinámico de técnicas en scripts .nutb");
            Console.WriteLine("  --no-starter-guarantee       Desactiva garantía de 1 técnica/EG inicial en todos los jugadores");
            Console.WriteLine("  --romfs <carpeta_romfs>      Carpeta RomFS modificada cuyos archivos se fusionarán en el .cia");
            Console.WriteLine("  --aes-keys <aes_keys.txt>    Opcional: aes_keys.txt personalizado (los .cia/.piratelegit.cia retail funcionan sin él)");
            Console.WriteLine("  -o, --output <carpeta>       Carpeta de destino separada (modo clásico LayeredFS)");
            Console.WriteLine("  -s, --seed <texto>           Semilla manual reproducible (opcional)");
            Console.WriteLine("  -e, --edition <modo>         auto | luz | sombra");
            Console.WriteLine("  --install-citra [ruta_mods]  Instala también romfs/ie_a.fa directo en los mods de Citra");
            Console.WriteLine("  --diagnose                   Analiza el archivo/RomFS y verifica la carpeta de mods de Citra");
            Console.WriteLine("  --no-blind                   Desactiva el modo a ciegas y escribe spoiler_log.txt");
            Console.WriteLine("  --config <json>              Carga opciones avanzadas desde un archivo JSON");
        }

        private static int RunInspectCiaCli(string ciaPath, string aesKeysPath, string exportDiagPath, bool jsonOutput)
        {
            try
            {
                var svc = new CiaWorkflowService();
                var rep = svc.InspectLocalCia(ciaPath, aesKeysPath, computeFullSha256: true);
                if (!string.IsNullOrWhiteSpace(exportDiagPath))
                {
                    svc.SaveRomFreeDiagnosticJson(rep, exportDiagPath);
                }

                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(rep, new JsonSerializerOptions { WriteIndented = true }));
                    return rep.IsValidCiaStructure ? 0 : 1;
                }

                Console.WriteLine("================================================================");
                Console.WriteLine("  IEGO RANDOMIZER v1.2.2 — INSPECCIÓN LOCAL .CIA (SOLO LECTURA)");
                Console.WriteLine("================================================================");
                Console.WriteLine($"  Archivo analizado : {rep.FilePath} ({rep.FileSizeBytes:N0} B)");
                Console.WriteLine($"  SHA-256 original  : {rep.Sha256Hex}");
                Console.WriteLine($"  Estructura CIA    : {(rep.IsValidCiaStructure ? "VÁLIDA" : "INVÁLIDA")}");
                Console.WriteLine($"  Estado de cifrado : {rep.EncryptionKind}");
                Console.WriteLine($"  Procesable ahora  : {(rep.CanProcessNow ? "SÍ" : "NO")}");
                Console.WriteLine($"  TitleID / Versión : {rep.TmdTitleIdHex} | {rep.TmdTitleVersionFormatted} (0x{rep.TmdTitleVersionRaw:X4})");
                Console.WriteLine($"  Edición / Código  : {rep.DetectedEdition} / {rep.ProductCode}");
                Console.WriteLine($"  SaveDataSize TMD  : {rep.SaveDataSizeBytes / 1024} KB | Contenidos: {rep.ContentCount}");
                if (rep.RomFsReadable)
                {
                    Console.WriteLine($"  Inventario RomFS  : {rep.RomFsFileCount} archivos en {rep.RomFsDirectoryCount} carpetas (ie_a.fa={rep.HasIeAFa}, fix.xr={rep.HasFixXr})");
                }
                foreach (string f in rep.Findings) Console.WriteLine($"  [OK] {f}");
                foreach (string w in rep.LimitationsAndWarnings) Console.WriteLine($"  [INFO] {w}");
                if (!string.IsNullOrWhiteSpace(exportDiagPath))
                    Console.WriteLine($"  Informe sin ROM   : {Path.GetFullPath(exportDiagPath)}");
                return rep.IsValidCiaStructure ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR INSPECCIÓN .CIA] {ex.Message}");
                return 1;
            }
        }

        private static int RunRandomizeCiaCli(string sourceCiaPath, string outputCiaPath, RandomizerOptions options)
        {
            if (string.IsNullOrWhiteSpace(sourceCiaPath) || string.IsNullOrWhiteSpace(outputCiaPath))
            {
                Console.Error.WriteLine("Error: Debe indicar -i <original.cia> y --output-cia <nuevo.cia>.");
                return 2;
            }

            try
            {
                Console.WriteLine("================================================================");
                Console.WriteLine("  IEGO RANDOMIZER v1.2.2 — MODO PRINCIPAL (.CIA -> ACTUALIZACIÓN FBI .CIA)");
                Console.WriteLine("================================================================");

                var svc = new CiaWorkflowService();
                int lastPct = -1;
                string lastStage = string.Empty;
                var progress = new Progress<RandomizerProgressStep>(step =>
                {
                    if (step.Percent > lastPct ||
                        (step.Stage != lastStage &&
                         !step.Stage.StartsWith("Empaquetando RomFS") &&
                         !step.Stage.StartsWith("Escribiendo archivo ARC0")))
                    {
                        lastPct = Math.Max(lastPct, step.Percent);
                        lastStage = step.Stage;
                        Console.WriteLine($"[{lastPct,3}%] {step.Stage}");
                    }
                });

                var report = svc.RandomizeCiaToCia(sourceCiaPath, outputCiaPath, options, progress);
                var t = report.CiaBuildTelemetry;
                var v = report.CiaVerification;

                Console.WriteLine();
                Console.WriteLine("Nuevo archivo .cia generado y verificado en frío:");
                Console.WriteLine($"  Semilla (Seed)          : {report.Seed} (Huella: {report.SeedFingerprint})");
                Console.WriteLine($"  Edición / Title ID      : {report.EffectiveEdition} ({t.TitleIdHex} | {t.SourceTitleVersionFormatted} -> {t.TitleVersionFormatted})");
                Console.WriteLine($"  Actualización FBI 2DS   : {(t.IsFbiInPlaceUpdateReady ? "LISTA (Sobrescribe juego sin borrar partida 00000001.sav)" : $"Versión preservada ({t.TitleVersionFormatted})")}");
                Console.WriteLine($"  Original sin modificar  : SÍ (SHA-256 idéntico: {t.SourceCiaSha256Hex})");
                Console.WriteLine($"  Nuevo .cia generado     : {t.OutputCiaPath} ({t.OutputCiaSizeBytes:N0} B)");
                Console.WriteLine($"  SHA-256 nuevo .cia      : {t.OutputCiaSha256Hex}");
                Console.WriteLine($"  RomFS preservada total  : {t.OutputRomFsFileCount} archivos (original: {t.SourceRomFsFileCount})");
                Console.WriteLine($"  Verificación IVFC/TMD   : {(v.IsValid ? "SUPERADA" : "ERROR")} ({v.VerifiedIvfcBlocksCount} bloques SHA-256, {t.PreservedContentsCount} contenidos)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"[ERROR] {ex.Message}");
                return 1;
            }
        }

        private static int RunMergeCiaCli(string sourceCiaPath, string modRomFsDir, string outputCiaPath, string aesKeysPath, CiaPackageOptions ciaOptions)
        {
            if (string.IsNullOrWhiteSpace(sourceCiaPath) || string.IsNullOrWhiteSpace(modRomFsDir) || string.IsNullOrWhiteSpace(outputCiaPath))
            {
                Console.Error.WriteLine("Error: Debe indicar --merge-cia <original.cia> --romfs <carpeta_romfs> --output-cia <nuevo.cia>.");
                return 2;
            }

            try
            {
                Console.WriteLine("================================================================");
                Console.WriteLine("  IEGO RANDOMIZER v1.2.2 — MODO 2 (.CIA + ROMFS -> NUEVO .CIA)");
                Console.WriteLine("================================================================");

                var svc = new CiaWorkflowService();
                int lastPct = -1;
                var progress = new Progress<RandomizerProgressStep>(step =>
                {
                    if (step.Percent > lastPct)
                    {
                        lastPct = step.Percent;
                        Console.WriteLine($"[{lastPct,3}%] {step.Stage}");
                    }
                });

                var res = svc.MergeModifiedRomFsIntoCia(sourceCiaPath, modRomFsDir, outputCiaPath, aesKeysPath, progress, ciaOptions);
                var t = res.RebuildTelemetry;
                var v = res.Verification;

                Console.WriteLine();
                Console.WriteLine("Fusión de carpeta RomFS + .cia completada y verificada en frío:");
                Console.WriteLine($"  Original sin modificar  : SÍ (SHA-256 idéntico: {t.SourceCiaSha256Hex})");
                Console.WriteLine($"  Archivos incorporados   : {t.OverlaidModifiedRomFsFilesCount} archivo(s) desde {res.ModifiedRomFsDirectory}");
                Console.WriteLine($"  RomFS final en el .cia  : {t.OutputRomFsFileCount} archivos totales (original: {t.SourceRomFsFileCount})");
                Console.WriteLine($"  Nuevo .cia generado     : {t.OutputCiaPath} ({t.OutputCiaSizeBytes:N0} B)");
                Console.WriteLine($"  Verificación IVFC/TMD   : {(v.IsValid ? "SUPERADA" : "ERROR")} ({v.VerifiedIvfcBlocksCount} bloques SHA-256)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"[ERROR] {ex.Message}");
                return 1;
            }
        }
    }
}
