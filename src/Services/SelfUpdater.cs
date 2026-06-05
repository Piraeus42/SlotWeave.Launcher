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
    /// Download the new launcher and launch the update stager.
    /// This method never returns — it exits the process after launching the batch file.
    /// </summary>
    public async Task<bool> UpdateAsync()
    {
        if (_downloadUrl == null)
            return false;

        ConsoleUI.ShowHeader(Loc.T("selfupdate.updating"));

        // Download new exe to .new file
        var currentExe = Environment.ProcessPath ?? Path.Combine(_launcherDir, "SlotWeave.Launcher.exe");
        var newExe = currentExe + ".new";
        var batPath = Path.Combine(_launcherDir, "update.bat");

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
        ConsoleUI.ShowInfo(Loc.T("selfupdate.restarting"));

        // Write the batch stager script
        WriteUpdateBatch(batPath, currentExe, newExe);

        // Launch the batch file and exit
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                WorkingDirectory = _launcherDir,
                UseShellExecute = true,
                CreateNoWindow = true
            });
        }
        catch
        {
            // Fallback: just tell user to manually replace
            ConsoleUI.ShowInfo(Loc.T("selfupdate.manual", newExe, currentExe));
        }

        Environment.Exit(0);
        return true; // unreachable
    }

    /// <summary>
    /// Write the batch stager that replaces the exe and restarts.
    /// </summary>
    private static void WriteUpdateBatch(string batPath, string currentExe, string newExe)
    {
        var script = $"""
@echo off
title SlotWeave Launcher Update
echo Updating SlotWeave Launcher...
timeout /t 2 /nobreak >nul
del /f /q "{currentExe}" 2>nul
move /y "{newExe}" "{currentExe}" 2>nul
if exist "{currentExe}" (
    echo Update complete. Restarting...
    start "" "{currentExe}"
) else (
    echo Update failed. Please manually rename:
    echo   {newExe}
    echo   to
    echo   {currentExe}
    pause
)
del /f /q "%~f0" 2>nul
""";
        File.WriteAllText(batPath, script, System.Text.Encoding.ASCII);
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
