using System.Text.Json.Serialization;

namespace VT2ModUpdater.Models;

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
