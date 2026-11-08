using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Modules;

namespace IEGORandomizer.App.Services
{
    public sealed class RandomizerJobState
    {
        public string JobId { get; init; } = string.Empty;
        public string Status { get; set; } = "running"; // running | completed | failed
        public int Percent { get; set; }
        public string CurrentStage { get; set; } = "Iniciando...";
        public string ErrorMessage { get; set; }
        public RandomizationExecutionReport Report { get; set; }
    }

    public sealed class GenerateRunRequest
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public RandomizerOptions Options { get; set; } = new();
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
                    state.ErrorMessage = ex.Message;
                    state.CurrentStage = "Proceso detenido por validación o error de entrada.";
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
