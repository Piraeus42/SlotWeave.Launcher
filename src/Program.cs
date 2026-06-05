using System.Text.Json;
using SlotWeave.Launcher.Models;
using SlotWeave.Launcher.Services;
using SlotWeave.Launcher.UI;

namespace SlotWeave.Launcher;

class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "SlotWeave Launcher";

        try
        {
            // Load configuration
            var configPath = FindConfigFile();
            var config = LoadConfig(configPath);

            ConsoleUI.SetVersion(config.LauncherVersion);

            var launcherDir = Path.GetDirectoryName(configPath)
                ?? Environment.CurrentDirectory;

            // Initialize localization: check cache or show language picker
            var loc = await LocalizationService.InitializeAsync(launcherDir);
            if (loc == null)
            {
                // First launch — show banner, then language picker
                ShowMinimalBanner(config.LauncherVersion);
                var lang = LocalizationService.ShowLanguagePicker();
                InitializeLanguage(launcherDir, lang);
            }

            // Create services
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(config.DownloadTimeoutSeconds);

            var githubService = new GitHubService(httpClient);
            var detector = new GameDetector(config, launcherDir);
            var scanner = new ComponentScanner(config);
            var cacheManager = new CacheManager(config);
            var installer = new Installer(githubService, scanner, cacheManager, config);
            var uninstaller = new Uninstaller(cacheManager, config);
            var selfUpdater = new SelfUpdater(githubService, config, launcherDir);

            // Create menu controller
            var menu = new MenuController(
                githubService, detector, scanner,
                installer, uninstaller, cacheManager,
                selfUpdater, config);

            // Run the launcher
            await menu.RunAsync();

            // Save updated config (with found game path)
            SaveConfig(configPath, config);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            try
            {
                Console.WriteLine($"\n{Loc.T("error.fatal", ex.Message)}");
            }
            catch
            {
                Console.WriteLine($"\nFatal error: {ex.Message}");
            }
            Console.ResetColor();
            Console.WriteLine(ex.StackTrace);
            try
            {
                var msg = Loc.T("error.press_any_key");
                Console.WriteLine($"\n{msg}");
            }
            catch
            {
                Console.WriteLine("\nPress any key to exit...");
            }
            try { Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { /* non-interactive terminal */ }
        }
    }

    private static void ShowMinimalBanner(string version)
    {
        ConsoleUI.SetVersion(version);
        ConsoleUI.ClearScreen();
        ConsoleUI.DrawBanner();
        Console.WriteLine();
    }

    private static void InitializeLanguage(string launcherDir, string lang)
    {
        // Create a fresh LocalizationService, set language, and persist
        var service = new LocalizationService();
        service.SetLanguageForFirstLaunch(launcherDir, lang);
    }

    /// <summary>
    /// Find the launcher_config.json file.
    /// </summary>
    private static string FindConfigFile()
    {
        // Use the actual exe directory (not extraction temp in single-file mode)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;

        var configPath = Path.Combine(exeDir, "launcher_config.json");
        if (File.Exists(configPath))
            return configPath;

        configPath = Path.Combine(Environment.CurrentDirectory, "launcher_config.json");
        if (File.Exists(configPath))
            return configPath;

        return Path.Combine(exeDir, "launcher_config.json");
    }

    /// <summary>
    /// Load and parse the configuration file.
    /// </summary>
    private static LauncherConfig LoadConfig(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<LauncherConfig>(json, JsonOpts)
                    ?? CreateDefaultConfig();
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowWarning($"Config parse failed: {ex.Message}");
                ConsoleUI.ShowInfo("Using default configuration");
            }
        }

        var config = CreateDefaultConfig();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch { /* best effort */ }

        return config;
    }

    /// <summary>
    /// Save the configuration file with updated paths.
    /// </summary>
    private static void SaveConfig(string path, LauncherConfig config)
    {
        try
        {
            config.GameDirectoryPaths = config.GameDirectoryPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .Take(10)
                .ToList();

            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Create the default configuration.
    /// </summary>
    private static LauncherConfig CreateDefaultConfig()
    {
        return new LauncherConfig
        {
            Repositories = new List<ComponentDefinition>
            {
                new()
                {
                    Id = "slotweave",
                    Owner = "Piraeus42",
                    Repo = "SlotWeave",
                    DisplayName = "SlotWeave Framework",
                    InstallPath = "SlotWeave",
                    IsCore = true,
                    ManifestFile = "SlotWeave/version.json",
                    AssetPattern = "SlotWeave.zip",
                    GameFiles = new List<string> { "winmm.dll", "SlotWeave/" }
                },
                new()
                {
                    Id = "betterlandlord",
                    Owner = "Piraeus42",
                    Repo = "BetterLandlord",
                    DisplayName = "Better Landlord",
                    InstallPath = "SlotWeave/mods/Piraeus.BetterLandlord",
                    IsCore = false,
                    ManifestFile = "SlotWeave/mods/Piraeus.BetterLandlord/version.json",
                    AssetPattern = "BetterLandlord-v*.zip",
                    DependsOn = "slotweave"
                }
            },
            GameExecutable = "Luck be a Landlord.exe",
            GameDirectoryPaths = new List<string>(),
            SteamAppId = null,
            CacheDirectories = new List<string>
            {
                "SlotWeave/logs",
                "SlotWeave/cache"
            },
            BackupDirectory = ".slotweave_launcher/backups",
            TempDirectory = ".slotweave_launcher/temp",
            MaxBackups = 3,
            DownloadTimeoutSeconds = 300,
            DownloadMirrors = new List<string>
            {
                "https://ghproxy.com/",
                "https://mirror.ghproxy.com/"
            },
            LauncherVersion = "1.0.0",
            LauncherRepo = new LauncherRepoInfo
            {
                Owner = "Piraeus42",
                Repo = "SlotWeave.Launcher",
                AssetPattern = "SlotWeave.Launcher.exe"
            }
        };
    }
}
