using System.Text.Json.Serialization;

namespace VT2ModUpdater.Models;

public sealed class ReleaseManifest
{
    [JsonPropertyName("release_tag")]
    public string ReleaseTag { get; set; } = "";

    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = "";

    [JsonPropertyName("mods")]
    public List<ManifestEntry> Mods { get; set; } = new();
}

public sealed class ManifestEntry
{
    [JsonPropertyName("mod_id")]
    public string ModId { get; set; } = "";

    [JsonPropertyName("friendly_name")]
    public string FriendlyName { get; set; } = "";

    [JsonPropertyName("workshop_id")]
    public string WorkshopId { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("asset_filename")]
    public string AssetFilename { get; set; } = "";

    /// <summary>
    /// Lowercase-hex SHA-256 of the bundle zip. Null or empty when consuming an older
    /// manifest that pre-dates integrity verification — in that case the deployer skips
    /// the integrity check with a debug log and proceeds. Producer side:
    /// <c>vermintide-2-tweaker/tools/publish-release/publish-release.ps1</c> computes
    /// this via <c>Get-FileHash -Algorithm SHA256</c> right after <c>Compress-Archive</c>.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>
    /// Exact producer commit for this manifest row. Older manifests may omit it;
    /// the explicit recovery surface then requires the user to supply a commit.
    /// This value is only a convenient input default, never recovery authority:
    /// the recovery resolver still proves the complete historical record.
    /// </summary>
    [JsonPropertyName("source_commit")]
    public string? SourceCommit { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "";
}
