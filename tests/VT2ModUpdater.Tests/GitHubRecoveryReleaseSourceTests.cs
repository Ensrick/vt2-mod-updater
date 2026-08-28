using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public class GitHubRecoveryReleaseSourceTests
{
    private const string Repository = RecoveryRecordContract.Repository;

    [Fact]
    public async Task PageRequestUsesGeneratedCoordinateAndRejectsHostileLinkWithoutFollowing()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            var response = JsonResponse("""
                [
                  {
                    "id": 123,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00Z",
                    "draft": false,
                    "prerelease": false,
                    "assets": []
                  }
                ]
                """);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://evil.example/releases?page=2>; rel=\"next\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Single(handler.Calls);
        Assert.Equal(
            "https://api.github.com/repos/Ensrick/vermintide-2-tweaker/" +
            "releases?per_page=100&page=1",
            handler.Calls[0].RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(
        "<https://api.github.com/repos/Other/repository/releases?per_page=100&page=2>; " +
        "rel=\"next\"")]
    [InlineData(
        "<https://api.github.com/repositories/111/releases?per_page=100&page=2>; " +
        "rel=\"next\", <https://api.github.com/repositories/222/releases?per_page=100&page=3>; " +
        "rel=\"last\"")]
    [InlineData(
        "<https://api.github.com/repositories/111/releases?per_page=100&page=02>; " +
        "rel=\"next\"")]
    [InlineData(
        "<https://api.github.com/repositories/111/releases?per_page=100&page=2>; " +
        "rel=\"next\", <https://api.github.com/repositories/111/releases?per_page=100&page=2>; " +
        "rel=\"next\"")]
    public async Task HostileOrAmbiguousLinkMetadataIsRejected(string link)
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(ReleasePageJson(1));
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Headers.TryAddWithoutValidation("Link", link);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task PageParserRetainsExactNumericAssetAndBrowserCoordinate()
    {
        var browserUrl = AssetUrl("mods-2026-08-26", "manifest.json");
        var digest = new string('a', 64);
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse($$"""
                [
                  {
                    "id": 123,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00Z",
                    "draft": false,
                    "prerelease": false,
                    "assets": [
                      {
                        "id": 456,
                        "name": "manifest.json",
                        "size": 789,
                        "browser_download_url": "{{browserUrl}}",
                        "digest": "sha256:{{digest}}"
                      }
                    ]
                  }
                ]
                """);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var page = await source.GetReleasePageAsync(
            Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default);

        var asset = Assert.Single(Assert.Single(page.Releases).Assets);
        Assert.Equal(456, asset.Id);
        Assert.Equal(789, asset.Size);
        Assert.Equal(browserUrl, asset.BrowserDownloadUrl);
        Assert.Equal(digest, asset.DigestSha256);
    }

    [Fact]
    public async Task FullPageUsesValidatedNumericLinkButGeneratesItsOwnNextCoordinate()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(ReleasePageJson(100));
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://api.github.com/repositories/123456/releases?per_page=100&page=2>; " +
                "rel=\"next\", " +
                "<https://api.github.com/repositories/123456/releases?per_page=100&page=4>; " +
                "rel=\"last\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var page = await source.GetReleasePageAsync(
            Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default);

        Assert.True(page.HasNextPage);
        Assert.Equal(100, page.Releases.Count);
        Assert.Single(handler.Calls);
        Assert.EndsWith("per_page=100&page=1", handler.Calls[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task FullPageWithoutNextRelationIsRejectedAsFalselyTerminal()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(ReleasePageJson(100));
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("full", exception.Message);
    }

    [Fact]
    public async Task ShortPageWithNextRelationIsRejected()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(ReleasePageJson(1));
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://api.github.com/repositories/123456/releases?per_page=100&page=2>; " +
                "rel=\"next\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("short", exception.Message);
    }

    [Theory]
    [InlineData(
        "<https://api.github.com/repositories/123456/releases?per_page=100&page=1>; " +
        "rel=\"last\"")]
    [InlineData(
        "<https://api.github.com/repositories/123456/releases?per_page=100&page=3>; " +
        "rel=\"last\"")]
    public async Task TerminalPageRejectsPresentLastRelationOtherThanCurrent(string link)
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(ReleasePageJson(1));
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Headers.TryAddWithoutValidation("Link", link);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 2, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("last", exception.Message);
    }

    [Fact]
    public async Task TerminalPageAcceptsPresentLastRelationEqualToCurrent()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(ReleasePageJson(1));
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://api.github.com/repositories/123456/releases?per_page=100&page=2>; " +
                "rel=\"last\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var page = await source.GetReleasePageAsync(
            Repository, 2, 100, RecoveryHistoryResolver.MaximumTotalAssets, default);

        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task RemainingAssetBudgetWinsBeforeMalformedFirstAssetIsInspected()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse("""
                [
                  {
                    "id": 123,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00Z",
                    "draft": false,
                    "prerelease": false,
                    "assets": [ { "id": "malformed" }, {} ]
                  }
                ]
                """);
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(Repository, 1, 100, 1, default));

        Assert.Equal(RecoveryReleaseSourceFailure.AssetBoundExceeded, exception.Failure);
    }

    [Fact]
    public async Task FirstExcessAssetIsRejectedBeforeItsMalformedFieldsAreInspected()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse("""
                [
                  {
                    "id": 123,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00Z",
                    "draft": false,
                    "prerelease": false,
                    "assets": [ {}, { "id": "first-excess-malformed" } ]
                  }
                ]
                """);
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(Repository, 1, 100, 1, default));

        Assert.Equal(RecoveryReleaseSourceFailure.AssetBoundExceeded, exception.Failure);
    }

    [Fact]
    public async Task UppercaseOrMissingGitHubAssetDigestIsRejected()
    {
        var browserUrl = AssetUrl("mods-2026-08-26", "manifest.json");
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse($$"""
                [
                  {
                    "id": 123,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00Z",
                    "draft": false,
                    "prerelease": false,
                    "assets": [
                      {
                        "id": 456,
                        "name": "manifest.json",
                        "size": 789,
                        "browser_download_url": "{{browserUrl}}",
                        "digest": "sha256:{{new string('A', 64)}}"
                      }
                    ]
                  }
                ]
                """);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("lowercase sha256", exception.Message);
    }

    [Fact]
    public async Task NoncanonicalGitHubPublishedTimestampIsRejected()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse("""
                [
                  {
                    "id": 123,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00+00:00",
                    "draft": false,
                    "prerelease": false,
                    "assets": []
                  }
                ]
                """);
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("UTC ISO-8601", exception.Message);
    }

    [Fact]
    public async Task RevalidationUsesExactPageAndIfNoneMatch()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"stable\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await source.RevalidateReleasePageAsync(
            Repository,
            3,
            100,
            "\"stable\"",
            default);

        Assert.Equal(RecoveryPageRevalidation.Unchanged, result);
        Assert.Single(handler.Calls);
        Assert.Equal("\"stable\"", handler.Calls[0].Headers.GetValues("If-None-Match").Single());
        Assert.EndsWith("per_page=100&page=3", handler.Calls[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Conflicting304EntityTagIsReportedAsChanged()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"different\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await source.RevalidateReleasePageAsync(
            Repository, 1, 100, "\"stable\"", default);

        Assert.Equal(RecoveryPageRevalidation.Changed, result);
    }

    [Fact]
    public async Task Missing304EntityTagIsTypedAsContractFailure()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.RevalidateReleasePageAsync(
                Repository, 1, 100, "\"stable\"", default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("304", exception.Message);
    }

    [Fact]
    public async Task ConflictingHttp304FlowsThroughResolverAsHistoryChanged()
    {
        var calls = 0;
        var handler = new ScriptedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var page = JsonResponse(ReleasePageJson(1));
                page.Headers.ETag =
                    new System.Net.Http.Headers.EntityTagHeaderValue("\"stable\"");
                return Task.FromResult(page);
            }
            var changed = new HttpResponseMessage(HttpStatusCode.NotModified);
            changed.Headers.ETag =
                new System.Net.Http.Headers.EntityTagHeaderValue("\"different\"");
            return Task.FromResult(changed);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(
            new RecoveryHistoryQuery(
                Repository,
                "mx",
                "1234567890",
                new string('a', 40)));

        Assert.Equal(RecoveryResolutionStatus.RemoteFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.HistoryChangedDuringScan, result.Failure);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(
        "producer-tracked-manifest.json",
        "tracked",
        "c367667af8ddf00c08d8b78f2fb5f8b791dc6b7897109f06316835d41a527dc6")]
    [InlineData(
        "producer-receipt-manifest.json",
        "receipt",
        "812f656096f178fecfcb59e2a74b37811b046ab187516b0df8b65cc1e43981ec")]
    public async Task ProducerManifestThroughHttpResolverSelectsWithoutRequestingZip(
        string fixture,
        string expectedAuthority,
        string expectedFixtureSha256)
    {
        const string pageEntityTag = "\"stable\"";
        var manifest = ProducerManifestBytes(fixture);
        var manifestSha256 = Sha256(manifest);
        Assert.Equal(expectedFixtureSha256, manifestSha256);
        using var manifestDocument = JsonDocument.Parse(manifest);
        var manifestRoot = manifestDocument.RootElement;
        var releaseTag = manifestRoot.GetProperty("release_tag").GetString()!;
        var row = manifestRoot.GetProperty("mods")[0];
        var recovery = row.GetProperty("recovery");
        var asset = recovery.GetProperty("asset");
        var assetFilename = asset.GetProperty("filename").GetString()!;
        var calls = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.Host == "api.github.com")
            {
                if (request.Headers.Contains("If-None-Match"))
                {
                    var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified);
                    unchanged.Headers.ETag =
                        new System.Net.Http.Headers.EntityTagHeaderValue(pageEntityTag);
                    return Task.FromResult(unchanged);
                }

                var manifestUrl = AssetUrl(releaseTag, "manifest.json");
                var zipUrl = AssetUrl(releaseTag, assetFilename);
                var page = JsonResponse($$"""
                    [
                      {
                        "id": 123,
                        "tag_name": "{{releaseTag}}",
                        "published_at": "2026-08-26T12:00:00Z",
                        "draft": false,
                        "prerelease": false,
                        "assets": [
                          {
                            "id": 456,
                            "name": "manifest.json",
                            "size": {{manifest.Length}},
                            "browser_download_url": "{{manifestUrl}}",
                            "digest": "sha256:{{manifestSha256}}"
                          },
                          {
                            "id": 457,
                            "name": "{{assetFilename}}",
                            "size": {{asset.GetProperty("length").GetInt64()}},
                            "browser_download_url": "{{zipUrl}}",
                            "digest": "sha256:{{asset.GetProperty("sha256").GetString()}}"
                          }
                        ]
                      }
                    ]
                    """);
                page.Headers.ETag =
                    new System.Net.Http.Headers.EntityTagHeaderValue(pageEntityTag);
                return Task.FromResult(page);
            }

            if (request.RequestUri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/object?token=fixture");
                return Task.FromResult(redirect);
            }

            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(manifest)
            });
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(
            new RecoveryHistoryQuery(
                Repository,
                recovery.GetProperty("mod_id").GetString()!,
                recovery.GetProperty("workshop_id").GetString()!,
                recovery.GetProperty("source").GetProperty("commit").GetString()!));

        Assert.Equal(RecoveryResolutionStatus.SourceExactSurvivingArtifact, result.Status);
        Assert.Equal("mx.zip", Assert.IsType<SourceExactRecoveryArtifact>(result.Artifact)
            .AssetFilename);
        Assert.Equal(expectedAuthority, result.Artifact!.Proof.Record.BundleAuthority);
        Assert.Equal(4, calls);
        Assert.DoesNotContain(
            handler.Calls,
            request => request.RequestUri!.AbsolutePath.EndsWith(
                ".zip",
                StringComparison.Ordinal));
        Assert.Equal(
            pageEntityTag,
            handler.Calls[^1].Headers.GetValues("If-None-Match").Single());
    }

    [Fact]
    public async Task OversizedContentLengthIsRefusedBeforeStreamRead()
    {
        var content = new ThrowIfReadContent();
        content.Headers.ContentLength = GitHubRecoveryReleaseSource.MaximumReleasePageBytes + 1L;
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.HistoryBoundExceeded, exception.Failure);
        Assert.False(content.WasRead);
    }

    [Fact]
    public async Task DuplicateGitHubPropertyIsRejectedWithoutLastValueWins()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse("""
                [
                  {
                    "id": 123,
                    "id": 124,
                    "tag_name": "mods-2026-08-26",
                    "published_at": "2026-08-26T12:00:00Z",
                    "draft": false,
                    "prerelease": false,
                    "assets": []
                  }
                ]
                """);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetReleasePageAsync(
                Repository, 1, 100, RecoveryHistoryResolver.MaximumTotalAssets, default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("duplicate property 'id'", exception.Message);
    }

    [Fact]
    public async Task HostileManifestRedirectIsRejectedWithoutSecondRequest()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/manifest.json");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                AssetUrl("mods-test", "manifest.json"),
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ForeignInitialBrowserDownloadUrlIsRejectedBeforeHttp()
    {
        var handler = new ScriptedHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not run"));
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                "https://evil.example/manifest.json",
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" mods-test")]
    [InlineData("mods/test")]
    [InlineData("møds-test")]
    public async Task NoncanonicalReleaseTagIsTypedBeforeUriConstructionOrHttp(
        string? releaseTag)
    {
        var handler = new ScriptedHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not run"));
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                releaseTag!,
                20,
                "manifest.json",
                "https://github.com/Ensrick/vermintide-2-tweaker/releases/download/" +
                "mods-test/manifest.json",
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("release tag", exception.Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task OversizedReleaseTagIsTypedBeforeUriConstructionOrHttp()
    {
        var handler = new ScriptedHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not run"));
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                new string('a', 129),
                20,
                "manifest.json",
                "https://github.com/Ensrick/vermintide-2-tweaker/releases/download/" +
                "mods-test/manifest.json",
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task AllowedReleaseAssetRedirectReturnsOnlyBoundedManifestBytes()
    {
        var calls = 0;
        var expected = Encoding.UTF8.GetBytes("{\"manifest_schema\":2}");
        var handler = new ScriptedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/object?token=x");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
            });
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await source.GetManifestAsync(
            Repository,
            10,
            "mods-test",
            20,
            "manifest.json",
            AssetUrl("mods-test", "manifest.json"),
            1024,
            default);

        Assert.Equal(RecoveryManifestFetchStatus.Found, result.Status);
        Assert.Equal(expected, result.Bytes.ToArray());
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("github.com", handler.Calls[0].RequestUri!.Host);
        Assert.Equal("release-assets.githubusercontent.com", handler.Calls[1].RequestUri!.Host);
    }

    [Fact]
    public async Task ManifestCdnCannotRedirectBackToGitHubOrIssueAThirdRequest()
    {
        var calls = 0;
        var handler = new ScriptedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/object?token=x");
                return Task.FromResult(redirect);
            }
            if (calls == 2)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(AssetUrl("mods-test", "manifest.json"));
                return Task.FromResult(redirect);
            }
            throw new InvalidOperationException("a rejected CDN return must not issue a third request");
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                AssetUrl("mods-test", "manifest.json"),
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("CDN", exception.Message);
        Assert.Equal(2, calls);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task ManifestMayTraverseOnlyApprovedCdnHostsAfterLeavingGitHub()
    {
        var calls = 0;
        var expected = Encoding.UTF8.GetBytes("{\"manifest_schema\":2}");
        var handler = new ScriptedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/object?token=x");
                return Task.FromResult(redirect);
            }
            if (calls == 2)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://objects.githubusercontent.com/object?token=y");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
            });
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await source.GetManifestAsync(
            Repository,
            10,
            "mods-test",
            20,
            "manifest.json",
            AssetUrl("mods-test", "manifest.json"),
            1024,
            default);

        Assert.Equal(RecoveryManifestFetchStatus.Found, result.Status);
        Assert.Equal(expected, result.Bytes.ToArray());
        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal("github.com", handler.Calls[0].RequestUri!.Host);
        Assert.Equal("release-assets.githubusercontent.com", handler.Calls[1].RequestUri!.Host);
        Assert.Equal("objects.githubusercontent.com", handler.Calls[2].RequestUri!.Host);
    }

    [Fact]
    public async Task DirectSourceUnderdeclaredLengthConsumesOnlyOneSentinelByte()
    {
        var stream = new RecordingReadStream(Encoding.UTF8.GetBytes(new string('x', 100)));
        var handler = new ScriptedHandler((_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentLength = 5;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                AssetUrl("mods-test", "manifest.json"),
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Contract, exception.Failure);
        Assert.Contains("Content-Length", exception.Message);
        Assert.Equal(6, stream.BytesReturned);
        Assert.Equal(6, stream.LargestRequestedRead);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ResolverDeclaredManifestBoundConsumesAtMostOneSentinelByteOnOverrun()
    {
        const string releaseTag = "mods-test";
        var stream = new RecordingReadStream(Encoding.UTF8.GetBytes(new string('x', 100)));
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var manifestUrl = AssetUrl(releaseTag, "manifest.json");
                var page = JsonResponse($$"""
                    [
                      {
                        "id": 123,
                        "tag_name": "{{releaseTag}}",
                        "published_at": "2026-08-26T12:00:00Z",
                        "draft": false,
                        "prerelease": false,
                        "assets": [
                          {
                            "id": 456,
                            "name": "manifest.json",
                            "size": 5,
                            "browser_download_url": "{{manifestUrl}}",
                            "digest": "sha256:{{new string('0', 64)}}"
                          }
                        ]
                      }
                    ]
                    """);
                page.Headers.ETag =
                    new System.Net.Http.Headers.EntityTagHeaderValue("\"stable\"");
                return Task.FromResult(page);
            }

            var content = new StreamContent(stream);
            content.Headers.ContentLength = 5;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(
            new RecoveryHistoryQuery(
                Repository,
                "mx",
                "1234567890",
                new string('a', 40)));

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.ManifestBoundExceeded, result.Failure);
        Assert.Equal(6, stream.BytesReturned);
        Assert.Equal(6, stream.LargestRequestedRead);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task StalledPostHeaderManifestReadHitsLinkedDeadlineAsTypedRemoteFailure()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubRecoveryReleaseSource(
            http,
            TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                AssetUrl("mods-test", "manifest.json"),
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Remote, exception.Failure);
        Assert.Contains("deadline", exception.Message);
    }

    [Fact]
    public async Task MidstreamManifestReadFailureIsTypedAsRemoteFailure()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new OneChunkThenThrowStream(
                    Encoding.UTF8.GetBytes("{\"partial\":")))
            }));
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var exception = await Assert.ThrowsAsync<RecoveryReleaseSourceException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                AssetUrl("mods-test", "manifest.json"),
                1024,
                default));

        Assert.Equal(RecoveryReleaseSourceFailure.Remote, exception.Failure);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task CallerCancellationDuringPostHeaderReadIsPreserved()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubRecoveryReleaseSource(http, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.GetManifestAsync(
                Repository,
                10,
                "mods-test",
                20,
                "manifest.json",
                AssetUrl("mods-test", "manifest.json"),
                1024,
                cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task DeletedManifestAssetHasTypedGoneResult(HttpStatusCode status)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);

        var result = await source.GetManifestAsync(
            Repository,
            10,
            "mods-test",
            20,
            "manifest.json",
            AssetUrl("mods-test", "manifest.json"),
            1024,
            default);

        Assert.Equal(RecoveryManifestFetchStatus.Gone, result.Status);
        Assert.True(result.Bytes.IsEmpty);
    }

    [Fact]
    public async Task CancellationPropagatesThroughHttpSource()
    {
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubRecoveryReleaseSource(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.GetReleasePageAsync(
                Repository,
                1,
                100,
                RecoveryHistoryResolver.MaximumTotalAssets,
                cancellation.Token));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ReleasePageJson(int count) => JsonSerializer.Serialize(
        Enumerable.Range(1, count).Select(index => new
        {
            id = index,
            tag_name = $"mods-page-{index:D3}",
            published_at = "2026-08-26T12:00:00Z",
            draft = false,
            prerelease = false,
            assets = Array.Empty<object>()
        }));

    private static string AssetUrl(string releaseTag, string assetName) =>
        $"https://github.com/{Repository}/releases/download/{releaseTag}/{assetName}";

    private static byte[] ProducerManifestBytes(string name) => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryManifests",
        name));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _send;

        public ScriptedHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
            _send = send;

        public List<HttpRequestMessage> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls.Add(CloneRequest(request));
            var response = await _send(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    private sealed class ThrowIfReadContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("content must not be read");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            WasRead = true;
            throw new InvalidOperationException("content must not be read");
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class OneChunkThenThrowStream : Stream
    {
        private readonly byte[] _chunk;
        private bool _sent;

        public OneChunkThenThrowStream(byte[] chunk) => _chunk = chunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent)
                throw new IOException("fixture midstream failure");
            _sent = true;
            var copied = Math.Min(count, _chunk.Length);
            _chunk.AsSpan(0, copied).CopyTo(buffer.AsSpan(offset, copied));
            return copied;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sent)
                return ValueTask.FromException<int>(
                    new IOException("fixture midstream failure"));
            _sent = true;
            var copied = Math.Min(buffer.Length, _chunk.Length);
            _chunk.AsMemory(0, copied).CopyTo(buffer);
            return ValueTask.FromResult(copied);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReadStream : Stream
    {
        private readonly byte[] _bytes;
        private int _offset;

        public RecordingReadStream(byte[] bytes) => _bytes = bytes;

        public int BytesReturned { get; private set; }
        public int LargestRequestedRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRequestedRead = Math.Max(LargestRequestedRead, count);
            var read = Math.Min(count, _bytes.Length - _offset);
            _bytes.AsSpan(_offset, read).CopyTo(buffer.AsSpan(offset, read));
            _offset += read;
            BytesReturned += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length);
            var read = Math.Min(buffer.Length, _bytes.Length - _offset);
            _bytes.AsMemory(_offset, read).CopyTo(buffer);
            _offset += read;
            BytesReturned += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
