using System;
using System.IO;
using System.Text.Json;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Configuration;
using Lynx.Core.RomFs;

namespace IEGORandomizer.App
{
    /// <summary>
    /// Headless command-line runner for batch execution and automated testing.
    /// </summary>
    public static class CliRunner
    {
        public static int Run(string[] args)
        {
            string inputPath = null;
            string outputDir = null;
            string configJsonPath = null;
            var options = new RandomizerOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--cli":
                        break;
                    case "--input":
                    case "-i":
                        if (i + 1 < args.Length) inputPath = args[++i];
                        break;
                    case "--output":
                    case "-o":
                        if (i + 1 < args.Length) outputDir = args[++i];
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
                    case "--no-blind":
                        options.BlindPlayMode = false;
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

            if (!string.IsNullOrWhiteSpace(configJsonPath) && File.Exists(configJsonPath))
            {
                string rawJson = File.ReadAllText(configJsonPath);
                options = JsonSerializer.Deserialize<RandomizerOptions>(rawJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? options;
            }

            if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputDir))
            {
                Console.Error.WriteLine("Error: Debe indicar --input <ruta> y --output <carpeta_salida>.");
                PrintHelp();
                return 2;
            }

            try
            {
                Console.WriteLine("============================================================");
                Console.WriteLine("  IEGO RANDOMIZER — Inazuma Eleven GO: Luz y Sombra (CLI)");
                Console.WriteLine("============================================================");

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

        public static void PrintHelp()
        {
            Console.WriteLine("Uso:");
            Console.WriteLine("  IEGORandomizer.App                       Inicia la interfaz gráfica de escritorio (PC)");
            Console.WriteLine("  IEGORandomizer.App --cli -i <entrada> -o <carpeta_salida> [opciones]");
            Console.WriteLine();
            Console.WriteLine("Opciones CLI:");
            Console.WriteLine("  -i, --input <ruta>       Ruta al archivo .3ds desencriptado, carpeta RomFS o ie_a.fa");
            Console.WriteLine("  -o, --output <carpeta>   Carpeta de destino separada para los archivos generados");
            Console.WriteLine("  -s, --seed <texto>       Semilla manual reproducible (opcional)");
            Console.WriteLine("  -e, --edition <modo>     auto | luz | sombra");
            Console.WriteLine("  --no-blind               Desactiva el modo a ciegas y escribe spoiler_log.txt");
            Console.WriteLine("  --config <json>          Carga opciones avanzadas desde un archivo JSON");
        }
    }
}
