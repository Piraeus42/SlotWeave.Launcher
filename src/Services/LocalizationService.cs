using System.Text.Json;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Singleton localization service. Loads locale JSON files and provides T(key) lookups.
/// Language preference is cached in .slotweave_launcher/settings.json.
/// </summary>
public class LocalizationService
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ?? throw new InvalidOperationException(
        "LocalizationService not initialized. Call InitializeAsync first.");

    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private string _dataDir = string.Empty;

    public string CurrentLanguage { get; private set; } = "en";

    internal LocalizationService() { }

    /// <summary>
    /// Initialize the localization service. If a cached language exists, load it.
    /// Otherwise returns null to signal that language selection is needed.
    /// </summary>
    public static async Task<LocalizationService?> InitializeAsync(string dataDir)
    {
        var service = new LocalizationService { _dataDir = dataDir };

        var cached = LoadCachedLanguage(dataDir);
        if (cached != null)
        {
            service.CurrentLanguage = cached;
            service.LoadLocale(cached);
            _instance = service;
            return service;
        }

        return null; // Signal: first launch, needs language selection
    }

    /// <summary>
    /// Set the language (called after user selection) and persist it.
    /// </summary>
    public void SetLanguage(string lang)
    {
        CurrentLanguage = lang;
        LoadLocale(lang);
        SaveLanguage();
        _instance = this;
    }

    /// <summary>
    /// First-launch initialization: set language, save, and register as singleton.
    /// </summary>
    public void SetLanguageForFirstLaunch(string dataDir, string lang)
    {
        _dataDir = dataDir;
        CurrentLanguage = lang;
        LoadLocale(lang);
        SaveLanguage();
        _instance = this;
    }

    /// <summary>
    /// Get a localized string by key. Returns the key itself if not found.
    /// </summary>
    public string T(string key)
    {
        if (_strings.TryGetValue(key, out var value))
            return value;
        return $"[[{key}]]";
    }

    /// <summary>
    /// Get a localized string with format arguments.
    /// </summary>
    public string T(string key, params object[] args)
    {
        try
        {
            return string.Format(T(key), args);
        }
        catch
        {
            return T(key);
        }
    }

    /// <summary>
    /// Check if a cached language preference exists.
    /// </summary>
    public static bool HasCachedLanguage(string launcherDir)
    {
        return LoadCachedLanguage(launcherDir) != null;
    }

    /// <summary>
    /// Show the language selection picker. Returns after user selects.
    /// </summary>
    public static string ShowLanguagePicker()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════╗");
        Console.WriteLine("  ║  请选择语言 / Please select language ║");
        Console.WriteLine("  ╚══════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("  [1] English");
        Console.WriteLine("  [2] 中文");
        Console.WriteLine();
        Console.WriteLine("  Choice will be saved for next launch.");
        Console.WriteLine("  选择将被保存，下次启动时自动应用。");
        Console.WriteLine();
        Console.Write("  > ");

        while (true)
        {
            var key = ConsoleUI.TryReadKey(true);
            if (key == null) continue;

            if (key.Value.KeyChar == '1')
            {
                Console.WriteLine("English");
                return "en";
            }
            if (key.Value.KeyChar == '2')
            {
                Console.WriteLine("中文");
                return "zh";
            }
        }
    }

    private void LoadLocale(string lang)
    {
        _strings.Clear();

        // Strategy 1: Embedded resource (single-file publish)
        var resourceName = $"SlotWeave.Launcher.Locales.{lang}.json";
        var json = LoadFromEmbeddedResource(resourceName);

        // Strategy 2: Loose files (development / side-by-side)
        if (json == null)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Locales", $"{lang}.json"),
                Path.Combine(Environment.CurrentDirectory, "Locales", $"{lang}.json"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    json = File.ReadAllText(path);
                    break;
                }
            }
        }

        if (json == null)
        {
            LoadMinimalFallback();
            return;
        }

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict != null)
            {
                _strings = new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            LoadMinimalFallback();
        }
    }

    /// <summary>
    /// Try to load a string from an embedded assembly resource.
    /// Returns null if the resource doesn't exist.
    /// </summary>
    private static string? LoadFromEmbeddedResource(string resourceName)
    {
        try
        {
            var assembly = typeof(LocalizationService).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Minimal built-in strings so the app doesn't crash if locale files are missing.
    /// </summary>
    private void LoadMinimalFallback()
    {
        _strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["menu.install"] = "Install Mods",
            ["menu.update"] = "Update Mods",
            ["menu.uninstall"] = "Uninstall Mods",
            ["menu.launch"] = "Launch Game",
            ["menu.refresh"] = "Refresh",
            ["menu.exit"] = "Exit",
            ["menu.please_select"] = "Select [{0}-{1}]: ",
            ["menu.goodbye"] = "Goodbye!",
            ["error.game_not_found"] = "Game installation not found",
            ["info.all_latest"] = "All components are up to date",
            ["info.wait_return"] = "Press any key to return...",
            ["status.detecting_game"] = "Detecting game directory...",
            ["status.scanning"] = "Scanning installed components...",
            ["status.checking_updates"] = "Checking for updates...",
            ["component.not_installed"] = "Not installed",
            ["component.version_unknown"] = "Installed (unknown version)",
            ["component.latest_suffix"] = " (latest)",
        };
    }

    private void SaveLanguage()
    {
        try
        {
            var settingsPath = Path.Combine(_dataDir, "settings.json");
            var settings = new Dictionary<string, string>
            {
                ["language"] = CurrentLanguage
            };
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // Best effort
        }
    }

    private static string? LoadCachedLanguage(string dataDir)
    {
        try
        {
            var settingsPath = Path.Combine(dataDir, "settings.json");
            if (!File.Exists(settingsPath))
                return null;

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (settings != null && settings.TryGetValue("language", out var lang) &&
                (lang == "zh" || lang == "en"))
            {
                return lang;
            }
        }
        catch { }
        return null;
    }
}
