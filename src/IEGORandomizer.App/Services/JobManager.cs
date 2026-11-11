using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Cia;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Modules;

namespace IEGORandomizer.App.Services
{
    public sealed class RandomizerJobState
    {
        public string JobId { get; init; } = string.Empty;
        public string JobMode { get; init; } = "classic"; // classic | cia-randomize | cia-merge
        public string Status { get; set; } = "running"; // running | completed | failed
        public int Percent { get; set; }
        public string CurrentStage { get; set; } = "Iniciando...";
        public string ErrorMessage { get; set; }
        public string ErrorDetails { get; set; }
        public RandomizationExecutionReport Report { get; set; }
        public CiaMergeExecutionReport MergeReport { get; set; }
    }

    public sealed class GenerateRunRequest
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public RandomizerOptions Options { get; set; } = new();
    }

    public sealed class CiaRandomizeRunRequest
    {
        public string SourceCiaPath { get; set; } = string.Empty;
        public string OutputCiaPath { get; set; } = string.Empty;
        public RandomizerOptions Options { get; set; } = new();
    }

    public sealed class CiaMergeRunRequest
    {
        public string SourceCiaPath { get; set; } = string.Empty;
        public string ModifiedRomFsDirectory { get; set; } = string.Empty;
        public string OutputCiaPath { get; set; } = string.Empty;
        public string AesKeysPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Executes randomization jobs asynchronously on a background thread so the UI
    /// receives smooth real-time progress updates and clear error diagnostics.
    /// </summary>
    public sealed class JobManager
    {
        private readonly ConcurrentDictionary<string, RandomizerJobState> _jobs = new();

        public RandomizerJobState StartJob(GenerateRunRequest request)
        {
            string jobId = Guid.NewGuid().ToString("N");
            var state = new RandomizerJobState
            {
                JobId = jobId,
                Status = "running",
                Percent = 1,
                CurrentStage = "Preparando motor de randomización..."
            };
            _jobs[jobId] = state;

            _ = Task.Run(() =>
            {
                try
                {
                    var coordinator = new RandomizerCoordinator();
                    var progress = new Progress<RandomizerProgressStep>(step =>
                    {
                        state.Percent = step.Percent;
                        state.CurrentStage = step.Stage;
                    });

                    var report = coordinator.GenerateRun(
                        request.InputPath,
                        request.OutputDirectory,
                        request.Options ?? new RandomizerOptions(),
                        progress);

                    state.Report = report;
                    state.Percent = 100;
                    state.CurrentStage = "Generación completada con éxito.";
                    state.Status = "completed";
                }
                catch (Exception ex)
                {
                    string failedStage = state.CurrentStage;
                    state.ErrorMessage = ex.Message;
                    state.ErrorDetails = $"[{state.Percent}% — {failedStage}] {ex}";
                    state.CurrentStage = "Proceso detenido por validación o error de entrada.";
                    state.Status = "failed";
                }
            });

            return state;
        }

        public RandomizerJobState StartCiaRandomizeJob(CiaRandomizeRunRequest request)
        {
            string jobId = Guid.NewGuid().ToString("N");
            var state = new RandomizerJobState
            {
                JobId = jobId,
                JobMode = "cia-randomize",
                Status = "running",
                Percent = 1,
                CurrentStage = "Preparando flujo principal (.cia original -> randomizar -> nuevo .cia)..."
            };
            _jobs[jobId] = state;

            _ = Task.Run(() =>
            {
                try
                {
                    var ciaService = new CiaWorkflowService();
                    var progress = new Progress<RandomizerProgressStep>(step =>
                    {
                        state.Percent = step.Percent;
                        state.CurrentStage = step.Stage;
                    });

                    var report = ciaService.RandomizeCiaToCia(
                        request.SourceCiaPath,
                        request.OutputCiaPath,
                        request.Options ?? new RandomizerOptions(),
                        progress);

                    state.Report = report;
                    state.Percent = 100;
                    state.CurrentStage = "Nuevo archivo .cia randomizado y verificado en frío con éxito.";
                    state.Status = "completed";
                }
                catch (Exception ex)
                {
                    string failedStage = state.CurrentStage;
                    state.ErrorMessage = ex.Message;
                    state.ErrorDetails = $"[{state.Percent}% — {failedStage}] {ex}";
                    state.CurrentStage = "Reconstrucción .cia detenida por seguridad o validación.";
                    state.Status = "failed";
                }
            });

            return state;
        }

        public RandomizerJobState StartCiaMergeJob(CiaMergeRunRequest request)
        {
            string jobId = Guid.NewGuid().ToString("N");
            var state = new RandomizerJobState
            {
                JobId = jobId,
                JobMode = "cia-merge",
                Status = "running",
                Percent = 1,
                CurrentStage = "Preparando fusión (.cia original + carpeta RomFS modificada -> nuevo .cia)..."
            };
            _jobs[jobId] = state;

            _ = Task.Run(() =>
            {
                try
                {
                    var ciaService = new CiaWorkflowService();
                    var progress = new Progress<RandomizerProgressStep>(step =>
                    {
                        state.Percent = step.Percent;
                        state.CurrentStage = step.Stage;
                    });

                    var mergeReport = ciaService.MergeModifiedRomFsIntoCia(
                        request.SourceCiaPath,
                        request.ModifiedRomFsDirectory,
                        request.OutputCiaPath,
                        request.AesKeysPath,
                        progress);

                    state.MergeReport = mergeReport;
                    state.Percent = 100;
                    state.CurrentStage = "Fusión de carpeta RomFS + .cia completada y verificada en frío.";
                    state.Status = "completed";
                }
                catch (Exception ex)
                {
                    string failedStage = state.CurrentStage;
                    state.ErrorMessage = ex.Message;
                    state.ErrorDetails = $"[{state.Percent}% — {failedStage}] {ex}";
                    state.CurrentStage = "Fusión .cia detenida por seguridad o validación.";
                    state.Status = "failed";
                }
            });

            return state;
        }

        public RandomizerJobState GetJob(string jobId)
        {
            _jobs.TryGetValue(jobId ?? string.Empty, out var state);
            return state;
        }
    }
}
