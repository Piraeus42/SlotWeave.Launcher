using System.Text.Json.Serialization;

namespace SlotWeave.Launcher.Models;

/// <summary>
/// Root configuration model loaded from launcher_config.json.
/// </summary>
public class LauncherConfig
{
    [JsonPropertyName("repositories")]
    public List<ComponentDefinition> Repositories { get; set; } = new();

    [JsonPropertyName("gameExecutable")]
    public string GameExecutable { get; set; } = "Luck be a Landlord.exe";

    [JsonPropertyName("gameDirectoryPaths")]
    public List<string> GameDirectoryPaths { get; set; } = new();

    [JsonPropertyName("steamAppId")]
    public int? SteamAppId { get; set; }

    [JsonPropertyName("cacheDirectories")]
    public List<string> CacheDirectories { get; set; } = new();

    [JsonPropertyName("backupDirectory")]
    public string BackupDirectory { get; set; } = "SlotWeave/.launcher_backups";

    [JsonPropertyName("tempDirectory")]
    public string TempDirectory { get; set; } = "SlotWeave/.launcher_temp";

    [JsonPropertyName("maxBackups")]
    public int MaxBackups { get; set; } = 3;

    [JsonPropertyName("downloadTimeoutSeconds")]
    public int DownloadTimeoutSeconds { get; set; } = 300;

    [JsonPropertyName("downloadMirrors")]
    public List<string> DownloadMirrors { get; set; } = new();

    [JsonPropertyName("launcherVersion")]
    public string LauncherVersion { get; set; } = "1.0.2";

    [JsonPropertyName("launcherRepo")]
    public LauncherRepoInfo? LauncherRepo { get; set; }
}

/// <summary>
/// GitHub repo info for the launcher's self-update.
/// </summary>
public class LauncherRepoInfo
{
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;

    [JsonPropertyName("assetPattern")]
    public string AssetPattern { get; set; } = string.Empty;
}
