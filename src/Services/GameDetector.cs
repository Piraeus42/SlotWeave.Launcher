using Microsoft.Win32;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Auto-detects the Luck be a Landlord game installation directory.
/// Tries multiple strategies: config paths, Steam library, registry.
/// </summary>
public class GameDetector
{
    private readonly LauncherConfig _config;
    private readonly string _launcherDir;

    public GameDetector(LauncherConfig config, string launcherDir)
    {
        _config = config;
        _launcherDir = launcherDir;
    }

    /// <summary>
    /// Try to locate the game directory.
    /// Returns the path if found, null otherwise.
    /// </summary>
    public string? DetectGameDirectory()
    {
        var exeName = _config.GameExecutable;

        // 1. Check configured paths first
        foreach (var path in _config.GameDirectoryPaths)
        {
            if (TryPath(path, exeName, out var found))
                return found;
        }

        // 2. Check common Steam library locations
        var steamPaths = GetSteamLibraryPaths();
        foreach (var steamPath in steamPaths)
        {
            var candidate = Path.Combine(steamPath, "common", "Luck be a Landlord");
            if (TryPath(candidate, exeName, out var found))
                return found;
        }

        // 3. Check parent directory of the launcher (portable install)
        var parentDir = Directory.GetParent(_launcherDir)?.FullName;
        if (parentDir != null && TryPath(parentDir, exeName, out var parentFound))
            return parentFound;

        // 4. Check current directory
        if (TryPath(Environment.CurrentDirectory, exeName, out var currentFound))
            return currentFound;

        // 5. Check common install locations
        var commonPaths = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Luck be a Landlord",
            @"D:\Steam\steamapps\common\Luck be a Landlord",
            @"D:\steam\steamapps\common\Luck be a Landlord",
            @"E:\Steam\steamapps\common\Luck be a Landlord",
            @"E:\steam\steamapps\common\Luck be a Landlord",
            @"F:\Steam\steamapps\common\Luck be a Landlord",
            @"F:\steam\steamapps\common\Luck be a Landlord",
        };

        foreach (var path in commonPaths)
        {
            if (TryPath(path, exeName, out var found))
                return found;
        }

        return null;
    }

    /// <summary>
    /// Check if a path contains the game executable.
    /// </summary>
    private static bool TryPath(string directoryPath, string exeName, out string foundPath)
    {
        foundPath = string.Empty;

        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
            return false;

        var exePath = Path.Combine(directoryPath, exeName);
        if (File.Exists(exePath))
        {
            foundPath = directoryPath;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Get all Steam library paths by parsing libraryfolders.vdf.
    /// </summary>
    private List<string> GetSteamLibraryPaths()
    {
        var paths = new List<string>();

        // Try to find the Steam installation directory
        string? steamDir = null;

        // Check registry (Windows)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Valve\Steam");
            steamDir = key?.GetValue("InstallPath") as string;

            if (steamDir == null)
            {
                using var key32 = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Valve\Steam");
                steamDir = key32?.GetValue("InstallPath") as string;
            }
        }
        catch
        {
            // Registry access may fail, try common paths
        }

        // Fallback common Steam paths
        if (steamDir == null)
        {
            var commonDirs = new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"D:\Steam",
                @"D:\steam",
                @"E:\Steam",
                @"E:\steam",
            };

            foreach (var d in commonDirs)
            {
                if (Directory.Exists(d) && File.Exists(Path.Combine(d, "steamapps", "libraryfolders.vdf")))
                {
                    steamDir = d;
                    break;
                }
            }
        }

        if (steamDir == null)
            return paths;

        // Add the default Steam library
        var defaultLibrary = Path.Combine(steamDir, "steamapps");
        if (Directory.Exists(defaultLibrary))
            paths.Add(defaultLibrary);

        // Parse libraryfolders.vdf for additional libraries
        var vdfPath = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var lines = File.ReadAllLines(vdfPath);
                foreach (var line in lines)
                {
                    // Format: "path"  "D:\\SteamLibrary"
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("\"path\""))
                    {
                        var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var libPath = parts[1].Replace("\\\\", "\\");
                            var fullPath = Path.Combine(libPath, "steamapps");
                            if (Directory.Exists(fullPath) && !paths.Contains(fullPath))
                                paths.Add(fullPath);
                        }
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }

        return paths;
    }
}
