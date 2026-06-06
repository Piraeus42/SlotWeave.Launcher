# SlotWeave Launcher — Architecture & State Machine Audit

> Generated 2026-06-06 from source analysis. No code changes were made in the writing of this document.

---

## 1. Project Overview

**SlotWeave Launcher** is a .NET 8.0 single-file Windows console application that manages the installation, update, and uninstallation of the SlotWeave mod framework for the game *Luck be a Landlord*. It also self-updates.

| Attribute | Value |
|-----------|-------|
| Language | C# 12 |
| Runtime | .NET 8.0 |
| Output | Single-file, framework-dependent exe (win-x64) |
| Assembly Name | `SlotWeave一键模组更新管理器` |
| Self-update | GitHub Releases → rename-based atomic swap |
| Package manager | None (no NuGet dependencies beyond BCL) |

### Source Tree

```
SlotWeave.Launcher.csproj
launcher_config.json              ← repo template (not used at runtime)
Locales/
  en.json, zh.json                ← embedded resources
Resources/
  winmm.dll                       ← embedded resource (AV-resilient deployment)
src/
  Program.cs                      ← entry point, config lifecycle, startup sequence
  UI/
    ConsoleUI.cs                  ← terminal rendering (banner, status, menu)
    MenuController.cs             ← main menu state machine, submenus, operations
    ProgressBar.cs                ← download progress bar
  Models/
    LauncherConfig.cs             ← root config model + LauncherRepoInfo
    ComponentDefinition.cs        ← repo/component definition + InstalledComponent
    GitHubRelease.cs              ← GitHub API response models
  Services/
    GitHubService.cs              ← Atom feed (version check) + REST API (download)
    SelfUpdater.cs                ← launcher self-update logic
    GameDetector.cs               ← game directory auto-detection
    ComponentScanner.cs           ← installed component state scanner
    Installer.cs                  ← download/extract/backup/rollback
    Uninstaller.cs                ← safe uninstall with backup
    CacheManager.cs               ← cache directory cleanup
    LocalizationService.cs        ← locale loading, language cache
    Loc.cs                        ← T() static shortcut
```

### Data Directories

| Path | Purpose |
|------|---------|
| `%APPDATA%\SlotWeave.Launcher\` | Config, settings, language cache |
| `{exe_dir}\` | Single-file exe, `.old` / `.new` during self-update |
| `{game_dir}\` | Detected game installation root |
| `{game_dir}\.slotweave_launcher\` | Backups, temp downloads, extraction |

---

## 2. Configuration Lifecycle

### 2.1 Config Sources (precedence)

```
1. %APPDATA%\SlotWeave.Launcher\launcher_config.json  ← runtime source of truth
2. LauncherConfig C# defaults                           ← fallback if file missing
3. Assembly version (GetAssemblyVersion)                ← overrides #1 on startup
```

### 2.2 Config Flow

```
START
  │
  ├─ FindConfigFile() → %APPDATA%\SlotWeave.Launcher\launcher_config.json
  │
  ├─ LoadConfig(path)
  │    ├─ File exists? → Deserialize JSON → LauncherConfig
  │    └─ No file?     → CreateDefaultConfig() → Save → Return
  │
  ├─ [Version Sync]  GetAssemblyVersion() != config.LauncherVersion?
  │    └─ Yes → config.LauncherVersion = asmVersion → SaveConfig()
  │
  ├─ … app runs …
  │
  └─ SaveConfig(path, config)   ← on clean exit only
       └─ Deduplicates GameDirectoryPaths, limits to 10
```

### 2.3 Version Number Sources

| Source | How Set | Persisted? |
|--------|---------|------------|
| `csproj <Version>` | Author at build time | Source file |
| `AssemblyInformationalVersionAttribute` | .NET SDK from csproj | Assembly metadata |
| `GetAssemblyVersion()` | Reads assembly attribute | N/A |
| `LauncherConfig.LauncherVersion` default | `"1.0.2"` (hardcoded) | In code |
| `CreateDefaultConfig().LauncherVersion` | `GetAssemblyVersion()` | Writes to APPDATA |
| `config.LauncherVersion` at runtime | Synced from assembly on `Main()` | APPDATA config file |
| `TrySaveConfigVersion()` in SelfUpdater | Written during self-update swap | APPDATA config file |

**Bug risk**: `LauncherConfig.LauncherVersion` field default (`"1.0.2"`) is stale unless manually kept in sync with csproj. However, the `Main()` version sync (`GetAssemblyVersion()`) overwrites it every startup, and `CreateDefaultConfig()` also uses the assembly. So the field default only matters if a `new LauncherConfig {}` is constructed without going through the factory.

---

## 3. Startup Sequence State Machine

```
┌─────────────────────────────────────────────────────────────┐
│ Program.Main()                                               │
│                                                              │
│  [A] CompletePendingUpdate()                                 │
│       ├─ .old exists?   → Delete .old (cleanup)              │
│       ├─ .new exists?   → rename current→.old, .new→current  │
│       │   ├─ Success    → Start new exe → Exit(0)            │
│       │   └─ Fail       → Rollback .old→current, stay alive  │
│       └─ Nothing        → continue                           │
│                                                              │
│  [B] LoadConfig() + Version Sync                             │
│       └─ Assembly version → config.LauncherVersion           │
│                                                              │
│  [C] Localization.InitializeAsync()                          │
│       ├─ Cached language? → load, register singleton         │
│       └─ First launch     → null → ShowLanguagePicker()      │
│                                                              │
│  [D] Game Detection                                          │
│       ├─ Config paths → Steam libraries → Registry           │
│       ├─ Found? → proceed                                   │
│       └─ Not found → prompt user → validate path            │
│                                                              │
│  [E] Component Scan                                          │
│       └─ ScanAll() → InstalledComponent list                 │
│           ├─ installDir exists?                              │
│           │   ├─ Yes → IsInstalled=true → verify GameFiles   │
│           │   │   ├─ All present → ok                        │
│           │   │   └─ Some missing → IsPartial=true ⚠         │
│           │   └─ No  → check GameFiles individually          │
│           │       ├─ All present → installed                 │
│           │       ├─ Some present → IsPartial=true ⚠         │
│           │       └─ None → IsInstalled=false ○              │
│           └─ ReadInstalledVersion() (version.json/DLL)       │
│                                                              │
│  [F] CheckForUpdatesAsync()                                  │
│       └─ Per component: GetLatestVersionAsync() via Atom     │
│          └─ Set comp.LatestVersion                           │
│       └─ Compute counts: updateCount, partialCount, newCount │
│                                                              │
│  [G] SelfUpdater.CheckAsync()                                │
│       └─ GetLatestVersionAsync() via Atom (launcher)  │
│       └─ IsNewer(remote, local)? → HasUpdate flag            │
│                                                              │
│  [H] MainMenuLoopAsync()  ← infinite loop                    │
│       └─ Refresh: goto [E]                                   │
│       └─ Exit: return to [I]                                 │
│                                                              │
│  [I] SaveConfig()  → exit                                    │
└─────────────────────────────────────────────────────────────┘
```

### Known State Machine Gaps

1. **Step [A] → [B]: No version check after swap.** If `CompletePendingUpdate` swaps to a new exe and starts it, the new exe begins at [A] from scratch. However, if the swap fails (`.new`→current rename fails with rollback), the current process continues to [B] with the `.new` file still on disk — it will be retried next launch. The user gets no feedback that a pending update failed.

2. **Step [E] → [F] gap**: If `CheckForUpdatesAsync` fails (network error), `comp.LatestVersion` stays null. The status display shows the installed version without "(latest)" suffix, but no error is surfaced beyond a generic message.

3. **Step [G] isolated from [F]**: Launcher self-update check is independent of component checks. If both the launcher AND components have updates, the menu shows both, but there's no dependency ordering (e.g., "update launcher first").

---

## 4. InstalledComponent State Transitions

### 4.1 State Properties

```
┌─────────────────────────────────────────────────────────────┐
│ InstalledComponent                                           │
│                                                              │
│  IsInstalled  ∈ {false, true}                                │
│  IsPartial    ∈ {false, true}                                │
│  InstalledVersion ∈ {null, "1.0.0", "1.0.3", …}             │
│  LatestVersion    ∈ {null, "1.0.0", "1.0.3", …}             │
│                                                              │
│  Derived:                                                     │
│  HasUpdate   = IsInstalled                                   │
│             && InstalledVersion != null                       │
│             && LatestVersion != null                          │
│             && InstalledVersion != LatestVersion              │
│                                                              │
│  NeedsRepair = IsPartial                                     │
│  NeedsAction = HasUpdate || NeedsRepair                      │
│                                                              │
│  StatusIcon  = IsPartial     ? "⚠"                           │
│              : !IsInstalled  ? "○"                           │
│              : HasUpdate     ? "⚠"                           │
│              :                 "✓"                           │
└─────────────────────────────────────────────────────────────┘
```

### 4.2 Component State Transitions

```
                    ┌──────────────────────────────────────────┐
                    │           NOT INSTALLED                    │
                    │  IsInstalled=false, IsPartial=false        │
                    │  InstalledVersion=null                     │
                    │  StatusIcon="○"                            │
                    └──────┬──────────────────────┬─────────────┘
                           │                      │
              install/     │                      │ partial file
              update       │                      │ detection on scan
              success      │                      │
                           ▼                      ▼
                    ┌──────────────┐    ┌──────────────────────┐
                    │  INSTALLED   │    │      INCOMPLETE      │
                    │  ✓ latest    │    │  IsPartial=true ⚠    │
                    │              │    │                      │
                    │ HasUpdate=f  │    │ NeedsRepair=true     │
                    └──┬───┬───────┘    └────┬──────────┬──────┘
                       │   │                │           │
          new version  │   │  uninstall     │ install/  │ uninstall
          on GitHub    │   │  (full/partial)│ update    │ (partial)
                       │   │                │ success   │
                       ▼   ▼                │           ▼
            ┌──────────────┐                │    ┌──────────────┐
            │ HAS UPDATE ⚠ │                │    │ NOT INSTALLED│
            │              │                │    │ (back to top)│
            │ HasUpdate=t  │                │    └──────────────┘
            │ NeedsAction=t│                │
            └──────────────┘                │
                                            │
                              ┌─────────────┘
                              ▼
                    ┌──────────────────────┐
                    │      INSTALLED       │
                    │  ✓ latest (repaired) │
                    └──────────────────────┘
```

### 4.3 State Transition Triggers

| Trigger | Old State | New State | Method |
|---------|-----------|-----------|--------|
| Scan finds dir + all files | Any | INSTALLED | `ComponentScanner.ScanComponent()` |
| Scan finds dir + missing files | Any | INCOMPLETE | `ComponentScanner.ScanComponent()` |
| Scan finds no dir + some files | Any | INCOMPLETE | `ComponentScanner.ScanComponent()` |
| GitHub Atom shows newer version | INSTALLED | HAS UPDATE | `MenuController.CheckForUpdatesAsync()` |
| GitHub Atom shows same version | HAS UPDATE | INSTALLED | `MenuController.CheckForUpdatesAsync()` |
| Install/Update succeeds | INCOMPLETE | INSTALLED | `Installer.InstallOrUpdateAsync()` → `RefreshComponentStateAsync()` |
| Uninstall succeeds | INCOMPLETE | NOT INSTALLED | `Uninstaller.Uninstall()` → `RefreshComponentStateAsync()` |
| Uninstall succeeds | INSTALLED | NOT INSTALLED | `Uninstaller.Uninstall()` → `RefreshComponentStateAsync()` |
| Manual Refresh (menu option) | Any | Re-scanned | `MenuController.MainMenuLoopAsync()` refresh case |
| **Update fails (rollback)** | INSTALLED | INSTALLED (rolled back) | `Installer.InstallOrUpdateAsync()` — **state NOT refreshed!** |
| **AV deletes file after install** | INSTALLED | Still shows INSTALLED | Nothing detects this until next scan |

### 4.4 Critical State Machine Bugs

#### B1: `HasUpdate` can return false for INCOMPLETE state even when update would fix it

```csharp
public bool HasUpdate => IsInstalled          // ← FALSE when IsPartial && !IsInstalled
    && InstalledVersion != null
    && LatestVersion != null
    && InstalledVersion != LatestVersion;
```

When a component is `IsPartial=true`, `IsInstalled=true`, `InstalledVersion="1.0.2"`, `LatestVersion="1.0.3"`:
- `HasUpdate` = `true && true && true && ("1.0.2" != "1.0.3")` = **true** ✅

But when `IsPartial=true`, `IsInstalled=false` (install dir doesn't exist, but some GameFiles do):
- `HasUpdate` = `false && …` = **false** ❌
- `NeedsRepair` = `true` → `NeedsAction` = `true` ✅

**Impact**: Partial-not-installed components correctly show in "update" menu via `NeedsAction`. No user-visible bug.

#### B2: Update failure does not refresh component state

`Installer.InstallOrUpdateAsync()` on failure returns `false`, and `ExecuteOperationAsync` only calls `RefreshComponentStateAsync` on success. On extract failure with successful rollback, the component's in-memory state is stale (still shows old version as current). The user sees no visual change, but the `InstalledVersion` field still holds the pre-update value — which is _correct_ after rollback, but the scan didn't verify this.

**Impact**: Low. Rollback restores files, so next scan would be consistent. But if rollback also fails, the component state is orphaned.

#### B3: RefreshComponentStateAsync re-scans ALL components for one update

```csharp
var fresh = _scanner.ScanAll();    // ← rescans every component
var updated = fresh.FirstOrDefault(c => c.Definition.Id == component.Definition.Id);
```

This is wasteful (N file I/O operations for 1 update) but functionally correct. After "Install All", N×N scans occur (each `RefreshComponentStateAsync` scans all N components).

**Impact**: Performance only. Fix: add a `ScanOne(string id)` method.

#### B4: LatestVersion can be stale between refresh cycles

`CheckForUpdatesAsync` runs at startup and on manual refresh. Between those, component updates change `InstalledVersion`, and `RefreshComponentStateAsync` now re-fetches `LatestVersion`. But if the user sits on the main menu for hours, the "latest version" may be stale.

**Impact**: Low for a CLI tool with short sessions. No background polling exists.

#### B5: IsPartial detection is one-shot at scan time

`VerifyCriticalFiles` in `Installer` checks files post-extract and warns, but does NOT update `component.IsPartial`. The `InstalledComponent` in the `_components` list keeps its old `IsPartial` value. Only a full re-scan updates it.

```csharp
// Installer.cs line 134-135:
// Verify critical files survived
VerifyCriticalFiles(definition);  // ← warns but doesn't set IsPartial
```

Then:
```csharp
// Installer.cs line 160-164:
ConsoleUI.ShowSuccess(isUpdate
    ? Loc.T("result.update_ok", ...)
    : Loc.T("result.install_ok", ...));
return true;  // ← returns success even if critical files are missing!
```

After this, `RefreshComponentStateAsync` does a fresh `ScanAll()` which would detect missing files and set `IsPartial`. So the fix-up happens, but there's a window where the success message was already printed — the user sees "✅ Updated successfully" followed by "⚠ Incomplete" after refresh.

**Impact**: Medium. User gets conflicting messages: success then warning. The success should be conditional on `VerifyCriticalFiles` passing.

---

## 5. Self-Update State Machine

### 5.1 File States During Self-Update

```
Normal:           [SlotWeave.exe]
                    ↑ Environment.ProcessPath

Update pending:   [SlotWeave.exe] [SlotWeave.exe.new]
                    ↑ running        ↑ downloaded

Swapping:         [SlotWeave.exe.old] [SlotWeave.exe.new]
                    ↑ running (renamed)  ↑ to be renamed

After swap:       [SlotWeave.exe.old] [SlotWeave.exe]
                    ↑ stale              ↑ new (started by Process.Start)

After cleanup:    [SlotWeave.exe]
                    ↑ latest version
```

### 5.2 Self-Update Flow

```
SelfUpdater.CheckAsync()
  │
  ├─ LauncherRepo == null? → return false
  ├─ GetLatestVersionAsync() via Atom   ← 0 API calls
  ├─ version == null? → return false
  ├─ IsNewer(remote, local)? — no → return false
  └─ Yes → _latestVersion = remote → HasUpdate = true
       └─ _urlResolved = false  (deferred)

[User selects "Update Launcher" in menu]

SelfUpdater.UpdateAsync()
  │
  ├─ _urlResolved? — no → ResolveDownloadUrlAsync()  ← 1 API call
  │    └─ GetLatestReleaseAsync() → FindMatchingAsset() → download URL
  │
  ├─ DownloadAssetAsync() → .new file
  │    ├─ Direct URL → mirrors → fail
  │    └─ Success → continue
  │
  ├─ TrySaveConfigVersion()         ← persist new version to APPDATA
  │
  ├─ File.Move(current → .old)       ← rename running exe (Windows allows)
  │    └─ Fail → clean up .new, return false
  │
  ├─ File.Move(.new → current)       ← place new exe
  │    └─ Fail → File.Move(.old → current) rollback, return false
  │
  ├─ Process.Start(current)          ← start new exe
  └─ Environment.Exit(0)             ← exit old process
```

### 5.3 CompletePendingUpdate (startup cleanup)

```
CompletePendingUpdate()
  │
  ├─ .old exists? → File.Delete(.old)   ← best effort
  ├─ .new absent? → return             ← nothing to do
  │
  ├─ .new exists → previous update didn't finish swap
  │    ├─ File.Delete(.old)            ← clean stale
  │    ├─ File.Move(current → .old)    ← rename aside
  │    │    └─ Fail → return (retry next launch)
  │    ├─ File.Move(.new → current)    ← complete swap
  │    │    └─ Fail → File.Move(.old → current) rollback, return
  │    ├─ Process.Start(current)       ← start new exe
  │    └─ Environment.Exit(0)
```

### 5.4 Self-Update State Machine Bugs

#### S1: No integrity verification before swap

The downloaded `.new` file is never verified:
- No PE header check (`MZ` magic)
- No SHA256 comparison against GitHub's `asset.digest`
- No size sanity check
- A truncated or corrupted download silently replaces the working exe

**Impact**: High. If download is corrupted mid-stream and `HttpClient` doesn't detect it, the working launcher is replaced with garbage. Recovery requires manual re-download.

#### S2: Config version written before swap confirmed

`TrySaveConfigVersion()` writes the new version to APPDATA config BEFORE the rename swap happens. If the swap fails after this point:
- Config says `"1.0.3"` (new version)
- But the running exe is still `"1.0.2"` (swap was rolled back)
- On next launch: `GetAssemblyVersion()` → `"1.0.2"` → sync corrects it (step [B])
- Safe, but wastes a write. Would be better to write after successful swap.

**Impact**: Low. Version sync at startup corrects it. But config file is left in an inconsistent state between sessions.

#### S3: ResolveDownloadUrlAsync can fail after user already committed

`CheckAsync` reports "update available" based on Atom feed. But `UpdateAsync` calls `ResolveDownloadUrlAsync` which makes an API call that can fail (rate limit, network). The user already chose to update, but the download URL resolution fails. The error message is `error.no_release_asset` which is cryptic ("No matching asset found in Release 1.0.3").

**Impact**: Medium. User confusion. Should distinguish between "no asset matching pattern" vs "API call failed".

#### S4: IsNewer uses Version.TryParse → fragile for non-semver tags

```csharp
if (Version.TryParse(remote, out var rv) && Version.TryParse(local, out var lv))
    return rv > lv;
```

`System.Version` requires exactly `major.minor[.build[.revision]]`. Tags like `v1.0.3-beta1` or `v2026.06` fail `TryParse` and fall to string comparison. The `LooksLikePrerelease` filter in `GetLatestVersionAsync` catches `X.Y.Z-suffix`, but if GitHub tags use any other format, string comparison is unpredictable.

**Impact**: Low for current usage (tags are `v1.0.0`, `v1.0.1`, etc.).

---

## 6. GitHub Communication Layer

### 6.1 Dual-Channel Architecture

```
                    ┌─────────────────────────────┐
                    │       GitHubService           │
                    │                              │
                    │  GetLatestVersionAsync()      │
                    │    → releases.atom (Atom XML) │
                    │    → 0 rate limit, 0 auth     │
                    │    → returns version string   │
                    │    → used for: version checks │
                    │                              │
                    │  GetLatestReleaseAsync()      │
                    │    → /releases API (JSON)     │
                    │    → 1 rate-limit hit         │
                    │    → returns full release     │
                    │    → used for: asset download │
                    └─────────────────────────────┘
```

### 6.2 API Call Budget

| Operation | Calls / execution | Source |
|-----------|-------------------|--------|
| Startup version check | 0 (Atom) | `CheckForUpdatesAsync`, `CheckAsync` |
| Manual refresh | 0 (Atom) | Same |
| Launcher update (actual) | 1 (API) | `ResolveDownloadUrlAsync` |
| Component install/update (each) | 1 (API) | `ExecuteOperationAsync` → `GetLatestReleaseAsync` |
| "Install All" (N components) | N (API) | `InstallAllAsync` |

### 6.3 Atom Feed Parsing

```csharp
// Extracts tag from <link href="…/releases/tag/v1.0.3">
// Skips entries where the version looks like a prerelease (X.Y.Z-suffix)
// Returns version string without 'v' prefix
```

**Fallback**: If Atom feed fails (network error, XML parse error), `GetLatestVersionAsync` returns `null`. Callers treat this as "no update available" — they do NOT fall back to the API. This is a deliberate design choice to preserve rate limit budget, but means transient feed failures silently suppress update notifications.

### 6.4 Known Issues

#### G1: Atom feed failure is silent

`GetLatestVersionAsync` catches all exceptions and returns `null`. The caller (`CheckAsync`, `CheckForUpdatesAsync`) treats `null` the same as "no newer version." A transient GitHub outage means the user sees "all up to date" when they might not be.

**Impact**: Low (transient), but could mask issues. A `_lastCheckFailed` flag would let the UI show "⚠ Could not check for updates."

#### G2: Prerelease heuristic is regex-based

```csharp
private static bool LooksLikePrerelease(string version)
    => Regex.IsMatch(version, @"^\d+\.\d+\.\d+-");
```

This correctly matches `1.0.3-beta1`, `2.0.0-rc.1`, `1.0.0-alpha`. It misses non-semver prerelease indicators like `1.0.3beta` (no hyphen), `v1.0.3_preview` (underscore), or a release named "pre-release" with a normal tag.

**Impact**: Low. Currently no prereleases exist in the repos.

#### G3: FindMatchingAsset fallback chain order

```csharp
return release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name))
    ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", …))
    ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", …));
```

For a component that should download a `.zip`, if no asset matches the pattern and a `.exe` exists in the release, the `.exe` is returned. This silently downloads the wrong file type.

**Impact**: Low for current repo structure (each release has one asset type). Could become a problem if releases mix `.exe` and `.zip` assets.

---

## 7. Installer State Machine

### 7.1 InstallOrUpdate Flow

```
InstallOrUpdateAsync(component, release)
  │
  ├─ Validate: gameDir set? asset found?
  │
  ├─ [1] CheckAndCloseGame() — poll for "Luck be a Landlord" process
  │
  ├─ [2] Download zip → {tempDir}/{id}_{version}.zip
  │    └─ Fail → CleanupTemp, return false
  │
  ├─ [3] VerifyZip() — open as ZipFile, check has entries
  │    └─ Fail → CleanupTemp, return false
  │
  ├─ [4] CreateBackup() — if updating (IsInstalled==true)
  │    ├─ Copy install dir + GameFiles → {backupDir}/{id}_{timestamp}/
  │    ├─ PruneBackups() — keep only MaxBackups (3) most recent
  │    └─ Fail → warn, continue without backup
  │
  ├─ [5] ClearAll() — wipe cache directories
  │
  ├─ [6] Extract & Copy
  │    ├─ ZipFile.ExtractToDirectory(zip, extractDir)
  │    ├─ GetContentRoot() — strip GitHub wrapper dir if present
  │    ├─ CopyDirectory(source → target) — core to game root, mods to install path
  │    ├─ WriteEmbeddedWinmmDll() — core only, from embedded resource
  │    ├─ VerifyCriticalFiles() — check GameFiles exist, warn if missing
  │    └─ Fail → RestoreBackup() if updating, return false
  │
  ├─ [7] WriteVersionMarker() — version.json to manifest path
  │
  ├─ [8] CleanupTemp() — delete temp dir
  │
  └─ Return true
```

### 7.2 Backup/Rollback State

```
CreateBackup:
  {gameDir}/.slotweave_launcher/backups/{id}_{timestamp}/
    ├─ {InstallPath}/…     ← from game dir
    └─ {GameFiles}…         ← individual files

RestoreBackup:
  Finds most recent {id}_* backup dir
  CopyDirectory(backup → gameDir)  ← overwrite
```

### 7.3 Known Issues

#### I1: VerifyCriticalFiles warns but doesn't fail

After extraction, `VerifyCriticalFiles` checks each `GameFiles` entry and calls `ShowWarning` if missing. But the method returns `void` — the installer still reports SUCCESS. An antivirus-deleted `winmm.dll` means the user sees "✅ installed successfully" followed by "⚠ winmm.dll missing."

```csharp
// Installer.cs line 131-135
if (definition.IsCore)
    WriteEmbeddedWinmmDll();
VerifyCriticalFiles(definition);  // ← void return, no effect on success
// … continues to success path
```

**Impact**: High. The user is told installation succeeded when critical files are missing.

#### I2: WriteEmbeddedWinmmDll resource name is hardcoded

```csharp
const string resourceName = "SlotWeave.Launcher.Resources.winmm.dll";
```

The embedded resource logical name in the csproj is `SlotWeave.Launcher.Resources.winmm.dll`. If the assembly name changes (it did — to Chinese), or the resource path changes, this silently fails:

```csharp
using var stream = assembly.GetManifestResourceStream(resourceName);
if (stream == null) return; // Not embedded (dev build without Resources/)
```

The comment says "dev build without Resources/" but actually the resource name mismatch would also cause this in production.

**Impact**: Medium. On assembly rename, winmm.dll silently not written. The `VerifyCriticalFiles` would catch it with a warning, but the installer still reports success.

#### I3: Backup uses DateTime for ordering, not version

```csharp
var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
var componentBackupDir = Path.Combine(backupDir, $"{definition.Id}_{timestamp}");
```

Backups are ordered by timestamp, not by version number. If the user updates 1.0.2→1.0.3, then 1.0.3→1.0.4, the backup for 1.0.3 might be pruned while 1.0.2 is kept (if 1.0.2 backup was created later due to clock issues). `RestoreBackup` picks the most recent by directory name sort, which is timestamp-based.

**Impact**: Low. Clock skew between updates is unlikely in practice.

#### I4: No download integrity check for component zips

`VerifyZip` only checks that `ZipFile.OpenRead` succeeds and there's at least one entry. It does NOT:
- Verify CRC32 (handled by ZipFile internally during extract, but not pre-checked)
- Compare against GitHub's `asset.digest` (SHA256)
- Check total file size matches expected

**Impact**: Medium. Corrupted downloads are detected on extract (ZipFile throws), which triggers rollback. But the download step already wrote a bad file, and the user waited for it.

---

## 8. Uninstaller State Machine

### 8.1 Flow

```
Uninstall(component, createBackup=true)
  │
  ├─ component.IsInstalled? — no → "not installed" warning, return true
  │
  ├─ Confirm: "Are you sure?" [Y/N]
  │
  ├─ IsCore? → extra warning: "will remove all mods!"
  │
  ├─ CreateBackup (if createBackup):
  │    ├─ Copy install dir + GameFiles → {backupDir}/{id}_uninstall_{timestamp}/
  │    ├─ Fail → "Continue without backup?" [Y/N]
  │    └─ No → cancel
  │
  ├─ Delete installPath directory (recursive)
  ├─ Delete each GameFile individually
  ├─ ClearAll caches
  │
  └─ Return true (even with partial failures — just warns)
```

### 8.2 Known Issues

#### U1: Always returns true even on partial failure

```csharp
if (errors.Count == 0) { /* success */ return true; }
else { /* partial */ return true; }  // ← still true!
```

The caller (`ExecuteOperationAsync`) calls `RefreshComponentStateAsync` regardless. If uninstall was partial, the component will be re-scanned and likely show as INCOMPLETE. The user sees "✅ Uninstalled" but then the status shows "⚠ Incomplete."

**Impact**: Medium. Same success-then-warning pattern as installer.

#### U2: Uninstall of partial component does not clean up orphaned files

When a component is `IsPartial`, uninstall only deletes what's in the ComponentDefinition's `InstallPath` and `GameFiles` list. If the partial install created files outside these lists (e.g., nested subdirectories not explicitly listed), they remain as orphans.

**Impact**: Low. Current component definitions list all files.

---

## 9. Data Flow Summary

```
┌──────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   GitHub      │     │   APPDATA        │     │   Game Dir       │
│   (remote)    │     │   (local config) │     │   (local files)  │
└──────┬───────┘     └────────┬─────────┘     └────────┬────────┘
       │                      │                        │
       │  Atom feed (ver)     │                        │
       ├──────────────────────┤                        │
       │  REST API (assets)   │                        │
       ├──────────────────────┼────────────────────────┤
       │                      │                        │
       │                      │  launcher_config.json  │
       │                      │  settings.json (lang)  │
       │                      │                        │
       │                      │                        │
       ▼                      ▼                        ▼
┌────────────────────────────────────────────────────────────┐
│                    Runtime Memory                           │
│                                                             │
│  LauncherConfig _config         ← config.LauncherVersion    │
│  List<InstalledComponent>      ← _components               │
│  SelfUpdater                   ← _latestVersion, _downloadUrl│
│  string? _gameDir              ← detected/entered path     │
│  bool _hasGdWeave              ← legacy detection           │
└────────────────────────────────────────────────────────────┘
```

### Version Number Propagation

```
csproj <Version>1.0.3</Version>
  │
  └─ AssemblyInformationalVersionAttribute("1.0.3")
       │
       ├─ GetAssemblyVersion() → "1.0.3"
       │    │
       │    ├─ CreateDefaultConfig().LauncherVersion
       │    └─ Main() version sync → config.LauncherVersion
       │
       └─ ConsoleUI.SetVersion(config.LauncherVersion)
            └─ Banner display: "SlotWeave Launcher v1.0.3"
```

---

## 10. Summary of All Identified Issues

### Critical (data loss / security)

| # | Issue | Location |
|---|-------|----------|
| S1 | Self-update .new exe has zero integrity verification before swap | `SelfUpdater.UpdateAsync` |
| I1 | `VerifyCriticalFiles` warns but doesn't prevent success return | `Installer.InstallOrUpdateAsync` line 134-135 |

### High (user-visible incorrect behavior)

| # | Issue | Location |
|---|-------|----------|
| I2 | Embedded resource name mismatch possible after assembly rename | `Installer.WriteEmbeddedWinmmDll` line 422 |
| B5 | AV-deleted files not reflected in component state until next scan | `Installer.InstallOrUpdateAsync` → `ComponentScanner` |
| U1 | Uninstall returns success even on partial failure | `Uninstaller.Uninstall` line 171 |

### Medium (confusing UX / edge cases)

| # | Issue | Location |
|---|-------|----------|
| S2 | Config version written before swap confirmed | `SelfUpdater.UpdateAsync` line 119 |
| S3 | Cryptic error message when download URL resolution fails | `SelfUpdater.ResolveDownloadUrlAsync` |
| B2 | Update failure doesn't refresh state (but rollback restores files) | `MenuController.ExecuteOperationAsync` line 382-384 |
| G1 | Atom feed failure silently suppresses update notifications | `GitHubService.GetLatestVersionAsync` |
| G3 | FindMatchingAsset can return .exe when .zip is expected | `GitHubService.FindMatchingAsset` line 75 |
| I4 | No pre-extract download integrity check for component zips | `Installer.InstallOrUpdateAsync` step 2-3 |

### Low (performance / edge cases)

| # | Issue | Location |
|---|-------|----------|
| B3 | RefreshComponentStateAsync rescans all N components for 1 update | `MenuController.RefreshComponentStateAsync` |
| B4 | LatestVersion can be stale between refresh cycles | `MenuController._components` |
| S4 | IsNewer fragile for non-semver tags | `SelfUpdater.IsNewer` |
| I3 | Backup ordering by timestamp not version | `Installer.PruneBackups` |
| G2 | Prerelease detection is regex-based, misses non-standard formats | `GitHubService.LooksLikePrerelease` |
| U2 | Partial uninstall may leave orphan files | `Uninstaller.Uninstall` |

---

## 11. Recommendations (non-exhaustive)

1. **Add PE header verification** to `SelfUpdater.UpdateAsync` before swap — check `MZ` magic at minimum; ideally compare SHA256 against GitHub asset digest.

2. **Make VerifyCriticalFiles blocking** — if critical files are missing after extraction, the install should return failure and trigger rollback, not report success.

3. **Fix embedded resource name** to use `nameof` or a constant that's validated at build time, or check against all embedded resources.

4. **Add `_lastCheckFailed` flag** to `SelfUpdater` and `MenuController` so the UI can show "⚠ Could not check for updates" instead of silence when Atom feed fails.

5. **Refactor `RefreshComponentStateAsync`** to scan a single component instead of all.

6. **Add dependency ordering** — if the launcher itself has an update, and components also have updates, consider prompting launcher update first.

7. **Consider a `ComponentState` enum** to replace the boolean flags (`IsInstalled`, `IsPartial`) with a proper state machine: `NotInstalled → Partial → Installed → UpdateAvailable`. This would make impossible states unrepresentable.
