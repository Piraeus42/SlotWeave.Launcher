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
