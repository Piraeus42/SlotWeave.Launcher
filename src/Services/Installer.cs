using System.Diagnostics;
using System.IO.Compression;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Handles installation, update, and rollback of SlotWeave components.
/// </summary>
public class Installer
{
    private readonly GitHubService _github;
    private readonly ComponentScanner _scanner;
    private readonly CacheManager _cacheManager;
    private readonly LauncherConfig _config;
    private string? _gameDir;
    private int _lastProgressLineLength;

    public Installer(GitHubService github, ComponentScanner scanner,
        CacheManager cacheManager, LauncherConfig config)
    {
        _github = github;
        _scanner = scanner;
        _cacheManager = cacheManager;
        _config = config;
    }

    public void SetGameDirectory(string gameDir)
    {
        _gameDir = gameDir;
    }

    public async Task<bool> InstallOrUpdateAsync(
        InstalledComponent component,
        GitHubRelease release,
        CancellationToken ct = default)
    {
        if (_gameDir == null)
        {
            ConsoleUI.ShowError(Loc.T("error.game_dir_not_set"));
            return false;
        }

        var definition = component.Definition;
        var asset = _github.FindMatchingAsset(release, definition.AssetPattern);

        if (asset == null)
        {
            ConsoleUI.ShowError(Loc.T("error.no_release_asset", release.TagName));
            return false;
        }

        var version = release.Version;
        var isUpdate = component.IsInstalled;

        ConsoleUI.ShowHeader(isUpdate
            ? Loc.T("result.update_ok_title", definition.DisplayName, component.InstalledVersion ?? "?", version)
            : Loc.T("result.install_title", definition.DisplayName, version));

        Console.WriteLine();

        // Step 1: Check for running game
        if (!await CheckAndCloseGame())
            return false;

        // Step 2: Download
        var tempDir = Path.Combine(_gameDir, _config.TempDirectory);
        var zipPath = Path.Combine(tempDir, $"{definition.Id}_{version}.zip");

        Console.Write(Loc.T("status.downloading") + " ");
        var downloadSuccess = await DownloadWithProgress(zipPath, asset.BrowserDownloadUrl, ct);
        if (!downloadSuccess)
        {
            ConsoleUI.ShowError(Loc.T("error.download_failed"));
            CleanupTemp(tempDir);
            return false;
        }

        Console.WriteLine();
        ConsoleUI.ShowSuccess(Loc.T("status.download_complete", FormatSize(asset.Size)));

        // Step 3: Verify zip
        Console.Write(Loc.T("status.verifying_zip") + " ");
        if (!VerifyZip(zipPath))
        {
            ConsoleUI.ShowError(Loc.T("error.zip_corrupt"));
            CleanupTemp(tempDir);
            return false;
        }
        ConsoleUI.ShowSuccess(Loc.T("status.zip_ok"));

        // Step 4: Backup current version (if updating)
        if (isUpdate)
        {
            Console.Write(Loc.T("status.backing_up") + " ");
            var backupSuccess = CreateBackup(definition);
            if (backupSuccess)
                ConsoleUI.ShowSuccess(Loc.T("status.backup_done"));
            else
                ConsoleUI.ShowWarning(Loc.T("warn.backup_continue"));
        }

        // Step 5: Clear caches
        Console.Write(Loc.T("status.clearing_cache") + " ");
        _cacheManager.ClearAll();
        ConsoleUI.ShowSuccess(Loc.T("status.cache_cleared"));

        // Step 6: Extract and copy files
        Console.Write(Loc.T("status.extracting") + " ");
        var extractDir = Path.Combine(tempDir, "extract");
        try
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            var sourceDir = GetContentRoot(extractDir, definition);

            // Determine target: core files go to game root, mods go to their install path
            var targetDir = definition.IsCore
                ? _gameDir
                : Path.Combine(_gameDir, definition.InstallPath);

            CopyDirectory(sourceDir, targetDir, overwrite: true);

            ConsoleUI.ShowSuccess(Loc.T("status.files_installed"));
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("error.extract_failed", ex.Message));

            if (isUpdate)
            {
                Console.Write(Loc.T("status.rolling_back") + " ");
                if (RestoreBackup(definition))
                    ConsoleUI.ShowSuccess(Loc.T("status.rollback_ok"));
                else
                    ConsoleUI.ShowError(Loc.T("error.rollback_failed"));
            }

            return false;
        }

        // Step 7: Write version marker
        _scanner.WriteVersionMarker(definition, version);

        // Step 8: Cleanup temp
        CleanupTemp(tempDir);

        Console.WriteLine();
        ConsoleUI.ShowSuccess(isUpdate
            ? Loc.T("result.update_ok", definition.DisplayName, version)
            : Loc.T("result.install_ok", definition.DisplayName, version));

        return true;
    }

    private async Task<bool> DownloadWithProgress(
        string destinationPath,
        string downloadUrl,
        CancellationToken ct)
    {
        _lastProgressLineLength = 0;
        var lastReportTime = DateTime.MinValue;
        var label = Loc.T("status.downloading") + " ";

        return await _github.DownloadAssetAsync(downloadUrl, destinationPath,
            (received, total) =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalMilliseconds < 100)
                    return;
                lastReportTime = now;

                Console.Write('\r');
                var line = label;
                if (total > 0)
                {
                    line += ProgressBar.Build(received, total);
                    line += $" {FormatSize(received)} / {FormatSize(total)}";
                }
                else
                {
                    line += ProgressBar.BuildIndeterminate((int)(received % 100));
                    line += $" {FormatSize(received)}";
                }

                var pad = _lastProgressLineLength - GetVisualLength(line);
                if (pad > 0) line += new string(' ', pad);
                _lastProgressLineLength = GetVisualLength(line);

                Console.Write(line);
            },
            ct,
            mirrors: _config.DownloadMirrors);
    }

    private static int GetVisualLength(string s)
    {
        var len = 0;
        foreach (var c in s)
        {
            if (c == '\r' || c == '\n') continue;
            len += 1;
        }
        return len;
    }

    private async Task<bool> CheckAndCloseGame()
    {
        while (true)
        {
            var gameProcess = Process.GetProcessesByName("Luck be a Landlord").FirstOrDefault();
            if (gameProcess == null)
                return true;

            ConsoleUI.ShowWarning(Loc.T("warn.game_running"));
            Console.Write(Loc.T("warn.close_game"));

            var key = ConsoleUI.TryReadKey(true);
            if (key == null) { await Task.Delay(500); continue; }
            Console.WriteLine();

            if (key.Value.Key == ConsoleKey.S)
                return false;

            await Task.Delay(500);
        }
    }

    private bool CreateBackup(ComponentDefinition definition)
    {
        try
        {
            var backupDir = Path.Combine(_gameDir!, _config.BackupDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var componentBackupDir = Path.Combine(backupDir, $"{definition.Id}_{timestamp}");

            if (!Directory.Exists(componentBackupDir))
                Directory.CreateDirectory(componentBackupDir);

            var installPath = Path.Combine(_gameDir!, definition.InstallPath);
            if (Directory.Exists(installPath))
            {
                CopyDirectory(installPath,
                    Path.Combine(componentBackupDir, definition.InstallPath), overwrite: true);
            }

            foreach (var file in definition.GameFiles)
            {
                var src = Path.Combine(_gameDir!, file);
                var dst = Path.Combine(componentBackupDir, file);

                if (File.Exists(src))
                {
                    var dstDir = Path.GetDirectoryName(dst);
                    if (dstDir != null && !Directory.Exists(dstDir))
                        Directory.CreateDirectory(dstDir);
                    File.Copy(src, dst, overwrite: true);
                }
                else if (Directory.Exists(src))
                {
                    CopyDirectory(src, dst, overwrite: true);
                }
            }

            PruneBackups(backupDir, definition.Id);
            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("error.backup_failed", ex.Message));
            return false;
        }
    }

    private bool RestoreBackup(ComponentDefinition definition)
    {
        try
        {
            var backupDir = Path.Combine(_gameDir!, _config.BackupDirectory);
            if (!Directory.Exists(backupDir))
                return false;

            var backups = Directory.GetDirectories(backupDir, $"{definition.Id}_*")
                .OrderByDescending(d => d)
                .ToList();

            if (backups.Count == 0)
                return false;

            CopyDirectory(backups.First(), _gameDir!, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PruneBackups(string backupDir, string componentId)
    {
        try
        {
            var backups = Directory.GetDirectories(backupDir, $"{componentId}_*")
                .OrderByDescending(d => d)
                .ToList();

            for (int i = _config.MaxBackups; i < backups.Count; i++)
            {
                Directory.Delete(backups[i], true);
            }
        }
        catch { /* best effort */ }
    }

    private static bool VerifyZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Length > 0)
                    return true;
            }
            return zip.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find the actual content root within an extracted archive.
    /// GitHub wraps archives in a directory like "repo-hash", which we strip.
    /// We only strip when there are no content files (.dll/.json/etc) at root,
    /// to avoid misidentifying a real content directory as a wrapper.
    /// </summary>
    private static string GetContentRoot(string extractDir, ComponentDefinition definition)
    {
        var dirs = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);

        // Content file extensions — if these exist at root, it's NOT a wrapper
        var hasContentFiles = files.Any(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return ext is ".dll" or ".json" or ".exe" or ".txt"
                or ".deps" or ".pdb" or ".xml" or ".cfg" or ".ini";
        });

        // Only strip when: one directory + no content files (pure wrapper scenario)
        if (dirs.Length == 1 && !hasContentFiles)
        {
            var singleDir = dirs[0];
            var dirName = Path.GetFileName(singleDir);

            // Don't strip if dir name matches the expected install leaf
            var installLeaf = Path.GetFileName(definition.InstallPath.TrimEnd('/', '\\'));
            if (dirName.Equals(installLeaf, StringComparison.OrdinalIgnoreCase))
                return extractDir;

            return singleDir;
        }

        return extractDir;
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool overwrite)
    {
        if (!Directory.Exists(sourceDir))
            return;

        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        var dir = new DirectoryInfo(sourceDir);

        foreach (var file in dir.GetFiles())
        {
            var targetPath = Path.Combine(destDir, file.Name);
            file.CopyTo(targetPath, overwrite);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            CopyDirectory(subDir.FullName, Path.Combine(destDir, subDir.Name), overwrite);
        }
    }

    private static void CleanupTemp(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
        catch { /* best effort */ }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }
}
