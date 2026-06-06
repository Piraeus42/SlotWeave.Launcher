using SlotWeave.Launcher.Services.Pipeline;

namespace SlotWeave.Launcher.Models;

/// <summary>
/// Operation result — success or failure with error message.
/// No ComponentState coupling; generic enough for all operation layers.
/// </summary>
public readonly struct Result
{
    public bool IsSuccess { get; }
    public string? Error { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(string error) => new(false, error);

    private Result(bool success, string? error)
    {
        IsSuccess = success;
        Error = error;
    }
}

/// <summary>
/// Operation result with a typed value on success.
/// </summary>
public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }

    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);

    private Result(bool success, T? value, string? error)
    {
        IsSuccess = success;
        Value = value;
        Error = error;
    }
}

/// <summary>
/// Pipeline result — carries the list of executed steps so the caller
/// can decide rollback strategy based on what actually ran.
/// </summary>
public readonly struct PipelineResult
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public IReadOnlyList<IOperationStep> ExecutedSteps { get; }

    public static PipelineResult Success(IReadOnlyList<IOperationStep> executedSteps)
        => new(true, null, executedSteps);

    public static PipelineResult Failure(string error, IReadOnlyList<IOperationStep> executedSteps)
        => new(false, error, executedSteps);

    private PipelineResult(bool success, string? error, IReadOnlyList<IOperationStep> executedSteps)
    {
        IsSuccess = success;
        Error = error;
        ExecutedSteps = executedSteps;
    }
}
