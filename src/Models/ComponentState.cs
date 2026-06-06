namespace SlotWeave.Launcher.Models;

/// <summary>
/// Explicit component state machine.
/// Replaces the boolean flag combination (IsInstalled, IsPartial, HasUpdate)
/// with a single enum. Invalid states (e.g. IsInstalled=false && HasUpdate=true)
/// become compile-time impossible.
/// </summary>
public enum ComponentState
{
    NotInstalled,
    Installing,         // operation in progress
    Installed,
    UpdateAvailable,    // LatestVersion > InstalledVersion
    Updating,           // update in progress
    PartialInstall,     // directory exists but files missing
    Corrupted,          // files present but content hash mismatch
    Uninstalling        // uninstall in progress
}
