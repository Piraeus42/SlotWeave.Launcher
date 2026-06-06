using SlotWeave.Launcher.Models;
using SlotWeave.Launcher.Services.Verification;

namespace SlotWeave.Launcher.Services.Pipeline.Steps;

/// <summary>
/// Verifies a downloaded file using one or more integrity verifiers.
/// Used in self-update and component install pipelines.
/// </summary>
public class VerifyDownloadStep : IOperationStep
{
    private readonly string _filePath;
    private readonly IIntegrityVerifier _verifier;
    private readonly string? _expectedValue;

    public string Name => "Verify download";

    public VerifyDownloadStep(
        string filePath,
        IIntegrityVerifier verifier,
        string? expectedValue = null)
    {
        _filePath = filePath;
        _verifier = verifier;
        _expectedValue = expectedValue;
    }

    public Result CanExecute()
    {
        return File.Exists(_filePath)
            ? Result.Success()
            : Result.Failure($"File not found: {_filePath}");
    }

    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var result = await _verifier.VerifyAsync(_filePath, _expectedValue);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.Error!);
    }

    public Task<Result> RollbackAsync()
    {
        // Verification has no side effects — nothing to roll back
        return Task.FromResult(Result.Success());
    }
}
