using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Pipeline;

/// <summary>
/// Executes a sequence of IOperationSteps with automatic rollback on failure.
///
/// Usage:
///   var pipeline = new OperationPipeline()
///       .AddStep(new DownloadAssetStep(...))
///       .AddStep(new VerifyDownloadStep(...))
///       .AddStep(new ExtractStep(...));
///   var result = await pipeline.ExecuteAsync(ct);
///   if (!result.IsSuccess) { /* handle failure */ }
/// </summary>
public class OperationPipeline
{
    private readonly List<IOperationStep> _steps = new();
    private readonly Stack<IOperationStep> _executedSteps = new();

    public OperationPipeline AddStep(IOperationStep step)
    {
        _steps.Add(step);
        return this;
    }

    public async Task<PipelineResult> ExecuteAsync(CancellationToken ct = default)
    {
        _executedSteps.Clear();

        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();

            // Pre-condition check (failure here → no rollback needed)
            var canExecute = step.CanExecute();
            if (!canExecute.IsSuccess)
            {
                ConsoleUI.ShowError($"Pre-condition failed [{step.Name}]: {canExecute.Error}");
                return PipelineResult.Failure(canExecute.Error!, _executedSteps.ToList());
            }

            // Execute
            ConsoleUI.ShowInfo($"[{step.Name}]");

            var result = await step.ExecuteAsync(ct);

            if (!result.IsSuccess)
            {
                ConsoleUI.ShowError($"Step failed [{step.Name}]: {result.Error}");
                await RollbackAsync();
                return PipelineResult.Failure(result.Error!, _executedSteps.ToList());
            }

            _executedSteps.Push(step);
        }

        return PipelineResult.Success(_executedSteps.ToList());
    }

    private async Task RollbackAsync()
    {
        if (_executedSteps.Count == 0)
            return;

        ConsoleUI.ShowWarning($"Rolling back {_executedSteps.Count} executed step(s)...");

        while (_executedSteps.TryPop(out var step))
        {
            try
            {
                var rollback = await step.RollbackAsync();
                if (!rollback.IsSuccess)
                    ConsoleUI.ShowError($"Rollback failed [{step.Name}]: {rollback.Error}");
                else
                    ConsoleUI.ShowInfo($"Rolled back [{step.Name}]");
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowError($"Rollback exception [{step.Name}]: {ex.Message}");
            }
        }
    }
}
