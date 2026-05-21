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

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "";
}
