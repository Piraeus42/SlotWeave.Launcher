using System.Text.Json.Serialization;

namespace SlotWeave.Launcher.Models;

/// <summary>
/// Represents a configured repository/component that can be managed.
/// </summary>
public class ComponentDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("installPath")]
    public string InstallPath { get; set; } = string.Empty;

    [JsonPropertyName("isCore")]
    public bool IsCore { get; set; }

    [JsonPropertyName("manifestFile")]
    public string ManifestFile { get; set; } = string.Empty;

    [JsonPropertyName("assetPattern")]
    public string AssetPattern { get; set; } = string.Empty;

    [JsonPropertyName("gameFiles")]
    public List<string> GameFiles { get; set; } = new();

    [JsonPropertyName("dependsOn")]
    public string? DependsOn { get; set; }
}

/// <summary>
/// Represents the installed state of a component.
/// </summary>
public class InstalledComponent
{
    public ComponentDefinition Definition { get; set; } = null!;
    public string? InstalledVersion { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsPartial { get; set; }
    public string? LatestVersion { get; set; }

    public bool HasUpdate => IsInstalled
        && InstalledVersion != null
        && LatestVersion != null
        && InstalledVersion != LatestVersion;

    /// <summary>
    /// Needs repair: incomplete install (some files missing) or update available.
    /// </summary>
    public bool NeedsRepair => IsPartial;
    public bool NeedsAction => HasUpdate || NeedsRepair;

    public string? DownloadUrl { get; set; }
    public long DownloadSize { get; set; }

    public string StatusIcon => IsPartial ? "⚠" : !IsInstalled ? "○" : HasUpdate ? "⚠" : "✓";

    public string VersionDisplay
    {
        get
        {
            if (IsPartial)
                return Services.Loc.T("component.incomplete");

            if (!IsInstalled)
                return Services.Loc.T("component.not_installed");

            if (InstalledVersion == null)
                return Services.Loc.T("component.version_unknown");

            if (HasUpdate)
                return $"{InstalledVersion} → {LatestVersion}";

            return $"{InstalledVersion}{Services.Loc.T("component.latest_suffix")}";
        }
    }
}
