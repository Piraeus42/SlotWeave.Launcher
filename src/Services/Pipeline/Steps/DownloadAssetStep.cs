using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services.Pipeline.Steps;

/// <summary>
/// Downloads a GitHub release asset to a local file.
/// Integrates with the existing GitHubService.DownloadAssetAsync.
/// </summary>
public class DownloadAssetStep : IOperationStep
{
    private readonly GitHubService _github;
    private readonly string _downloadUrl;
    private readonly string _destinationPath;
    private readonly Action<long, long>? _progressCallback;
    private readonly List<string>? _mirrors;

    public string Name => "Download";

    public DownloadAssetStep(
        GitHubService github,
        string downloadUrl,
        string destinationPath,
        Action<long, long>? progressCallback = null,
        List<string>? mirrors = null)
    {
        _github = github;
        _downloadUrl = downloadUrl;
        _destinationPath = destinationPath;
        _progressCallback = progressCallback;
        _mirrors = mirrors;
    }

    public Result CanExecute()
    {
        // Ensure the destination directory exists
        var dir = Path.GetDirectoryName(_destinationPath);
        if (dir != null && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { return Result.Failure($"Cannot create directory: {ex.Message}"); }
        }

        return Result.Success();
    }

    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var success = await _github.DownloadAssetAsync(
            _downloadUrl,
            _destinationPath,
            _progressCallback,
            ct,
            _mirrors);

        return success
            ? Result.Success()
            : Result.Failure("Download failed");
    }

    public Task<Result> RollbackAsync()
    {
        if (File.Exists(_destinationPath))
        {
            try { File.Delete(_destinationPath); }
            catch (Exception ex) { return Task.FromResult(Result.Failure($"Cannot delete download: {ex.Message}")); }
        }

        return Task.FromResult(Result.Success());
    }
}
