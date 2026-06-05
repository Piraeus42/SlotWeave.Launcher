using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Handles safe uninstallation of SlotWeave components.
/// </summary>
public class Uninstaller
{
    private readonly CacheManager _cacheManager;
    private readonly LauncherConfig _config;
    private string? _gameDir;

    public Uninstaller(CacheManager cacheManager, LauncherConfig config)
    {
        _cacheManager = cacheManager;
        _config = config;
    }

    public void SetGameDirectory(string gameDir)
    {
        _gameDir = gameDir;
    }

    public bool Uninstall(InstalledComponent component, bool createBackup = true)
    {
        if (_gameDir == null)
        {
            ConsoleUI.ShowError(Loc.T("error.game_dir_not_set"));
            return false;
        }

        var definition = component.Definition;
        var modName = definition.DisplayName;

        if (!component.IsInstalled)
        {
            ConsoleUI.ShowWarning(Loc.T("warn.not_installed", modName));
            return true;
        }

        ConsoleUI.ShowHeader($"卸载 {modName} v{component.InstalledVersion}");
        Console.WriteLine();

        // Confirm
        Console.Write(Loc.T("warn.confirm_uninstall", modName));
        if (!ConfirmYesNo())
        {
            ConsoleUI.ShowInfo(Loc.T("info.cancelled"));
            return false;
        }

        // Extra warning for core
        if (definition.IsCore)
        {
            ConsoleUI.ShowWarning(Loc.T("warn.core_uninstall"));
            Console.Write(Loc.T("warn.continue_confirm"));
            if (!ConfirmYesNo())
            {
                ConsoleUI.ShowInfo(Loc.T("info.cancelled"));
                return false;
            }
        }

        // Create backup
        if (createBackup)
        {
            Console.Write(Loc.T("status.creating_backup") + " ");
            try
            {
                var backupDir = Path.Combine(_gameDir, _config.BackupDirectory);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var componentBackupDir = Path.Combine(backupDir, $"{definition.Id}_uninstall_{timestamp}");

                if (!Directory.Exists(componentBackupDir))
                    Directory.CreateDirectory(componentBackupDir);

                var installPath = Path.Combine(_gameDir, definition.InstallPath);
                if (Directory.Exists(installPath))
                {
                    CopyDirectory(installPath,
                        Path.Combine(componentBackupDir, definition.InstallPath));
                }

                foreach (var file in definition.GameFiles)
                {
                    var src = Path.Combine(_gameDir, file);
                    var dst = Path.Combine(componentBackupDir, file);

                    if (File.Exists(src))
                    {
                        var dstDir = Path.GetDirectoryName(dst);
                        if (dstDir != null && !Directory.Exists(dstDir))
                            Directory.CreateDirectory(dstDir);
                        File.Copy(src, dst, overwrite: false);
                    }
                    else if (Directory.Exists(src))
                    {
                        CopyDirectory(src, dst);
                    }
                }

                ConsoleUI.ShowSuccess(Loc.T("status.backup_done"));
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowWarning(Loc.T("error.backup_failed", ex.Message));
                Console.Write(Loc.T("warn.continue_without_backup"));
                if (!ConfirmYesNo())
                {
                    ConsoleUI.ShowInfo(Loc.T("info.cancelled"));
                    return false;
                }
            }
        }

        // Remove files
        var errors = new List<string>();

        var removePath = Path.Combine(_gameDir, definition.InstallPath);
        if (Directory.Exists(removePath))
        {
            Console.Write(Loc.T("status.deleting", definition.InstallPath) + " ");
            try
            {
                Directory.Delete(removePath, true);
                ConsoleUI.ShowSuccess(Loc.T("status.done"));
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowError(Loc.T("error.delete_failed", ex.Message));
                errors.Add(Loc.T("error.cannot_delete", definition.InstallPath, ex.Message));
            }
        }

        foreach (var file in definition.GameFiles)
        {
            var filePath = Path.Combine(_gameDir, file);
            if (File.Exists(filePath))
            {
                Console.Write(Loc.T("status.deleting", file) + " ");
                try
                {
                    File.Delete(filePath);
                    ConsoleUI.ShowSuccess(Loc.T("status.done"));
                }
                catch (Exception ex)
                {
                    ConsoleUI.ShowError(Loc.T("error.delete_failed", ex.Message));
                    errors.Add(Loc.T("error.cannot_delete", file, ex.Message));
                }
            }
        }

        // Clear caches
        Console.Write(Loc.T("status.clearing_cache") + " ");
        _cacheManager.ClearAll();
        ConsoleUI.ShowSuccess(Loc.T("status.cache_cleared"));

        Console.WriteLine();
        if (errors.Count == 0)
        {
            ConsoleUI.ShowSuccess(Loc.T("result.uninstall_ok", modName));
            return true;
        }
        else
        {
            ConsoleUI.ShowWarning(Loc.T("result.uninstall_partial", modName));
            foreach (var err in errors)
                ConsoleUI.ShowError($"  • {err}");
            return true;
        }
    }

    private static bool ConfirmYesNo()
    {
        var key = ConsoleUI.TryReadKey(true);
        if (key == null) return false;
        Console.WriteLine();
        return key.Value.Key == ConsoleKey.Y;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir))
            return;

        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        var dir = new DirectoryInfo(sourceDir);

        foreach (var file in dir.GetFiles())
        {
            var targetPath = Path.Combine(destDir, file.Name);
            file.CopyTo(targetPath, overwrite: false);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            CopyDirectory(subDir.FullName, Path.Combine(destDir, subDir.Name));
        }
    }
}
