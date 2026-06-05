using System.Net.Http.Json;
using System.Text.Json;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Handles all GitHub API communication for checking releases and downloading assets.
/// </summary>
public class GitHubService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GitHubService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("User-Agent", "SlotWeave.Launcher/1.0");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo)
    {
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=10";
            var releases = await _http.GetFromJsonAsync<List<GitHubRelease>>(url, JsonOpts);

            if (releases == null || releases.Count == 0)
                return null;

            return releases
                .Where(r => !r.Prerelease)
                .OrderByDescending(r => r.PublishedAt)
                .FirstOrDefault()
                ?? releases.OrderByDescending(r => r.PublishedAt).First();
        }
        catch (HttpRequestException ex)
        {
            ConsoleUI.ShowError(Loc.T("error.github_api_failed", owner, repo, ex.Message));
            return null;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("error.github_error", owner, repo, ex.Message));
            return null;
        }
    }

    public GitHubAsset? FindMatchingAsset(GitHubRelease release, string assetPattern)
    {
        if (release.Assets.Count == 0)
            return null;

        var exact = release.Assets.FirstOrDefault(a =>
            a.Name.Equals(assetPattern, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(assetPattern)
            .Replace("\\*", ".*") + "$";
        var regex = new System.Text.RegularExpressions.Regex(regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name))
            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> DownloadAssetAsync(
        string downloadUrl,
        string destinationPath,
        Action<long, long>? progressCallback = null,
        CancellationToken ct = default,
        List<string>? mirrors = null)
    {
        // Build URL list: direct first, then mirrors
        var urls = new List<string> { downloadUrl };
        if (mirrors != null)
        {
            foreach (var mirror in mirrors)
            {
                var mirrorUrl = mirror.TrimEnd('/') + "/" + downloadUrl;
                if (!urls.Contains(mirrorUrl))
                    urls.Add(mirrorUrl);
            }
        }

        for (int i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            if (i > 0)
            {
                Console.Write($"\r{Loc.T("status.trying_mirror", i)} ");
                progressCallback?.Invoke(0, -1);
            }

            try
            {
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var response = await _http.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                    continue;

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                    progressCallback?.Invoke(totalRead, totalBytes);
                }

                if (totalBytes < 0)
                    progressCallback?.Invoke(totalRead, totalRead);

                return true;
            }
            catch (OperationCanceledException)
            {
                ConsoleUI.ShowInfo(Loc.T("error.download_cancelled"));
                TryDelete(destinationPath);
                return false;
            }
            catch (Exception)
            {
                // Try next mirror
            }
        }

        // All mirrors exhausted
        ConsoleUI.ShowError(Loc.T("error.download_failed"));
        TryDelete(destinationPath);
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
