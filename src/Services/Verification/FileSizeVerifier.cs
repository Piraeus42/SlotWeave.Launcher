using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Verification;

/// <summary>
/// Verifies a file's size against an expected byte count.
/// Quick sanity check before deeper verification.
/// </summary>
public class FileSizeVerifier : IIntegrityVerifier
{
    public Task<Result<bool>> VerifyAsync(string filePath, string? expectedSizeStr = null)
    {
        if (!File.Exists(filePath))
            return Task.FromResult(Result<bool>.Failure($"File not found: {filePath}"));

        var fileInfo = new FileInfo(filePath);

        if (expectedSizeStr == null)
            return Task.FromResult(Result<bool>.Success(true));

        if (!long.TryParse(expectedSizeStr, out var expectedSize))
            return Task.FromResult(Result<bool>.Failure($"Invalid expected size: {expectedSizeStr}"));

        return fileInfo.Length == expectedSize
            ? Task.FromResult(Result<bool>.Success(true))
            : Task.FromResult(Result<bool>.Failure(
                $"File size mismatch.\nExpected: {expectedSize:N0} bytes\nActual:   {fileInfo.Length:N0} bytes"));
    }
}
