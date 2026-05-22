using System.Text.Json;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Tests;

public class ManifestTests
{
    [Fact]
    public void ReleaseManifest_RoundTripsKnownPayload()
    {
        var json = """
                   {
                     "release_tag": "mods-2026-05-21",
                     "published_at": "2026-05-21T23:04:35Z",
                     "mods": [
                       {
                         "mod_id": "ct",
                         "friendly_name": "Chaos Wastes Tweaker",
                         "workshop_id": "3712929235",
                         "version": "0.7.80-alpha",
                         "asset_filename": "ct.zip",
                         "visibility": "public"
                       },
                       {
                         "mod_id": "wt",
                         "friendly_name": "Weapon Tweaker",
                         "workshop_id": "3712896117",
                         "version": "0.12.63-dev",
                         "asset_filename": "wt.zip",
                         "visibility": "friends_only"
                       }
                     ]
                   }
                   """;
        var m = JsonSerializer.Deserialize<ReleaseManifest>(json);
        Assert.NotNull(m);
        Assert.Equal("mods-2026-05-21", m!.ReleaseTag);
        Assert.Equal(2, m.Mods.Count);
        Assert.Equal("ct", m.Mods[0].ModId);
        Assert.Equal("Chaos Wastes Tweaker", m.Mods[0].FriendlyName);
        Assert.Equal("3712929235", m.Mods[0].WorkshopId);
        Assert.Equal("0.7.80-alpha", m.Mods[0].Version);
        Assert.Equal("ct.zip", m.Mods[0].AssetFilename);
        Assert.Equal("public", m.Mods[0].Visibility);
        Assert.Equal("friends_only", m.Mods[1].Visibility);
    }

    [Fact]
    public void ReleaseManifest_MissingFields_DeserializeAsEmpty()
    {
        var json = """{ "release_tag": "x", "mods": [ {} ] }""";
        var m = JsonSerializer.Deserialize<ReleaseManifest>(json);
        Assert.NotNull(m);
        Assert.Single(m!.Mods);
        Assert.Equal("", m.Mods[0].ModId);
        Assert.Equal("", m.Mods[0].WorkshopId);
    }

    [Fact]
    public void GitHubRelease_DeserializesAssets()
    {
        var json = """
                   {
                     "tag_name": "v0.3.0",
                     "published_at": "2026-05-21T00:00:00Z",
                     "assets": [
                       { "name": "manifest.json", "browser_download_url": "https://x/manifest.json", "size": 1234 },
                       { "name": "ct.zip",        "browser_download_url": "https://x/ct.zip",        "size": 5000 }
                     ]
                   }
                   """;
        var r = JsonSerializer.Deserialize<GitHubRelease>(json);
        Assert.NotNull(r);
        Assert.Equal("v0.3.0", r!.TagName);
        Assert.Equal(2, r.Assets.Count);
        Assert.Equal("manifest.json", r.Assets[0].Name);
        Assert.Equal(1234, r.Assets[0].Size);
    }
}
