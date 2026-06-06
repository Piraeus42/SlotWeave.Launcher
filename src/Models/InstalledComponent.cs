using SlotWeave.Launcher.Services;

namespace SlotWeave.Launcher.Models;

/// <summary>
/// Installed component with explicit state machine.
///
/// State is the single source of truth. The legacy boolean properties
/// (IsInstalled, IsPartial, HasUpdate) are derived from State for
/// backward compatibility with existing callers. New code should use
/// State directly (e.g. State == ComponentState.UpdateAvailable).
///
/// State can change via two paths:
///   TransitionTo(newState)  — validated state machine (operations)
///   SetStateFromScan(state) — bypass validation (file-system scan)
/// </summary>
public class InstalledComponent
{
    private ComponentState _state;

    // ── Core properties ────────────────────────────────────────

    public ComponentState State
    {
        get => _state;
        private set => _state = value;
    }

    public ComponentDefinition Definition { get; }

    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; internal set; }

    /// <summary>
    /// True when the last version check failed (network error, etc.).
    /// UI should show "⚠ Could not check for updates" instead of silent.
    /// </summary>
    public bool LatestVersionCheckFailed { get; internal set; }

    // ── Download info (populated during update flow) ────────────

    public string? DownloadUrl { get; set; }
    public long DownloadSize { get; set; }

    // ── Constructor ─────────────────────────────────────────────

    public InstalledComponent(ComponentDefinition definition)
    {
        Definition = definition;
        _state = ComponentState.NotInstalled;
    }

    // ── State mutation ──────────────────────────────────────────

    /// <summary>
    /// Validated state transition — for use by operation flows
    /// (install, update, uninstall). Returns failure if the
    /// transition is not allowed from the current state.
    /// </summary>
    public Result TransitionTo(ComponentState newState)
    {
        if (!IsValidTransition(_state, newState))
            return Result.Failure(
                $"Invalid state transition: {Definition.DisplayName} [{_state} → {newState}]");

        _state = newState;
        return Result.Success();
    }

    /// <summary>
    /// Direct state set — bypasses transition validation.
    /// ONLY for use by ComponentScanner, which infers state from
    /// file-system contents, not from an operation lifecycle.
    /// </summary>
    internal void SetStateFromScan(ComponentState scannedState)
    {
        _state = scannedState;
    }

    /// <summary>
    /// Set the installed version (e.g. from version.json or DLL metadata).
    /// </summary>
    public void UpdateInstalledVersion(string? version)
    {
        InstalledVersion = version;
    }

    private static bool IsValidTransition(ComponentState from, ComponentState to)
    {
        return (from, to) switch
        {
            // Install flow
            (ComponentState.NotInstalled, ComponentState.Installing) => true,
            (ComponentState.Installing, ComponentState.Installed) => true,
            (ComponentState.Installing, ComponentState.PartialInstall) => true,
            (ComponentState.Installing, ComponentState.NotInstalled) => true,  // rollback

            // Update flow
            (ComponentState.Installed, ComponentState.Updating) => true,
            (ComponentState.UpdateAvailable, ComponentState.Updating) => true,
            (ComponentState.Updating, ComponentState.Installed) => true,
            (ComponentState.Updating, ComponentState.Corrupted) => true,
            (ComponentState.Updating, ComponentState.UpdateAvailable) => true,  // rollback

            // Uninstall flow
            (ComponentState.Installed, ComponentState.Uninstalling) => true,
            (ComponentState.UpdateAvailable, ComponentState.Uninstalling) => true,
            (ComponentState.PartialInstall, ComponentState.Uninstalling) => true,
            (ComponentState.Corrupted, ComponentState.Uninstalling) => true,
            (ComponentState.Uninstalling, ComponentState.NotInstalled) => true,

            // Repair flow
            (ComponentState.PartialInstall, ComponentState.Installing) => true,
            (ComponentState.Corrupted, ComponentState.Installing) => true,

            // Version-check transitions
            (ComponentState.Installed, ComponentState.UpdateAvailable) => true,
            (ComponentState.UpdateAvailable, ComponentState.Installed) => true,

            // Scanning bypasses validation (SetStateFromScan), but these
            // are documented for completeness:
            // (any, NotInstalled)       → via SetStateFromScan
            // (any, PartialInstall)     → via SetStateFromScan
            // (any, Corrupted)          → via SetStateFromScan

            _ => false
        };
    }

    // ── Backward-compat boolean properties ──────────────────────
    // Derived from State. Existing callers (MenuController, Installer,
    // Uninstaller) use these until they are migrated to State directly.

    /// <summary>
    /// [deprecated] Use State instead.
    /// True when the component is fully installed (may or may not have update).
    /// </summary>
    public bool IsInstalled =>
        _state is ComponentState.Installed
                  or ComponentState.UpdateAvailable
                  or ComponentState.Updating
                  or ComponentState.Corrupted;

    /// <summary>
    /// [deprecated] Use State instead.
    /// True when the install is incomplete (missing files) or corrupted.
    /// </summary>
    public bool IsPartial =>
        _state is ComponentState.PartialInstall
                  or ComponentState.Corrupted;

    /// <summary>
    /// [deprecated] Use State == UpdateAvailable instead.
    /// True when a newer version is available on GitHub.
    /// </summary>
    public bool HasUpdate =>
        _state == ComponentState.UpdateAvailable;

    /// <summary>
    /// [deprecated] Use State == PartialInstall or State == Corrupted instead.
    /// </summary>
    public bool NeedsRepair => IsPartial;

    /// <summary>
    /// [deprecated] Use NeedsAction based on State instead.
    /// </summary>
    public bool NeedsAction => NeedsActionBasedOnState;

    // ── Computed display properties ─────────────────────────────

    public bool NeedsActionBasedOnState => _state is
        ComponentState.UpdateAvailable or
        ComponentState.PartialInstall or
        ComponentState.Corrupted;

    public string StatusIcon => _state switch
    {
        ComponentState.NotInstalled => "○",
        ComponentState.Installed => "✓",
        ComponentState.UpdateAvailable => "⚠",
        ComponentState.PartialInstall => "⚠",
        ComponentState.Corrupted => "✗",
        ComponentState.Installing => "⋯",
        ComponentState.Updating => "⋯",
        ComponentState.Uninstalling => "⋯",
        _ => "?"
    };

    public string StateDescription => _state switch
    {
        ComponentState.NotInstalled => "Not installed",
        ComponentState.Installing => "Installing...",
        ComponentState.Installed => "Installed",
        ComponentState.UpdateAvailable => "Update available",
        ComponentState.Updating => "Updating...",
        ComponentState.PartialInstall => "Incomplete",
        ComponentState.Corrupted => "Corrupted",
        ComponentState.Uninstalling => "Uninstalling...",
        _ => "Unknown"
    };

    public string VersionDisplay
    {
        get
        {
            if (IsPartial)
                return Loc.T("component.incomplete");

            if (!IsInstalled)
                return Loc.T("component.not_installed");

            if (InstalledVersion == null)
                return Loc.T("component.version_unknown");

            if (HasUpdate)
                return $"{InstalledVersion} → {LatestVersion}";

            return $"{InstalledVersion}{Loc.T("component.latest_suffix")}";
        }
    }
}
