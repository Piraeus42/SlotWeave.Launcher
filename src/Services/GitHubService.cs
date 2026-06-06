using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Handles all GitHub API communication for checking releases and downloading assets.
/// Version checks use the releases Atom feed (no rate limit, no auth).
/// Asset lookups use the REST API (only when an update is actually performed).
/// </summary>
public class GitHubService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Atom feed XML namespace
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    public GitHubService(HttpClient http, string? token = null)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("User-Agent", "SlotWeave.Launcher/1.0");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _http.Timeout = TimeSpan.FromSeconds(30);

        // Authenticated requests get 5000/hr vs 60/hr unauthenticated.
        // Token is only needed when performing actual downloads (API asset lookup).
        token ??= Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    // ── Version check (Atom feed — zero rate limit) ────────────────

    /// <summary>
    /// Get the latest release version via the Atom feed.
    /// No API rate limit, no authentication required.
    /// Returns null if the feed can't be fetched or parsed.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(string owner, string repo)
    {
        try
        {
            var atomUrl = $"https://github.com/{owner}/{repo}/releases.atom";
            var xml = await _http.GetStringAsync(atomUrl);
            var doc = XDocument.Parse(xml);

            foreach (var entry in doc.Descendants(AtomNs + "entry"))
            {
                // Extract tag from <link> href: …/releases/tag/v1.0.3
                var link = entry.Element(AtomNs + "link");
                var href = link?.Attribute("href")?.Value ?? "";
                var tagPrefix = $"/{owner}/{repo}/releases/tag/";
                var idx = href.IndexOf(tagPrefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                var tag = href[(idx + tagPrefix.Length)..];
                // URL-decode in case tag contains special chars
                tag = Uri.UnescapeDataString(tag);

                // Skip prerelease-looking tags (semver: X.Y.Z-suffix)
                var version = tag.StartsWith('v') ? tag[1..] : tag;
                if (LooksLikePrerelease(version))
                    continue;

                return version;
            }
        }
        catch
        {
            // Fall through to API fallback
        }

        return null;
    }

    /// <summary>
    /// Heuristic: semver prereleases have a hyphen after the numeric part.
    /// e.g. "1.0.3-beta1", "2.0.0-rc.1", "1.0.0-alpha"
    /// </summary>
    private static bool LooksLikePrerelease(string version)
    {
        // Matches X.Y.Z-anything
        return System.Text.RegularExpressions.Regex.IsMatch(
            version, @"^\d+\.\d+\.\d+-");
    }

    // ── Full release info (REST API — 1 call per update) ──────────

    /// <summary>
    /// Fetch the full latest release from the REST API, including assets.
    /// This consumes 1 API request. Only call this when an update is
    /// actually being performed, not for routine version checks.
    /// </summary>
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

    // ── Asset matching ────────────────────────────────────────────

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
            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    // ── Download ──────────────────────────────────────────────────

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
