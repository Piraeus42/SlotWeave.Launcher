using System.Reflection;
using System.Text.Json;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Scans the game directory for installed SlotWeave components and reads their versions.
/// </summary>
public class ComponentScanner
{
    private readonly LauncherConfig _config;
    private string? _gameDir;

    public ComponentScanner(LauncherConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Set the detected game directory before scanning.
    /// </summary>
    public void SetGameDirectory(string gameDir)
    {
        _gameDir = gameDir;
    }

    /// <summary>
    /// Scan all configured repositories and return their installation state.
    /// </summary>
    public List<InstalledComponent> ScanAll()
    {
        if (_gameDir == null)
            throw new InvalidOperationException("Game directory not set. Call SetGameDirectory first.");

        var results = new List<InstalledComponent>();

        foreach (var repo in _config.Repositories)
        {
            var installed = ScanComponent(repo);
            results.Add(installed);
        }

        return results;
    }

    /// <summary>
    /// Check for legacy GDWeave installation (pre-SlotWeave fork).
    /// Returns true if GDWeave directory exists in the game folder.
    /// </summary>
    public bool DetectLegacyGdWeave()
    {
        if (_gameDir == null) return false;
        return Directory.Exists(Path.Combine(_gameDir, "GDWeave"));
    }

    /// <summary>
    /// Remove legacy GDWeave: delete the GDWeave/ directory and winmm.dll proxy.
    /// Returns true if cleanup succeeded.
    /// </summary>
    public bool RemoveLegacyGdWeave()
    {
        if (_gameDir == null) return false;

        var gdDir = Path.Combine(_gameDir, "GDWeave");
        var wmPath = Path.Combine(_gameDir, "winmm.dll");
        var ok = true;

        if (Directory.Exists(gdDir))
        {
            try
            {
                Directory.Delete(gdDir, true);
                ConsoleUI.ShowSuccess(Loc.T("gdweave.removed_dir", "GDWeave/"));
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowError(Loc.T("error.cannot_delete", "GDWeave/", ex.Message));
                ok = false;
            }
        }

        if (File.Exists(wmPath))
        {
            try
            {
                File.Delete(wmPath);
                ConsoleUI.ShowSuccess(Loc.T("gdweave.removed_file", "winmm.dll"));
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowError(Loc.T("error.cannot_delete", "winmm.dll", ex.Message));
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// Scan a single component for its installation state and version.
    /// </summary>
    private InstalledComponent ScanComponent(ComponentDefinition definition)
    {
        var result = new InstalledComponent
        {
            Definition = definition,
            IsInstalled = false,
            InstalledVersion = null
        };

        // Check if the installation directory exists
        var installDir = Path.Combine(_gameDir!, definition.InstallPath);
        if (!Directory.Exists(installDir))
        {
            if (definition.GameFiles.Count > 0)
            {
                var fileChecks = definition.GameFiles.Select(f =>
                    File.Exists(Path.Combine(_gameDir!, f)) ||
                    Directory.Exists(Path.Combine(_gameDir!, f))).ToList();

                if (fileChecks.All(x => x))
                {
                    // All game files exist — fully installed
                }
                else if (fileChecks.Any(x => x))
                {
                    // Some files exist but not all — incomplete install
                    result.IsPartial = true;
                    return result;
                }
                else
                {
                    return result;
                }
            }
            else
            {
                return result;
            }
        }

        result.IsInstalled = true;

        // Try to read installed version
        result.InstalledVersion = ReadInstalledVersion(definition);

        return result;
    }

    /// <summary>
    /// Read the installed version of a component using the configured manifest file.
    /// Priority:
    /// 1. manifest.json (for mods) → Metadata.Version
    /// 2. version.txt (for core) → first line
    /// 3. DLL AssemblyVersion (fallback)
    /// </summary>
    private string? ReadInstalledVersion(ComponentDefinition definition)
    {
        // Strategy 1: version.json or manifest.json
        if (definition.ManifestFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(_gameDir!, definition.ManifestFile);
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    using var doc = JsonDocument.Parse(json);

                    // Try simple "version" field first (version.json format, lowercase)
                    if (TryGetJsonProperty(doc.RootElement, "version", out var v))
                        return v;

                    // Try "Version" (PascalCase)
                    if (TryGetJsonProperty(doc.RootElement, "Version", out var v2))
                        return v2;

                    // Try Metadata.Version (mod manifest.json format)
                    if (doc.RootElement.TryGetProperty("Metadata", out var metadata) &&
                        metadata.TryGetProperty("Version", out var version))
                    {
                        return version.GetString();
                    }
                }
                catch
                {
                    // Fall through to next strategy
                }
            }
        }

        // Strategy 2: version.txt (plain text, first line is version)
        if (definition.ManifestFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var versionPath = Path.Combine(_gameDir!, definition.ManifestFile);
            if (File.Exists(versionPath))
            {
                try
                {
                    var lines = File.ReadAllLines(versionPath);
                    if (lines.Length > 0)
                    {
                        var version = lines[0].Trim();
                        if (!string.IsNullOrEmpty(version))
                            return version;
                    }
                }
                catch
                {
                    // Fall through
                }
            }
        }

        // Strategy 3: DLL AssemblyVersion
        if (definition.IsCore)
        {
            // Try multiple candidate DLL paths for core assembly
            var candidateDllPaths = new[]
            {
                Path.Combine(_gameDir!, definition.InstallPath, "core", "SlotWeave.dll"),
                Path.Combine(_gameDir!, definition.InstallPath, "SlotWeave.dll"),
            };

            foreach (var dllPath in candidateDllPaths)
            {
                if (!File.Exists(dllPath)) continue;

                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(dllPath);
                    var version = assemblyName.Version;
                    if (version != null && version.Major > 0)
                        return $"{version.Major}.{version.Minor}.{version.Build}";
                }
                catch
                {
                    // Try next candidate
                }
            }
        }
        else
        {
            // Try to find DLL in mod directory
            var modDir = Path.Combine(_gameDir!, definition.InstallPath);
            if (Directory.Exists(modDir))
            {
                try
                {
                    var dllFiles = Directory.GetFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly);
                    foreach (var dll in dllFiles)
                    {
                        try
                        {
                            var assemblyName = AssemblyName.GetAssemblyName(dll);
                            var version = assemblyName.Version;
                            if (version != null && version.Major > 0)
                                return $"{version.Major}.{version.Minor}.{version.Build}";
                        }
                        catch
                        {
                            // Try next DLL
                        }
                    }
                }
                catch
                {
                    // Best effort
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Write a version.txt file after successful install/update.
    /// </summary>
    public void WriteVersionMarker(ComponentDefinition definition, string version)
    {
        if (_gameDir == null) return;

        var versionPath = Path.Combine(_gameDir, definition.ManifestFile);
        try
        {
            var dir = Path.GetDirectoryName(versionPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Write a proper version.json (zip already includes this,
            // but write it as a fallback in case extraction didn't include it)
            var json = $"{{\"version\": \"{version}\"}}";
            File.WriteAllText(versionPath, json);
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("error.version_write_failed", ex.Message));
        }
    }

    /// <summary>
    /// Try to read a string property from a JSON element, case-insensitive.
    /// </summary>
    private static bool TryGetJsonProperty(
        System.Text.Json.JsonElement element, string propertyName, out string? value)
    {
        value = null;
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value.GetString();
                return value != null;
            }
        }
        return false;
    }
}
