using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Verification;

/// <summary>
/// Verifies that a file is a valid Windows PE executable.
/// Checks: minimum file size → MZ header → e_lfanew → PE signature.
/// Prevents catastrophic swap of a truncated/corrupted download.
/// </summary>
public class PeHeaderVerifier : IIntegrityVerifier
{
    private const int MinPeFileSize = 512;  // PE minimum size (DOS stub + headers)

    public Task<Result<bool>> VerifyAsync(string filePath, string? _ = null)
    {
        if (!File.Exists(filePath))
            return Task.FromResult(Result<bool>.Failure($"File not found: {filePath}"));

        try
        {
            var fileInfo = new FileInfo(filePath);

            // Basic size sanity: a truncated download won't have a valid PE structure
            if (fileInfo.Length < MinPeFileSize)
                return Task.FromResult(Result<bool>.Failure(
                    $"File size too small: {fileInfo.Length} bytes (min {MinPeFileSize})"));

            using var fs = File.OpenRead(filePath);

            // 1. Check MZ header (DOS stub magic)
            var mzHeader = new byte[2];
            if (fs.Read(mzHeader, 0, 2) != 2 || mzHeader[0] != 0x4D || mzHeader[1] != 0x5A)
                return Task.FromResult(Result<bool>.Failure("Not a valid PE file (missing MZ header)"));

            // 2. Read PE header offset (e_lfanew at offset 0x3C)
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffsetBytes = new byte[4];
            if (fs.Read(peOffsetBytes, 0, 4) != 4)
                return Task.FromResult(Result<bool>.Failure("Unable to read PE offset"));

            var peOffset = BitConverter.ToInt32(peOffsetBytes, 0);
            if (peOffset < 0 || peOffset > fileInfo.Length - 4)
                return Task.FromResult(Result<bool>.Failure($"PE offset out of range: {peOffset}"));

            // 3. Validate PE signature
            fs.Seek(peOffset, SeekOrigin.Begin);
            var peSig = new byte[4];
            if (fs.Read(peSig, 0, 4) != 4)
                return Task.FromResult(Result<bool>.Failure("Unable to read PE signature"));

            if (peSig[0] != 'P' || peSig[1] != 'E' || peSig[2] != 0 || peSig[3] != 0)
                return Task.FromResult(Result<bool>.Failure("Invalid PE signature"));

            return Task.FromResult(Result<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<bool>.Failure($"PE verification error: {ex.Message}"));
        }
    }
}
