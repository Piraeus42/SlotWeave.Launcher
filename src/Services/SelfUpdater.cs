using System.Diagnostics;
using System.Text.Json;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Checks for launcher updates from GitHub (Atom feed, no rate limit)
/// and performs self-update via rename-based atomic swap.
/// </summary>
public class SelfUpdater
{
    private readonly GitHubService _github;
    private readonly LauncherConfig _config;
    private readonly string _launcherDir;
    private string? _latestVersion;
    private string? _downloadUrl;
    private string? _assetDigest;
    private bool _checked;
    private bool _urlResolved;

    public SelfUpdater(GitHubService github, LauncherConfig config, string launcherDir)
    {
        _github = github;
        _config = config;
        _launcherDir = launcherDir;
    }

    public bool HasUpdate => _checked && _latestVersion != null;
    public string? LatestVersion => _latestVersion;

    /// <summary>
    /// Check GitHub for a newer launcher release.
    /// Uses Atom feed — zero API calls, zero rate limit.
    /// Returns true if an update is available.
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        _checked = true;

        if (_config.LauncherRepo == null)
            return false;

        // Atom feed for version-only check (no rate limit)
        var remoteVersion = await _github.GetLatestVersionAsync(
            _config.LauncherRepo.Owner, _config.LauncherRepo.Repo);

        if (remoteVersion == null)
            return false;

        if (!IsNewer(remoteVersion, _config.LauncherVersion))
            return false;

        _latestVersion = remoteVersion;
        _urlResolved = false; // download URL resolved lazily on actual update
        return true;
    }

    /// <summary>
    /// Download the new launcher and replace the current exe via rename-based atomic swap.
    /// This method never returns — it exits the process after starting the new exe.
    /// </summary>
    public async Task<bool> UpdateAsync()
    {
        if (_latestVersion == null)
            return false;

        ConsoleUI.ShowHeader(Loc.T("selfupdate.updating"));

        var currentExe = Environment.ProcessPath;
        if (currentExe == null)
        {
            ConsoleUI.ShowError("Cannot determine current executable path.");
            return false;
        }

        var newExe = currentExe + ".new";
        var oldExe = currentExe + ".old";
        var exeDir = Path.GetDirectoryName(currentExe) ?? ".";

        // Resolve download URL (1 API call — only when actually updating)
        if (!_urlResolved)
        {
            _downloadUrl = await ResolveDownloadUrlAsync();
            _urlResolved = true;
            if (_downloadUrl == null)
            {
                ConsoleUI.ShowError(Loc.T("error.no_release_asset", _latestVersion));
                return false;
            }
        }

        // Step 1 — Download new exe to .new
        Console.Write(Loc.T("status.downloading") + " ");
        var success = await _github.DownloadAssetAsync(
            _downloadUrl!,
            newExe,
            (received, total) =>
            {
                Console.Write('\r');
                var line = Loc.T("status.downloading") + " ";
                if (total > 0)
                {
                    line += ProgressBar.Build(received, total);
                    line += $" {received / 1024}K / {total / 1024}K";
                }
                else
                {
                    line += ProgressBar.BuildIndeterminate((int)(received % 100));
                }
                Console.Write(line);
            },
            mirrors: _config.DownloadMirrors);

        if (!success)
        {
            Console.WriteLine();
            ConsoleUI.ShowError(Loc.T("selfupdate.download_failed"));
            TryDelete(newExe);
            return false;
        }

        Console.WriteLine();
        ConsoleUI.ShowSuccess(Loc.T("selfupdate.download_ok"));

        // Step 1.5 — Verify downloaded exe (PE header + optional SHA256)
        Console.Write("Verifying... ");
        var peVerifier = new Verification.PeHeaderVerifier();
        var peResult = await peVerifier.VerifyAsync(newExe);
        if (!peResult.IsSuccess)
        {
            Console.WriteLine();
            ConsoleUI.ShowError($"Download verification failed: {peResult.Error}");
            TryDelete(newExe);
            return false;
        }

        if (!string.IsNullOrEmpty(_assetDigest))
        {
            var shaVerifier = new Verification.Sha256Verifier();
            var shaResult = await shaVerifier.VerifyAsync(newExe, _assetDigest);
            if (!shaResult.IsSuccess)
            {
                Console.WriteLine();
                ConsoleUI.ShowError($"Hash verification failed: {shaResult.Error}");
                TryDelete(newExe);
                return false;
            }
        }
        ConsoleUI.ShowSuccess("Verified");

        // Step 1.6 — Config transaction (commit only after successful swap)
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlotWeave.Launcher", "launcher_config.json");
        var tx = new ConfigTransaction(configPath, _config);
        tx.Update(c => c.LauncherVersion = _latestVersion!);

        ConsoleUI.ShowInfo(Loc.T("selfupdate.restarting"));

        // Step 2 — Atomic replacement via rename trick.
        try
        {
            TryDelete(oldExe);
        }
        catch { /* best effort */ }

        try
        {
            File.Move(currentExe, oldExe);
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("selfupdate.rename_failed", ex.Message));
            TryDelete(newExe);
            tx.Abort();
            return false;
        }

        try
        {
            File.Move(newExe, currentExe);
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("selfupdate.replace_failed", ex.Message));
            try { File.Move(oldExe, currentExe); }
            catch { }
            TryDelete(newExe);
            tx.Abort();
            return false;
        }

        // Only persist config if the swap succeeded
        tx.Commit();

        // Step 3 — Start the new exe and exit
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                WorkingDirectory = exeDir,
                UseShellExecute = true
            });
        }
        catch
        {
            ConsoleUI.ShowInfo(Loc.T("selfupdate.manual_restart"));
        }

        Environment.Exit(0);
        return true;
    }

    /// <summary>
    /// Resolve the download URL for the launcher asset.
    /// Makes 1 API call — only invoked when the user actually clicks update.
    /// </summary>
    private async Task<string?> ResolveDownloadUrlAsync()
    {
        if (_config.LauncherRepo == null) return null;

        var release = await _github.GetLatestReleaseAsync(
            _config.LauncherRepo.Owner, _config.LauncherRepo.Repo);
        if (release == null) return null;

        var asset = _github.FindMatchingAsset(release, _config.LauncherRepo.AssetPattern);
        if (asset != null)
        {
            _assetDigest = asset.Digest;
            return asset.BrowserDownloadUrl;
        }

        return null;
    }

    /// <summary>
    /// Compare two semver-style versions. Returns true if remote > local.
    /// </summary>
    private static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(remote, out var rv) && Version.TryParse(local, out var lv))
            return rv > lv;

        // Fallback: string comparison stripped of 'v' prefix
        var r = remote.StartsWith('v') ? remote[1..] : remote;
        var l = local.StartsWith('v') ? local[1..] : local;
        return string.Compare(r, l, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
