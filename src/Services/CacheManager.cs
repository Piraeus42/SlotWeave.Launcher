using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Manages cache directory cleanup for SlotWeave.
/// </summary>
public class CacheManager
{
    private readonly LauncherConfig _config;
    private string? _gameDir;

    public CacheManager(LauncherConfig config)
    {
        _config = config;
    }

    public void SetGameDirectory(string gameDir)
    {
        _gameDir = gameDir;
    }

    /// <summary>
    /// Clear all configured cache directories.
    /// Returns the number of successfully cleared directories.
    /// </summary>
    public int ClearAll()
    {
        if (_gameDir == null)
            return 0;

        int cleared = 0;

        foreach (var cacheDir in _config.CacheDirectories)
        {
            // Resolve relative to game directory
            var fullPath = Path.IsPathRooted(cacheDir)
                ? cacheDir
                : Path.Combine(_gameDir, cacheDir);

            if (ClearDirectory(fullPath))
                cleared++;
        }

        // Also try common AppData cache locations
        TryClearAppDataCache();

        return cleared;
    }

    /// <summary>
    /// Clear a single directory (remove all contents, keep the directory itself).
    /// </summary>
    private static bool ClearDirectory(string path)
    {
        if (!Directory.Exists(path))
            return false;

        try
        {
            var dir = new DirectoryInfo(path);

            foreach (var file in dir.GetFiles())
            {
                try { file.Delete(); } catch { /* best effort */ }
            }

            foreach (var subDir in dir.GetDirectories())
            {
                try { subDir.Delete(true); } catch { /* best effort */ }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to clear SlotWeave cache from common AppData locations.
    /// </summary>
    private static void TryClearAppDataCache()
    {
        try
        {
            var localLow = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..", "LocalLow");
            localLow = Path.GetFullPath(localLow);

            var slotWeaveCache = Path.Combine(localLow,
                "TrampolineTales", "LuckBeALandlord", "SlotWeave");

            if (Directory.Exists(slotWeaveCache))
            {
                ClearDirectory(slotWeaveCache);
            }
        }
        catch
        {
            // Best effort
        }
    }
}
