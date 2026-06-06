using System.Diagnostics;
using System.Text.Json;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Checks for launcher updates from GitHub and performs self-update via batch file stager.
/// </summary>
public class SelfUpdater
{
    private readonly GitHubService _github;
    private readonly LauncherConfig _config;
    private readonly string _launcherDir;
    private string? _latestVersion;
    private string? _downloadUrl;
    private bool _checked;

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
    /// Returns true if an update is available.
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        _checked = true;

        if (_config.LauncherRepo == null)
            return false;

        var release = await _github.GetLatestReleaseAsync(
            _config.LauncherRepo.Owner, _config.LauncherRepo.Repo);

        if (release == null)
            return false;

        var remoteVersion = release.Version;
        if (!IsNewer(remoteVersion, _config.LauncherVersion))
            return false;

        var asset = _github.FindMatchingAsset(release, _config.LauncherRepo.AssetPattern);
        if (asset == null)
            return false;

        _latestVersion = remoteVersion;
        _downloadUrl = asset.BrowserDownloadUrl;
        return true;
    }

    /// <summary>
    /// Download the new launcher and replace the current exe via rename-based atomic swap.
    /// Windows allows renaming a running .exe (but not deleting/overwriting it),
    /// so we rename current→.old, then rename .new→current, with rollback on failure.
    /// This method never returns — it exits the process after starting the new exe.
    /// </summary>
    public async Task<bool> UpdateAsync()
    {
        if (_downloadUrl == null)
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

        // Step 1 — Download new exe to .new
        Console.Write(Loc.T("status.downloading") + " ");
        var success = await _github.DownloadAssetAsync(
            _downloadUrl,
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

        // Step 1.5 — Persist the new version to config NOW, before swapping.
        // If we don't, the new exe reads the stale config version and
        // immediately offers another "update" for the same release.
        _config.LauncherVersion = _latestVersion!;
        TrySaveConfigVersion();

        ConsoleUI.ShowInfo(Loc.T("selfupdate.restarting"));

        // Step 2 — Atomic replacement via rename trick.
        // On Windows a running .exe CAN be renamed (not deleted/overwritten).
        // Sequence: delete stale .old → rename current→.old → rename .new→current.
        // If the final rename fails, roll back .old→current.
        try
        {
            // Clean up leftover .old from a previous successful update
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
            return false;
        }

        try
        {
            File.Move(newExe, currentExe);
        }
        catch (Exception ex)
        {
            // Rollback: put the old exe back in place
            ConsoleUI.ShowError(Loc.T("selfupdate.replace_failed", ex.Message));
            try { File.Move(oldExe, currentExe); }
            catch { /* if rollback also fails, user needs to restore manually */ }
            TryDelete(newExe);
            return false;
        }

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
        return true; // unreachable
    }

    /// <summary>
    /// Save the updated launcher version to the APPDATA config file
    /// so the incoming exe doesn't see itself as outdated.
    /// </summary>
    private void TrySaveConfigVersion()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlotWeave.Launcher");
            var configPath = Path.Combine(dataDir, "launcher_config.json");
            if (!File.Exists(configPath)) return;

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config == null) return;

            config.LauncherVersion = _latestVersion!;
            File.WriteAllText(configPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort — new exe will handle version sync on its first config save */ }
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
