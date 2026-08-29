using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

internal interface IReleaseClient : IDisposable
{
    Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken ct = default);
    Task<ReleaseManifest> DownloadManifestAsync(
        GitHubRelease release,
        CancellationToken ct = default);
    Task<byte[]> DownloadAssetAsync(
        GitHubRelease release,
        string assetFilename,
        CancellationToken ct = default);
}

public sealed class GitHubReleaseClient : IReleaseClient
{
    private const string Owner = "Ensrick";
    private const string Repo = "vermintide-2-tweaker";

    private readonly HttpClient _http;

    public GitHubReleaseClient()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VT2ModUpdater", "0.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json)
            ?? throw new InvalidOperationException("Failed to deserialize release payload");
        return release;
    }

    public async Task<ReleaseManifest> DownloadManifestAsync(GitHubRelease release, CancellationToken ct = default)
    {
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Release has no manifest.json asset");
        var json = await _http.GetStringAsync(asset.BrowserDownloadUrl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ReleaseManifest>(json)
            ?? throw new InvalidOperationException("Failed to deserialize manifest.json");
    }

    public async Task<byte[]> DownloadAssetAsync(GitHubRelease release, string assetFilename, CancellationToken ct = default)
    {
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, assetFilename, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release has no asset named {assetFilename}");
        return await _http.GetByteArrayAsync(asset.BrowserDownloadUrl, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
