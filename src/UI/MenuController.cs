using System.Diagnostics;
using SlotWeave.Launcher.Models;
using SlotWeave.Launcher.Services;

namespace SlotWeave.Launcher.UI;

/// <summary>
/// Menu state machine controlling the launcher UI flow.
/// </summary>
public class MenuController
{
    private readonly GitHubService _github;
    private readonly GameDetector _detector;
    private readonly ComponentScanner _scanner;
    private readonly Installer _installer;
    private readonly Uninstaller _uninstaller;
    private readonly CacheManager _cacheManager;
    private readonly SelfUpdater _selfUpdater;
    private readonly LauncherConfig _config;

    private string? _gameDir;
    private List<InstalledComponent> _components = new();
    private bool _hasGdWeave;
    private CancellationTokenSource? _cts;

    public MenuController(
        GitHubService github,
        GameDetector detector,
        ComponentScanner scanner,
        Installer installer,
        Uninstaller uninstaller,
        CacheManager cacheManager,
        SelfUpdater selfUpdater,
        LauncherConfig config)
    {
        _github = github;
        _detector = detector;
        _scanner = scanner;
        _installer = installer;
        _uninstaller = uninstaller;
        _cacheManager = cacheManager;
        _selfUpdater = selfUpdater;
        _config = config;
    }

    public async Task RunAsync()
    {
        _cts = new CancellationTokenSource();

        try
        {
            // Phase 1: Show banner
            ConsoleUI.ShowBanner(_config.LauncherVersion);

            // Phase 2: Detect game directory
            ConsoleUI.ShowInfo(Loc.T("status.detecting_game"));
            _gameDir = _detector.DetectGameDirectory();

            if (_gameDir == null)
            {
                ConsoleUI.ShowError(Loc.T("error.game_not_found"));
                Console.WriteLine();
                Console.Write(Loc.T("error.enter_path"));
                _gameDir = Console.ReadLine()?.Trim('"', ' ');
                if (string.IsNullOrEmpty(_gameDir) || !Directory.Exists(_gameDir))
                {
                    ConsoleUI.ShowError(Loc.T("error.invalid_path"));
                    return;
                }
            }
            ConsoleUI.ShowSuccess(_gameDir);

            // Update all services with the game directory
            _scanner.SetGameDirectory(_gameDir);
            _installer.SetGameDirectory(_gameDir);
            _uninstaller.SetGameDirectory(_gameDir);
            _cacheManager.SetGameDirectory(_gameDir);

            if (!_config.GameDirectoryPaths.Contains(_gameDir))
            {
                _config.GameDirectoryPaths.Insert(0, _gameDir);
            }

            // Phase 3: Scan installed components
            Console.WriteLine();
            ConsoleUI.ShowInfo(Loc.T("status.scanning"));
            _components = _scanner.ScanAll();

            // Detect legacy GDWeave (pre-SlotWeave fork)
            _hasGdWeave = _scanner.DetectLegacyGdWeave();
            if (_hasGdWeave)
                ConsoleUI.ShowWarning(Loc.T("gdweave.detected"));

            foreach (var comp in _components)
            {
                var status = $"{comp.Definition.DisplayName}: {comp.VersionDisplay}";
                if (comp.IsPartial)
                    ConsoleUI.ShowWarning(status);
                else if (comp.IsInstalled && !comp.HasUpdate)
                    ConsoleUI.ShowSuccess(status);
                else if (comp.HasUpdate)
                    ConsoleUI.ShowWarning(status);
                else
                    ConsoleUI.ShowInfo(status);
            }

            // Check if winmm.dll was eaten by antivirus
            if (_gameDir != null && !File.Exists(Path.Combine(_gameDir, "winmm.dll")))
                ConsoleUI.ShowError(Loc.T("warn.winmm_missing"));

            // Phase 4: Check GitHub for updates
            Console.WriteLine();
            await CheckForUpdatesAsync();

            // Phase 4.5: Check for launcher self-update
            var hasLauncherUpdate = await _selfUpdater.CheckAsync();
            if (hasLauncherUpdate)
                ConsoleUI.ShowWarning(Loc.T("selfupdate.available",
                    _selfUpdater.LatestVersion ?? "?", _config.LauncherVersion));

            // Phase 5: Main menu loop
            await MainMenuLoopAsync();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
        }
        finally
        {
            _cts?.Dispose();
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        ConsoleUI.ShowInfo(Loc.T("status.checking_updates"));

        // Version check via Atom feed — zero API calls, zero rate limit.
        // The full release (with asset download URL) is fetched later,
        // only when the user actually selects a component to install/update.
        var tasks = _components.Select(async comp =>
        {
            var version = await _github.GetLatestVersionAsync(
                comp.Definition.Owner, comp.Definition.Repo);

            if (version != null)
            {
                comp.LatestVersion = version;
                comp.LatestVersionCheckFailed = false;

                // State transition: if installed and remote is newer → UpdateAvailable
                if (comp.State == ComponentState.Installed
                    && comp.InstalledVersion != null
                    && comp.InstalledVersion != version)
                {
                    comp.TransitionTo(ComponentState.UpdateAvailable);
                }
                // State transition: was update-available, but now same version →
                // GitHub release was deleted/reverted
                else if (comp.State == ComponentState.UpdateAvailable
                    && comp.InstalledVersion == version)
                {
                    comp.TransitionTo(ComponentState.Installed);
                }
            }
            else
            {
                comp.LatestVersionCheckFailed = true;
            }
        });

        await Task.WhenAll(tasks);

        // Count by explicit state (per final.md §2.3)
        var updateCount = _components.Count(c => c.State == ComponentState.UpdateAvailable);
        var partialCount = _components.Count(c => c.State == ComponentState.PartialInstall);
        var corruptedCount = _components.Count(c => c.State == ComponentState.Corrupted);
        var newCount = _components.Count(c => c.State == ComponentState.NotInstalled);
        var checkFailedCount = _components.Count(c => c.LatestVersionCheckFailed);

        if (checkFailedCount > 0)
            ConsoleUI.ShowWarning(Loc.T("info.check_failed", checkFailedCount));
        if (corruptedCount > 0)
            ConsoleUI.ShowError(Loc.T("info.corrupted_detected", corruptedCount));
        if (partialCount > 0)
            ConsoleUI.ShowWarning(Loc.T("info.incomplete_detected", partialCount));
        if (updateCount > 0)
            ConsoleUI.ShowWarning(Loc.T("info.updates_available", updateCount));
        else if (newCount > 0)
            ConsoleUI.ShowInfo(Loc.T("info.installable", newCount));
        else if (updateCount == 0 && partialCount == 0 && corruptedCount == 0)
            ConsoleUI.ShowSuccess(Loc.T("info.all_latest"));
    }

    private async Task MainMenuLoopAsync()
    {
        while (true)
        {
            ConsoleUI.ShowPage();
            ConsoleUI.Separator();

            var hasNew = _components.Any(c => !c.IsInstalled && !c.IsPartial);
            var hasUpdates = _components.Any(c => c.HasUpdate || c.IsPartial);
            var hasInstalled = _components.Any(c => c.IsInstalled);

            var menuMap = new Dictionary<int, string>();
            int nextIdx = 1;

            // Self-update always at top when available
            if (_selfUpdater.HasUpdate)
                menuMap[nextIdx++] = "selfupdate";

            // Legacy GDWeave migration (hidden when not present)
            if (_hasGdWeave)
                menuMap[nextIdx++] = "gdweave";

            if (hasNew)
                menuMap[nextIdx++] = "install";
            if (hasUpdates)
                menuMap[nextIdx++] = "update";
            if (hasInstalled)
                menuMap[nextIdx++] = "uninstall";

            menuMap[nextIdx++] = "launch";
            menuMap[nextIdx++] = "refresh";

            foreach (var kvp in menuMap)
            {
                var label = kvp.Value switch
                {
                    "selfupdate" => Loc.T("selfupdate.menu"),
                    "gdweave" => Loc.T("gdweave.menu"),
                    "install" => Loc.T("menu.install"),
                    "update" => Loc.T("menu.update"),
                    "uninstall" => Loc.T("menu.uninstall"),
                    "launch" => Loc.T("menu.launch"),
                    "refresh" => Loc.T("menu.refresh"),
                    _ => kvp.Value
                };
                Console.WriteLine($"  [{kvp.Key}] {label}");
            }
            Console.WriteLine($"  [0] {Loc.T("menu.exit")}");

            // Status display
            Console.WriteLine();
            Console.WriteLine($"  {Loc.T("menu.status")}:");
            foreach (var comp in _components)
            {
                ConsoleUI.ShowStatus(
                    comp.Definition.DisplayName,
                    comp.VersionDisplay,
                    comp.HasUpdate || comp.IsPartial);
            }

            ConsoleUI.Separator();
            Console.Write(Loc.T("menu.please_select", 0, nextIdx - 1));

            var key = ConsoleUI.TryReadKey(true);
            if (key == null) { await Task.Delay(100); continue; }
            Console.WriteLine(key.Value.KeyChar);

            if (key.Value.KeyChar == '0')
            {
                ConsoleUI.ShowInfo(Loc.T("menu.goodbye"));
                return;
            }

            if (!int.TryParse(key.Value.KeyChar.ToString(), out int menuChoice)
                || !menuMap.TryGetValue(menuChoice, out var action))
                continue;

            switch (action)
            {
                case "selfupdate":
                    await _selfUpdater.UpdateAsync();
                    break;
                case "gdweave":
                    RemoveLegacyGdWeave();
                    break;
                case "install":
                case "update":
                case "uninstall":
                    await ShowSubMenuAsync(action);
                    break;
                case "launch":
                    LaunchGame();
                    break;
                case "refresh":
                    ConsoleUI.ShowInfo(Loc.T("status.refreshing"));
                    _components = _scanner.ScanAll();
                    await CheckForUpdatesAsync();
                    break;
            }
        }
    }

    private async Task ShowSubMenuAsync(string mode)
    {
        while (true)
        {
            var title = mode switch
            {
                "install" => Loc.T("submenu.install_title"),
                "update" => Loc.T("submenu.update_title"),
                "uninstall" => Loc.T("submenu.uninstall_title"),
                _ => mode
            };

            Console.WriteLine();
            ConsoleUI.ShowHeader(title);

            var relevant = mode switch
            {
                "install" => _components.Where(c => !c.IsInstalled && !c.IsPartial).ToList(),
                "update" => _components.Where(c => c.HasUpdate || c.IsPartial).ToList(),
                "uninstall" => _components.Where(c => c.IsInstalled || c.IsPartial).ToList(),
                _ => new List<InstalledComponent>()
            };

            if (relevant.Count == 0)
            {
                var msg = mode switch
                {
                    "install" => Loc.T("submenu.all_installed"),
                    "update" => Loc.T("submenu.no_updates"),
                    "uninstall" => Loc.T("submenu.nothing_installed"),
                    _ => Loc.T("submenu.no_operations")
                };
                ConsoleUI.ShowInfo(msg);
                ConsoleUI.WaitForKey();
                return;
            }

            if (mode != "uninstall" && relevant.Count > 1)
            {
                var label = mode == "install"
                    ? Loc.T("submenu.install_all")
                    : Loc.T("submenu.update_all");
                Console.WriteLine($"  [1] {label}{Loc.T("submenu.component_count", relevant.Count)}");
            }

            int idx = (mode != "uninstall" && relevant.Count > 1) ? 2 : 1;
            foreach (var comp in relevant)
            {
                var label = mode switch
                {
                    "install" => $"{comp.Definition.DisplayName} (v{comp.LatestVersion})",
                    "update" => comp.IsPartial
                        ? $"{comp.Definition.DisplayName}: {Loc.T("component.incomplete")} → v{comp.LatestVersion} ⚠"
                        : $"{comp.Definition.DisplayName}: {comp.InstalledVersion} → {comp.LatestVersion} ⚠",
                    "uninstall" => comp.IsPartial
                        ? $"{comp.Definition.DisplayName}: {Loc.T("component.incomplete")}"
                        : $"{comp.Definition.DisplayName} v{comp.InstalledVersion}",
                    _ => comp.Definition.DisplayName
                };
                Console.WriteLine($"  [{idx}] {label}");
                idx++;
            }
            Console.WriteLine($"  [0] {Loc.T("menu.back")}");

            ConsoleUI.Separator();
            Console.Write(Loc.T("menu.please_select", 0, idx - 1));

            var key = ConsoleUI.TryReadKey(true);
            if (key == null) { await Task.Delay(100); continue; }
            Console.WriteLine(key.Value.KeyChar);

            if (key.Value.KeyChar == '0')
                return;

            if (int.TryParse(key.Value.KeyChar.ToString(), out int choice) && choice > 0)
            {
                if (mode != "uninstall" && relevant.Count > 1 && choice == 1)
                {
                    await InstallAllAsync(relevant, mode);
                    ConsoleUI.WaitForKey();
                    return;
                }

                var offset = (mode != "uninstall" && relevant.Count > 1) ? 2 : 1;
                var compIdx = choice - offset;
                if (compIdx >= 0 && compIdx < relevant.Count)
                {
                    await ExecuteOperationAsync(relevant[compIdx], mode);
                    ConsoleUI.WaitForKey();
                    return;
                }
            }
        }
    }

    private async Task ExecuteOperationAsync(InstalledComponent component, string mode)
    {
        if (_gameDir == null) return;

        switch (mode)
        {
            case "install":
            case "update":
                Console.WriteLine();
                ConsoleUI.ShowInfo(Loc.T("status.fetching_release", component.Definition.DisplayName));
                var release = await _github.GetLatestReleaseAsync(
                    component.Definition.Owner, component.Definition.Repo);

                if (release == null)
                {
                    ConsoleUI.ShowError(Loc.T("error.release_fetch_failed"));
                    return;
                }

                var success = await _installer.InstallOrUpdateAsync(component, release, _cts?.Token ?? default);
                if (success)
                {
                    await RefreshComponentStateAsync(component);
                }
                break;

            case "uninstall":
                var uninstalled = _uninstaller.Uninstall(component);
                if (uninstalled)
                {
                    await RefreshComponentStateAsync(component);
                }
                break;
        }
    }

    private async Task InstallAllAsync(List<InstalledComponent> components, string mode)
    {
        Console.WriteLine();
        var header = mode == "install"
            ? Loc.T("submenu.install_all")
            : Loc.T("submenu.update_all");
        ConsoleUI.ShowHeader(header);

        int succeeded = 0;
        int failed = 0;

        var ordered = components
            .OrderBy(c => c.Definition.IsCore ? 0 : 1)
            .ToList();

        foreach (var comp in ordered)
        {
            var release = await _github.GetLatestReleaseAsync(
                comp.Definition.Owner, comp.Definition.Repo);

            if (release == null)
            {
                ConsoleUI.ShowError(Loc.T("error.component_fetch_failed", comp.Definition.DisplayName));
                failed++;
                continue;
            }

            var success = await _installer.InstallOrUpdateAsync(comp, release, _cts?.Token ?? default);
            if (success)
            {
                succeeded++;
                await RefreshComponentStateAsync(comp);
            }
            else
            {
                failed++;
                ConsoleUI.ShowError(Loc.T("error.component_op_failed", comp.Definition.DisplayName));
            }

            Console.WriteLine();
        }

        ConsoleUI.ShowHeader(Loc.T("result.summary"));
        ConsoleUI.ShowSuccess(Loc.T("result.success_count", succeeded));
        if (failed > 0)
            ConsoleUI.ShowError(Loc.T("result.fail_count", failed));
    }

    private async Task RefreshComponentStateAsync(InstalledComponent component)
    {
        if (_gameDir == null) return;

        // Re-scan the component's actual on-disk state (O(1) file I/O for this component only)
        var fresh = _scanner.ScanOne(component.Definition, _gameDir);

        // Apply scanner-authoritative state to the live component
        component.SetStateFromScan(fresh.State);
        component.UpdateInstalledVersion(fresh.InstalledVersion);

        // Re-fetch latest version from GitHub so status is immediately correct
        var version = await _github.GetLatestVersionAsync(
            component.Definition.Owner, component.Definition.Repo);
        if (version != null)
        {
            component.LatestVersion = version;
            component.LatestVersionCheckFailed = false;
        }
        else
        {
            component.LatestVersionCheckFailed = true;
        }
    }

    private void RemoveLegacyGdWeave()
    {
        ConsoleUI.ShowHeader(Loc.T("gdweave.title"));
        Console.WriteLine();
        ConsoleUI.ShowWarning(Loc.T("gdweave.confirm"));
        Console.Write(Loc.T("warn.continue_confirm"));

        if (!ConfirmYesNo())
        {
            ConsoleUI.ShowInfo(Loc.T("info.cancelled"));
            ConsoleUI.WaitForKey();
            return;
        }

        Console.WriteLine();
        var ok = _scanner.RemoveLegacyGdWeave();
        Console.WriteLine();

        if (ok)
        {
            ConsoleUI.ShowSuccess(Loc.T("gdweave.done"));
            _hasGdWeave = false;
        }

        ConsoleUI.WaitForKey();
    }

    private static bool ConfirmYesNo()
    {
        var key = ConsoleUI.TryReadKey(true);
        if (key == null) return false;
        Console.WriteLine();
        return key.Value.Key == ConsoleKey.Y;
    }

    private void LaunchGame()
    {
        if (_gameDir == null)
        {
            ConsoleUI.ShowError(Loc.T("error.game_dir_not_set"));
            return;
        }

        var exePath = Path.Combine(_gameDir, _config.GameExecutable);
        if (!File.Exists(exePath))
        {
            ConsoleUI.ShowError(Loc.T("error.exe_not_found", exePath));
            return;
        }

        try
        {
            ConsoleUI.ShowInfo(Loc.T("status.starting_game"));
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _gameDir,
                UseShellExecute = true
            });
            ConsoleUI.ShowSuccess(Loc.T("status.game_started"));
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError(Loc.T("error.launch_failed", ex.Message));
        }
    }
}
