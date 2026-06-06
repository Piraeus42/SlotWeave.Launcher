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

    // ── Public scan API ────────────────────────────────────────

    /// <summary>
    /// Scan a single component (optimised: only stats the target component's files).
    /// </summary>
    public InstalledComponent ScanOne(ComponentDefinition definition, string gameDir)
    {
        var component = new InstalledComponent(definition);

        var installDir = Path.Combine(gameDir, definition.InstallPath);
        var installDirExists = Directory.Exists(installDir);

        var gameFiles = definition.GameFiles
            .Select(f => Path.Combine(gameDir, f))
            .ToList();

        var existingFiles = gameFiles.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();

        // State determination
        ComponentState scannedState;

        if (!installDirExists && existingFiles.Count == 0)
        {
            // Nothing installed
            scannedState = ComponentState.NotInstalled;
        }
        else if (installDirExists && existingFiles.Count == gameFiles.Count)
        {
            // Directory exists and all files present → installed
            scannedState = ComponentState.Installed;
        }
        else
        {
            // Some files exist → partial / incomplete
            // Corrupted detection (hash mismatch) is left to the post-extraction
            // verification step, not the scanner.
            scannedState = ComponentState.PartialInstall;
        }

        component.SetStateFromScan(scannedState);

        // Read installed version if anything is present
        if (scannedState != ComponentState.NotInstalled)
        {
            var version = ReadInstalledVersion(definition, gameDir);
            component.UpdateInstalledVersion(version);
        }

        return component;
    }

    /// <summary>
    /// Scan all configured components.
    /// </summary>
    public List<InstalledComponent> ScanAll(IEnumerable<ComponentDefinition> definitions, string gameDir)
    {
        return definitions
            .Select(def => ScanOne(def, gameDir))
            .ToList();
    }

    /// <summary>
    /// Scan all configured repositories (backward-compat overload).
    /// </summary>
    public List<InstalledComponent> ScanAll()
    {
        if (_gameDir == null)
            throw new InvalidOperationException("Game directory not set. Call SetGameDirectory first.");

        return ScanAll(_config.Repositories, _gameDir);
    }

    // ── Legacy GDWeave detection ────────────────────────────────

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

    // ── Version I/O ─────────────────────────────────────────────

    /// <summary>
    /// Write a version marker file after successful install/update.
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

            var json = $"{{\"version\": \"{version}\"}}";
            File.WriteAllText(versionPath, json);
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("error.version_write_failed", ex.Message));
        }
    }

    /// <summary>
    /// Read the installed version of a component using the configured manifest file.
    /// Overload that accepts explicit game directory (for ScanOne).
    /// </summary>
    private string? ReadInstalledVersion(ComponentDefinition definition, string gameDir)
    {
        // Strategy 1: version.json or manifest.json
        if (definition.ManifestFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(gameDir, definition.ManifestFile);
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    using var doc = JsonDocument.Parse(json);

                    if (TryGetJsonProperty(doc.RootElement, "version", out var v))
                        return v;

                    if (TryGetJsonProperty(doc.RootElement, "Version", out var v2))
                        return v2;

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

        // Strategy 2: version.txt
        if (definition.ManifestFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var versionPath = Path.Combine(gameDir, definition.ManifestFile);
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
            var candidateDllPaths = new[]
            {
                Path.Combine(gameDir, definition.InstallPath, "core", "SlotWeave.dll"),
                Path.Combine(gameDir, definition.InstallPath, "SlotWeave.dll"),
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
            var modDir = Path.Combine(gameDir, definition.InstallPath);
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

    // ── Helpers ─────────────────────────────────────────────────

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
