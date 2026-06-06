using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Pipeline;

/// <summary>
/// A single step in an operation pipeline (install, update, uninstall).
/// Each step has pre-condition check, execution, and rollback.
/// </summary>
public interface IOperationStep
{
    string Name { get; }

    /// <summary>
    /// Pre-condition check. Failure here does NOT trigger rollback
    /// (no steps have executed yet).
    /// </summary>
    Result CanExecute();

    /// <summary>
    /// Execute the step. May have side effects.
    /// </summary>
    Task<Result> ExecuteAsync(CancellationToken ct = default);

    /// <summary>
    /// Rollback the side effects of a previously-executed step.
    /// Called when a later step fails.
    /// </summary>
    Task<Result> RollbackAsync();
}
