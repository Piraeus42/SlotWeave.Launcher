using System.Security.Cryptography;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Verification;

/// <summary>
/// Verifies a file's SHA256 hash against an expected value.
/// Used for download integrity checks when GitHub provides asset digests.
/// </summary>
public class Sha256Verifier : IIntegrityVerifier
{
    public async Task<Result<bool>> VerifyAsync(string filePath, string? expectedHash = null)
    {
        if (!File.Exists(filePath))
            return Result<bool>.Failure($"File not found: {filePath}");

        try
        {
            using var sha256 = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            if (expectedHash == null)
                return Result<bool>.Success(true);

            var expected = expectedHash.ToLowerInvariant();
            return hash == expected
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(
                    $"SHA256 mismatch.\nExpected: {expected}\nActual:   {hash}");
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Hash computation error: {ex.Message}");
        }
    }
}
