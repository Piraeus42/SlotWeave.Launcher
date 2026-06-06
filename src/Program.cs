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

        // Clean up any leftover .new from a failed self-update
        CompletePendingUpdate();

        try
        {
            // Load configuration
            var configPath = FindConfigFile();
            var config = LoadConfig(configPath);

            // Sync version from assembly (source of truth) into config.
            // The APPDATA config may carry a stale version from a previous
            // installation — the running binary knows its actual version.
            var asmVersion = GetAssemblyVersion();
            if (config.LauncherVersion != asmVersion)
            {
                config.LauncherVersion = asmVersion;
                SaveConfig(configPath, config);
            }

            ConsoleUI.SetVersion(config.LauncherVersion);

            var exeDir = GetExeDir();
            var dataDir = GetDataDir();

            // Initialize localization: check cache or show language picker
            var loc = await LocalizationService.InitializeAsync(dataDir);
            if (loc == null)
            {
                // First launch — show banner, then language picker
                ShowMinimalBanner(config.LauncherVersion);
                var lang = LocalizationService.ShowLanguagePicker();
                InitializeLanguage(dataDir, lang);
            }

            // Create services
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(config.DownloadTimeoutSeconds);

            var githubService = new GitHubService(httpClient);
            var detector = new GameDetector(config, exeDir);
            var scanner = new ComponentScanner(config);
            var cacheManager = new CacheManager(config);
            var installer = new Installer(githubService, scanner, cacheManager, config);
            var uninstaller = new Uninstaller(cacheManager, config);
            var selfUpdater = new SelfUpdater(githubService, config, exeDir);

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
    /// Clean up stale .old files and complete any interrupted .new replacement.
    /// Uses the same rename-based swap as SelfUpdater — no batch files.
    /// </summary>
    private static void CompletePendingUpdate()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath == null) return;

            var newPath = exePath + ".new";
            var oldPath = exePath + ".old";
            var exeDir = Path.GetDirectoryName(exePath) ?? ".";

            // Clean up leftover .old from a previous successful update
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); }
                catch { /* will retry next launch */ }
            }

            // If no .new waiting, nothing to complete
            if (!File.Exists(newPath)) return;

            // A .new is waiting — the previous update didn't finish the swap.
            // Complete it: rename current→.old, then rename .new→current.
            try { File.Delete(oldPath); } catch { }

            try
            {
                File.Move(exePath, oldPath);
            }
            catch
            {
                // Can't even rename current exe — leave .new for next attempt
                return;
            }

            try
            {
                File.Move(newPath, exePath);
            }
            catch
            {
                // Rollback: put original exe back
                try { File.Move(oldPath, exePath); } catch { }
                return;
            }

            // Success — start the new exe
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = exeDir,
                    UseShellExecute = true
                });
                Environment.Exit(0);
            }
            catch { /* user will restart manually */ }
        }
        catch { /* best effort */ }
    }

    private static string GetExeDir()
        => Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;

    private static string GetDataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SlotWeave.Launcher");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindConfigFile()
    {
        // Config lives in %APPDATA% for portability — zero loose files next to exe
        var dataDir = GetDataDir();
        var configPath = Path.Combine(dataDir, "launcher_config.json");
        return configPath;
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
            LauncherVersion = GetAssemblyVersion(),
            LauncherRepo = new LauncherRepoInfo
            {
                Owner = "Piraeus42",
                Repo = "SlotWeave.Launcher",
                AssetPattern = "SlotWeave.Launcher.exe"
            }
        };
    }

    /// <summary>
    /// Read version from the assembly (matches csproj &lt;Version&gt;).
    /// Returns "0.0.0" if unreadable.
    /// </summary>
    private static string GetAssemblyVersion()
    {
        try
        {
            var attr = typeof(Program).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (attr.Length > 0)
            {
                var ver = ((System.Reflection.AssemblyInformationalVersionAttribute)attr[0]).InformationalVersion;
                // Strip "+" suffix (semver build metadata) if present
                var plus = ver.IndexOf('+');
                return plus > 0 ? ver[..plus] : ver;
            }

            var asmVer = typeof(Program).Assembly.GetName().Version;
            if (asmVer != null)
                return $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}";
        }
        catch { }
        return "0.0.0";
    }
}
