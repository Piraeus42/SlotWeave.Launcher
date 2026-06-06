using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Verification;

/// <summary>
/// File integrity verification contract.
/// Verifiers can check file existence, PE headers, hashes, sizes, etc.
/// </summary>
public interface IIntegrityVerifier
{
    Task<Result<bool>> VerifyAsync(string filePath, string? expectedValue = null);
}
