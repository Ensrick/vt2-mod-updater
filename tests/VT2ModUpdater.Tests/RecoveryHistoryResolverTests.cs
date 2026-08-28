using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public class RecoveryHistoryResolverTests
{
    private const string Repository = RecoveryRecordContract.Repository;
    private const string ModId = "mx";
    private const string WorkshopId = "1234567890";
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OriginTag = "mods-fixture-2026-08-26";
    private const long ZipLength = 546;
    private const string ZipSha256 =
        "7d1f642208d5851b8cfa748e4207093c24de70a2a6377b2473b1b1996d86b4e0";

    [Fact]
    public async Task ExactTuple_ReturnsSourceExactSurvivingCoordinate()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.SourceExactSurvivingArtifact, result.Status);
        Assert.Equal(RecoveryResolutionFailure.None, result.Failure);
        Assert.NotNull(result.Artifact);
        Assert.Equal(100, result.Artifact!.ContainerReleaseId);
        Assert.Equal(1002, result.Artifact.AssetId);
        Assert.Equal("mx.zip", result.Artifact.AssetFilename);
        Assert.Equal(ZipLength, result.Artifact.AssetLength);
        Assert.Equal(OriginTag, result.Artifact.OriginReleaseTag);
        Assert.Equal(1, result.Artifact.EquivalentRecordCount);
        Assert.Equal(1, result.Artifact.SurvivingCoordinateCount);
        Assert.Equal(1, source.ManifestCalls);
        Assert.Equal(1, source.RevalidationCalls);
        Assert.Equal(
            source.Manifests[(100, 1001)].Bytes.Length,
            Assert.Single(source.ManifestByteBudgets));
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
    public async Task ByteExactProducerTrackedAndReceiptManifestsResolve(
        string fixture,
        string authority,
        string expectedFixtureSha256)
    {
        var manifest = ProducerManifestBytes(fixture);
        Assert.Equal(expectedFixtureSha256, Sha256(manifest));
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var releaseTag = root.GetProperty("release_tag").GetString()!;
        var recovery = root.GetProperty("mods")[0].GetProperty("recovery");
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddProducerManifestRelease(source, 100, manifest));

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(
            new RecoveryHistoryQuery(
                Repository,
                recovery.GetProperty("mod_id").GetString()!,
                recovery.GetProperty("workshop_id").GetString()!,
                recovery.GetProperty("source").GetProperty("commit").GetString()!));

        Assert.Equal(RecoveryResolutionStatus.SourceExactSurvivingArtifact, result.Status);
        Assert.Equal(authority, result.Artifact!.Proof.Record.BundleAuthority);
        Assert.Equal(releaseTag, result.Artifact.OriginReleaseTag);
    }

    [Fact]
    public async Task RepeatedIdenticalTuple_SelectsNewestPublishedSurvivorAfterCompleteScan()
    {
        var source = new FakeReleaseSource();
        var older = AddFixtureRelease(
            source,
            100,
            "mods-container-old",
            publishedAt: DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        var newer = AddFixtureRelease(
            source,
            200,
            "mods-container-new",
            publishedAt: DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        source.SetSinglePage(older, newer);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.SourceExactSurvivingArtifact, result.Status);
        Assert.Equal(200, result.Artifact!.ContainerReleaseId);
        Assert.Equal("mods-container-new", result.Artifact.ContainerReleaseTag);
        Assert.Equal(OriginTag, result.Artifact.OriginReleaseTag);
        Assert.Equal(2, result.Artifact.EquivalentRecordCount);
        Assert.Equal(2, result.Artifact.SurvivingCoordinateCount);
    }

    [Fact]
    public async Task RepeatedTuple_TieBreaksByNumericReleaseThenAssetId()
    {
        var source = new FakeReleaseSource();
        var time = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var lower = AddFixtureRelease(source, 100, "mods-container-a", time);
        var higher = AddFixtureRelease(source, 200, "mods-container-b", time);
        source.SetSinglePage(higher, lower);

        var result = await Resolve(source);

        Assert.Equal(200, result.Artifact!.ContainerReleaseId);
        Assert.Equal(2002, result.Artifact.AssetId);
    }

    [Fact]
    public async Task SameSemanticProof_MissingOlderZipStillSelectsSurvivingCopy()
    {
        var source = new FakeReleaseSource();
        var missing = AddFixtureRelease(
            source,
            100,
            "mods-container-old",
            includeZip: false);
        var survivor = AddFixtureRelease(
            source,
            200,
            "mods-container-new",
            includeZip: true);
        source.SetSinglePage(missing, survivor);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.SourceExactSurvivingArtifact, result.Status);
        Assert.Equal(200, result.Artifact!.ContainerReleaseId);
        Assert.Equal(2, result.Artifact.EquivalentRecordCount);
        Assert.Equal(1, result.Artifact.SurvivingCoordinateCount);
    }

    [Fact]
    public async Task DivergentRecoverySemanticProofs_AreRejectedAsAmbiguous()
    {
        var source = new FakeReleaseSource();
        var first = AddFixtureRelease(source, 100, "mods-container-a");
        var second = AddFixtureRelease(
            source,
            200,
            "mods-container-b",
            recoveryMutate: recovery =>
                recovery["release"]!["tag"] = "mods-different-origin");
        source.SetSinglePage(first, second);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.AmbiguousSemanticProof, result.Failure);
        Assert.Equal(2, result.Evidence.MatchingRecords);
    }

    [Fact]
    public async Task SameSourceTupleWithCoherentVersionDriftIsStillAmbiguous()
    {
        var source = new FakeReleaseSource();
        var first = AddFixtureRelease(source, 100, "mods-container-a");
        var second = AddFixtureRelease(
            source,
            200,
            "mods-container-b",
            recoveryMutate: recovery => recovery["version"] = "2.0.0",
            rowMutate: row => row["version"] = "2.0.0");
        source.SetSinglePage(first, second);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.AmbiguousSemanticProof, result.Failure);
        Assert.Equal(2, result.Evidence.MatchingRecords);
    }

    [Fact]
    public async Task ExactRecordWithoutZip_ReturnsDeterministicArtifactGone()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            includeZip: false));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSurvivingArchive, result.Failure);
        Assert.Equal(1, result.Evidence.MatchingRecords);
    }

    [Fact]
    public async Task NoRecoveryRecord_ReturnsDeterministicArtifactGone()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            includeRecovery: false));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
    }

    [Fact]
    public async Task MissingManifestAsset_ReturnsDeterministicArtifactGone()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(new RecoveryReleaseSummary(
            100,
            OriginTag,
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            false,
            false,
            Array.Empty<RecoveryReleaseAssetSummary>()));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
        Assert.Equal(0, source.ManifestCalls);
    }

    [Fact]
    public async Task DeletedManifestBytes_ReturnsDeterministicArtifactGone()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        source.Manifests[(100, 1001)] = RecoveryManifestFetch.Gone;
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
    }

    [Theory]
    [InlineData("Manifest.json", RecoveryResolutionFailure.MalformedReleaseMetadata)]
    [InlineData("MANIFEST.JSON", RecoveryResolutionFailure.MalformedReleaseMetadata)]
    public async Task WrongCaseManifestAsset_IsRejected(
        string wrongCase,
        RecoveryResolutionFailure expectedFailure)
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "manifest.json"
                    ? asset with
                    {
                        Name = wrongCase,
                        BrowserDownloadUrl = AssetUrl(OriginTag, wrongCase)
                    }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(expectedFailure, result.Failure);
        Assert.Equal(0, source.ManifestCalls);
    }

    [Fact]
    public async Task WrongCaseZipAsset_IsRejectedInsteadOfTreatedAsMissing()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "mx.zip"
                    ? asset with
                    {
                        Name = "MX.zip",
                        BrowserDownloadUrl = AssetUrl(OriginTag, "MX.zip")
                    }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.TamperedArtifactCoordinate, result.Failure);
    }

    [Fact]
    public async Task ZipLengthDrift_IsRejectedAsTamperedCoordinate()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "mx.zip"
                    ? asset with { Size = asset.Size + 1 }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.TamperedArtifactCoordinate, result.Failure);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("mod")]
    [InlineData("workshop")]
    [InlineData("source")]
    public async Task ForeignOrNonCanonicalQuery_FailsBeforeNetwork(string axis)
    {
        var source = new FakeReleaseSource();
        var query = Query() with
        {
            Repository = axis == "repository" ? "Other/repo" : Repository,
            ModId = axis == "mod" ? "mx\n" : ModId,
            WorkshopId = axis == "workshop" ? "0123" : WorkshopId,
            SourceCommit = axis == "source" ? new string('A', 40) : SourceCommit
        };

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(query);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.InvalidQuery, result.Failure);
        Assert.Equal(0, source.PageCalls);
    }

    [Fact]
    public async Task ForeignRepositoryPage_IsRejected()
    {
        var source = new FakeReleaseSource();
        source.Pages[1] = new RecoveryReleasePage(
            "Other/repo",
            1,
            RecoveryHistoryResolver.ReleasesPerPage,
            "\"etag\"",
            false,
            Array.Empty<RecoveryReleaseSummary>());

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("release-tag")]
    [InlineData("asset")]
    public async Task NullSourceMetadataReturnsTypedContractFailure(string axis)
    {
        var source = new FakeReleaseSource();
        if (axis == "page")
        {
            source.Pages[1] = null!;
        }
        else if (axis == "release-tag")
        {
            source.SetSinglePage(EmptyRelease(1) with { TagName = null! });
        }
        else
        {
            source.SetSinglePage(new RecoveryReleaseSummary(
                1,
                "mods-null-asset",
                DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
                false,
                false,
                Array.AsReadOnly(new RecoveryReleaseAssetSummary[] { null! })));
        }

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-etag")]
    [InlineData("*")]
    public async Task MissingMalformedOrWildcardEtag_IsRejected(string entityTag)
    {
        var source = new FakeReleaseSource();
        source.Pages[1] = new RecoveryReleasePage(
            Repository,
            1,
            RecoveryHistoryResolver.ReleasesPerPage,
            entityTag,
            false,
            Array.Empty<RecoveryReleaseSummary>());

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
        Assert.Equal(0, source.RevalidationCalls);
    }

    [Theory]
    [InlineData("mod_id", "other")]
    [InlineData("workshop_id", "987654321")]
    [InlineData("source_commit", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("sha256", "1111111111111111111111111111111111111111111111111111111111111111")]
    public async Task ParentRecoveryBindingDrift_IsRejected(
        string property,
        string replacement)
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            rowMutate: row => row[property] = replacement));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
    }

    [Fact]
    public async Task RecoveryForeignRepository_IsRejectedByMergedStrictContract()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            recoveryMutate: recovery =>
                recovery["release"]!["repository"] = "Other/repository"));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
    }

    [Fact]
    public async Task ContainerManifestTagMustMatchNumericReleaseMetadata()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, "mods-container-a");
        release = release with { TagName = "mods-container-b" };
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset with
                {
                    BrowserDownloadUrl = AssetUrl(release.TagName, asset.Name)
                })
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.Contains("release_tag", result.Message);
    }

    [Fact]
    public async Task FullFifthPageWithMoreHistory_ReturnsBoundedExhaustion()
    {
        var source = new FakeReleaseSource();
        for (var pageNumber = 1; pageNumber <= RecoveryHistoryResolver.MaximumPages; pageNumber++)
        {
            var releases = Enumerable.Range(1, RecoveryHistoryResolver.ReleasesPerPage)
                .Select(index => EmptyRelease(pageNumber * 1000L + index))
                .ToArray();
            source.Pages[pageNumber] = Page(pageNumber, true, releases);
        }

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.HistoryBoundExceeded, result.Failure);
        Assert.Equal(5, result.Evidence.PagesScanned);
        Assert.Equal(500, result.Evidence.ReleasesScanned);
        Assert.Equal(0, source.RevalidationCalls);
    }

    [Fact]
    public async Task FullFifthPageWithoutNextRelationIsRejectedAsUnprovenTerminal()
    {
        var source = new FakeReleaseSource();
        for (var pageNumber = 1; pageNumber <= RecoveryHistoryResolver.MaximumPages; pageNumber++)
        {
            var releases = Enumerable.Range(1, RecoveryHistoryResolver.ReleasesPerPage)
                .Select(index => EmptyRelease(pageNumber * 1000L + index))
                .ToArray();
            source.Pages[pageNumber] = Page(
                pageNumber,
                pageNumber < RecoveryHistoryResolver.MaximumPages,
                releases);
        }

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
        Assert.Equal(4, result.Evidence.PagesScanned);
    }

    [Fact]
    public async Task ShortFifthPageIsACompleteBoundedHistoryAndAllPagesRevalidate()
    {
        var source = new FakeReleaseSource();
        for (var pageNumber = 1; pageNumber < RecoveryHistoryResolver.MaximumPages; pageNumber++)
        {
            var releases = Enumerable.Range(1, RecoveryHistoryResolver.ReleasesPerPage)
                .Select(index => EmptyRelease(pageNumber * 1000L + index))
                .ToArray();
            source.Pages[pageNumber] = Page(pageNumber, true, releases);
        }
        source.Pages[RecoveryHistoryResolver.MaximumPages] = Page(
            RecoveryHistoryResolver.MaximumPages,
            false,
            EmptyRelease(5001));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
        Assert.Equal(5, result.Evidence.PagesScanned);
        Assert.Equal(5, source.RevalidationCalls);
    }

    [Fact]
    public async Task HostilePartialPaginationClaim_IsRejected()
    {
        var source = new FakeReleaseSource();
        source.Pages[1] = Page(1, true, EmptyRelease(1));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
    }

    [Fact]
    public async Task RepeatedReleaseAcrossPages_IsRejected()
    {
        var source = new FakeReleaseSource();
        var firstPage = Enumerable.Range(1, RecoveryHistoryResolver.ReleasesPerPage)
            .Select(index => EmptyRelease(index))
            .ToArray();
        source.Pages[1] = Page(1, true, firstPage);
        source.Pages[2] = Page(2, false, firstPage[0]);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
    }

    [Fact]
    public async Task RepeatedAssetIdAcrossDifferentReleasesIsRejectedGlobally()
    {
        var source = new FakeReleaseSource();
        var first = new RecoveryReleaseSummary(
            1,
            "mods-assets-1",
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            false,
            false,
            Array.AsReadOnly(new[]
            {
                new RecoveryReleaseAssetSummary(
                    500,
                    "first.bin",
                    1,
                    AssetUrl("mods-assets-1", "first.bin"),
                    new string('a', 64))
            }));
        var second = new RecoveryReleaseSummary(
            2,
            "mods-assets-2",
            DateTimeOffset.Parse("2026-08-26T12:00:01Z"),
            false,
            false,
            Array.AsReadOnly(new[]
            {
                new RecoveryReleaseAssetSummary(
                    500,
                    "second.bin",
                    1,
                    AssetUrl("mods-assets-2", "second.bin"),
                    new string('b', 64))
            }));
        source.SetSinglePage(first, second);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
        Assert.Contains("asset id 500", result.Message);
    }

    [Fact]
    public async Task CompleteTwoPageScanRevalidatesEveryObservedPage()
    {
        var source = new FakeReleaseSource();
        var firstPage = Enumerable.Range(1, RecoveryHistoryResolver.ReleasesPerPage)
            .Select(index => EmptyRelease(index))
            .ToArray();
        source.Pages[1] = Page(1, true, firstPage);
        source.Pages[2] = Page(2, false, AddFixtureRelease(source, 1000, OriginTag));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.SourceExactSurvivingArtifact, result.Status);
        Assert.Equal(2, result.Evidence.PagesScanned);
        Assert.Equal(2, source.RevalidationCalls);
    }

    [Fact]
    public async Task RemainingAggregateAssetBudgetIsPassedToEachPageParser()
    {
        var source = new FakeReleaseSource();
        var firstPage = Enumerable.Range(1, RecoveryHistoryResolver.ReleasesPerPage)
            .Select(index =>
            {
                var tag = $"mods-budget-{index}";
                return new RecoveryReleaseSummary(
                    index,
                    tag,
                    DateTimeOffset.Parse("2026-08-26T12:00:00Z").AddSeconds(index),
                    false,
                    false,
                    Array.AsReadOnly(new[]
                    {
                        new RecoveryReleaseAssetSummary(
                            10_000 + index,
                            $"asset-{index}.bin",
                            1,
                            AssetUrl(tag, $"asset-{index}.bin"),
                            new string('a', 64))
                    }));
            })
            .ToArray();
        source.Pages[1] = Page(1, true, firstPage);
        source.Pages[2] = Page(2, false, EmptyRelease(1001));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(
            new[]
            {
                RecoveryHistoryResolver.MaximumTotalAssets,
                RecoveryHistoryResolver.MaximumTotalAssets -
                    RecoveryHistoryResolver.ReleasesPerPage
            },
            source.AssetBudgets);
    }

    [Fact]
    public async Task ExactOrdinalTupleDoesNotFallBackToAnotherMod()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(source, 100, OriginTag));
        var query = Query() with { ModId = "other" };

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(query);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
    }

    [Fact]
    public async Task ManifestRowCountIsRefusedBeforeRecoveryRowParsing()
    {
        var source = new FakeReleaseSource();
        var manifest = BuildLegacyManifest(
            OriginTag,
            RecoveryHistoryResolver.MaximumRowsPerRelease + 1);
        var release = AddRawManifestRelease(source, 100, OriginTag, manifest);
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.RowBoundExceeded, result.Failure);
    }

    [Fact]
    public async Task LegacyManifestWithoutSchemaIsCountedButCannotBecomeSourceExact()
    {
        var source = new FakeReleaseSource();
        var manifest = BuildLegacyManifest(OriginTag, 1, includeSchema: false);
        source.SetSinglePage(AddRawManifestRelease(source, 100, OriginTag, manifest));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
        Assert.Equal(1, result.Evidence.RowsScanned);
    }

    [Fact]
    public async Task SchemaTwoRootPropertySetIsExactEvenWithoutRecovery()
    {
        var source = new FakeReleaseSource();
        var node = JsonNode.Parse(Encoding.UTF8.GetString(
            BuildLegacyManifest(OriginTag, 1)))!.AsObject();
        node["unexpected"] = true;
        source.SetSinglePage(AddRawManifestRelease(
            source,
            100,
            OriginTag,
            Encoding.UTF8.GetBytes(node.ToJsonString())));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.Contains("property set", result.Message);
    }

    [Fact]
    public async Task HistoricalUtf8BomManifestIsParsedWithoutWeakeningRecoveryProof()
    {
        var source = new FakeReleaseSource();
        var payload = BuildLegacyManifest(OriginTag, 1, includeSchema: false);
        var manifest = new byte[payload.Length + 3];
        manifest[0] = 0xef;
        manifest[1] = 0xbb;
        manifest[2] = 0xbf;
        payload.CopyTo(manifest, 3);
        source.SetSinglePage(AddRawManifestRelease(source, 100, OriginTag, manifest));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
        Assert.Equal(1, result.Evidence.RowsScanned);
    }

    [Fact]
    public async Task HistoricalOuterTagDriftWithoutRecoveryCannotBlockOrBecomeExact()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(
            source,
            100,
            "mods-manifest-tag",
            includeRecovery: false);
        release = release with { TagName = "mods-container-tag" };
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset with
                {
                    BrowserDownloadUrl = AssetUrl(release.TagName, asset.Name)
                })
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ArtifactGone, result.Status);
        Assert.Equal(RecoveryResolutionFailure.NoSourceExactRecord, result.Failure);
        Assert.Equal(1, result.Evidence.RowsScanned);
    }

    [Fact]
    public async Task LegacyManifestCannotSmuggleARecoveryChild()
    {
        var source = new FakeReleaseSource();
        var node = JsonNode.Parse(Encoding.UTF8.GetString(
            BuildFixtureManifest(OriginTag)))!.AsObject();
        node.Remove("manifest_schema");
        var manifest = Encoding.UTF8.GetBytes(node.ToJsonString());
        source.SetSinglePage(AddRawManifestRelease(source, 100, OriginTag, manifest));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.Contains("cannot carry a recovery child", result.Message);
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("missing-published")]
    [InlineData("noncanonical-timestamp")]
    public async Task RecoveryBearingSchemaTwoRequiresExactCanonicalRoot(string axis)
    {
        var source = new FakeReleaseSource();
        var node = JsonNode.Parse(Encoding.UTF8.GetString(
            BuildFixtureManifest(OriginTag)))!.AsObject();
        if (axis == "unknown-root")
            node["unexpected"] = true;
        else if (axis == "missing-published")
            node.Remove("published_at");
        else
            node["published_at"] = "2026-08-26T12:00:00+00:00";
        source.SetSinglePage(AddRawManifestRelease(
            source,
            100,
            OriginTag,
            Encoding.UTF8.GetBytes(node.ToJsonString())));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
    }

    [Theory]
    [InlineData("root-whole-seconds")]
    [InlineData("root-offset")]
    [InlineData("root-lowercase-z")]
    [InlineData("root-trailing")]
    [InlineData("authorization-fraction")]
    [InlineData("authorization-offset")]
    [InlineData("authorization-lowercase-z")]
    [InlineData("authorization-trailing")]
    public async Task ProducerTimestampAxesRejectCrossShapesAndVariants(string axis)
    {
        var source = new FakeReleaseSource();
        var node = JsonNode.Parse(Encoding.UTF8.GetString(
            BuildFixtureManifest(OriginTag)))!.AsObject();
        var authorization = node["mods"]![0]!["publication_authorization"]!;
        switch (axis)
        {
            case "root-whole-seconds":
                node["published_at"] = "2026-08-26T12:00:00Z";
                break;
            case "root-offset":
                node["published_at"] = "2026-08-26T12:00:00.0000000+00:00";
                break;
            case "root-lowercase-z":
                node["published_at"] = "2026-08-26T12:00:00.0000000z";
                break;
            case "root-trailing":
                node["published_at"] = "2026-08-26T12:00:00.0000000Z ";
                break;
            case "authorization-fraction":
                authorization["checked_at_utc"] = "2026-08-26T11:59:00.0000000Z";
                break;
            case "authorization-offset":
                authorization["checked_at_utc"] = "2026-08-26T11:59:00+00:00";
                break;
            case "authorization-lowercase-z":
                authorization["checked_at_utc"] = "2026-08-26T11:59:00z";
                break;
            default:
                authorization["checked_at_utc"] = "2026-08-26T11:59:00Z ";
                break;
        }
        source.SetSinglePage(AddRawManifestRelease(
            source,
            100,
            OriginTag,
            Encoding.UTF8.GetBytes(node.ToJsonString())));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.True(
            result.Message.Contains("grammar", StringComparison.Ordinal) ||
            result.Message.Contains("non-canonical", StringComparison.Ordinal),
            result.Message);
    }

    [Theory]
    [InlineData("unknown-row")]
    [InlineData("missing-authorization")]
    [InlineData("unknown-builder")]
    public async Task RecoveryBearingRowRequiresExactProducerPropertySets(string axis)
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            rowMutate: row =>
            {
                if (axis == "unknown-row")
                    row["unexpected"] = true;
                else if (axis == "missing-authorization")
                    row.Remove("publication_authorization");
                else
                    row["builder"]!["unexpected"] = true;
            }));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.Contains("property set", result.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-case")]
    public async Task NullOrWrongCaseRecoveryChildCannotBecomeLegacy(string axis)
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            rowMutate: row =>
            {
                if (axis == "null")
                {
                    row["recovery"] = null;
                    return;
                }
                var recovery = row["recovery"]!.DeepClone();
                row.Remove("recovery");
                row["Recovery"] = recovery;
            }));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
    }

    [Fact]
    public async Task ReceiptAuthorityWithoutRecoveryIsRejectedInsteadOfTreatedAsLegacy()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(
            source,
            100,
            OriginTag,
            includeRecovery: false,
            recoveryFixture: "valid-receipt.json"));

        var result = await new RecoveryHistoryResolver(source).ResolveAsync(
            Query() with
            {
                SourceCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            });

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.Contains("receipt authority requires recovery", result.Message);
    }

    [Fact]
    public async Task AggregateRowBoundIsEnforcedAcrossReleases()
    {
        var source = new FakeReleaseSource();
        var manifest = BuildLegacyManifest(
            "ignored-legacy-tag",
            RecoveryHistoryResolver.MaximumRowsPerRelease,
            includeSchema: false);
        var releases = new List<RecoveryReleaseSummary>();
        for (var index = 1; index <= 65; index++)
            releases.Add(AddRawManifestRelease(source, index, $"mods-rows-{index}", manifest));
        source.SetSinglePage(releases.ToArray());

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.RowBoundExceeded, result.Failure);
        Assert.Equal(64, result.Evidence.ManifestsRead);
        Assert.Equal(RecoveryHistoryResolver.MaximumTotalRows, result.Evidence.RowsScanned);
    }

    [Fact]
    public async Task RemainingRowBudgetWinsBeforeMalformedFirstExcessRowIsInspected()
    {
        var source = new FakeReleaseSource();
        var releases = new List<RecoveryReleaseSummary>();
        var full = BuildLegacyManifest(
            "ignored-legacy-tag",
            RecoveryHistoryResolver.MaximumRowsPerRelease,
            includeSchema: false);
        for (var index = 1; index <= 63; index++)
            releases.Add(AddRawManifestRelease(source, index, $"mods-rows-{index}", full));
        releases.Add(AddRawManifestRelease(
            source,
            64,
            "mods-rows-64",
            BuildLegacyManifest("ignored", 255, includeSchema: false)));

        var excess = JsonNode.Parse(Encoding.UTF8.GetString(
            BuildLegacyManifest("ignored", 2, includeSchema: false)))!.AsObject();
        excess["mods"]!.AsArray()[0] = "malformed-row-that-must-not-be-inspected";
        releases.Add(AddRawManifestRelease(
            source,
            65,
            "mods-rows-65",
            Encoding.UTF8.GetBytes(excess.ToJsonString())));
        source.SetSinglePage(releases.ToArray());

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.RowBoundExceeded, result.Failure);
        Assert.Equal(RecoveryHistoryResolver.MaximumTotalRows - 1, result.Evidence.RowsScanned);
        Assert.Equal(65, source.ManifestCalls);
    }

    [Fact]
    public async Task AggregateManifestByteBoundIsRefusedBeforeSixtyFifthFetch()
    {
        var source = new FakeReleaseSource();
        var small = BuildLegacyManifest("ignored-legacy-tag", 1, includeSchema: false);
        var manifest = new byte[RecoveryHistoryResolver.MaximumManifestBytes];
        small.CopyTo(manifest, 0);
        Array.Fill(manifest, (byte)' ', small.Length, manifest.Length - small.Length);
        var releases = new List<RecoveryReleaseSummary>();
        for (var index = 1; index <= 65; index++)
            releases.Add(AddRawManifestRelease(source, index, $"mods-bytes-{index}", manifest));
        source.SetSinglePage(releases.ToArray());

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.ManifestBoundExceeded, result.Failure);
        Assert.Equal(64, source.ManifestCalls);
        Assert.Equal(
            RecoveryHistoryResolver.MaximumAggregateManifestBytes,
            result.Evidence.ManifestBytesRead);
    }

    [Fact]
    public async Task AggregateAssetBoundIsEnforcedBeforeAnyManifestFetch()
    {
        var source = new FakeReleaseSource();
        var releases = new List<RecoveryReleaseSummary>();
        for (var releaseIndex = 1; releaseIndex <= 65; releaseIndex++)
        {
            var tag = $"mods-assets-{releaseIndex}";
            var assets = Enumerable.Range(1, RecoveryHistoryResolver.MaximumAssetsPerRelease)
                .Select(assetIndex => new RecoveryReleaseAssetSummary(
                    releaseIndex * 1000L + assetIndex,
                    $"asset-{assetIndex:D3}.bin",
                    1,
                    AssetUrl(tag, $"asset-{assetIndex:D3}.bin"),
                    new string('d', 64)))
                .ToArray();
            releases.Add(new RecoveryReleaseSummary(
                releaseIndex,
                tag,
                DateTimeOffset.Parse("2026-08-26T12:00:00Z").AddSeconds(releaseIndex),
                false,
                false,
                Array.AsReadOnly(assets)));
        }
        source.SetSinglePage(releases.ToArray());

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.AssetBoundExceeded, result.Failure);
        Assert.Equal(0, result.Evidence.AssetsScanned);
        Assert.Equal(
            [RecoveryHistoryResolver.MaximumTotalAssets],
            source.AssetBudgets);
        Assert.Equal(0, source.ManifestCalls);
    }

    [Fact]
    public async Task OversizedDeclaredAssetIsTypedAsBoundedExhaustion()
    {
        var source = new FakeReleaseSource();
        var tag = "mods-large-asset";
        source.SetSinglePage(new RecoveryReleaseSummary(
            100,
            tag,
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            false,
            false,
            Array.AsReadOnly(new[]
            {
                new RecoveryReleaseAssetSummary(
                    1001,
                    "large.bin",
                    RecoveryHistoryResolver.MaximumDeclaredAssetBytes + 1,
                    AssetUrl(tag, "large.bin"),
                    new string('d', 64))
            })));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.AssetBoundExceeded, result.Failure);
    }

    [Fact]
    public async Task ForeignBrowserDownloadUrlIsRejectedBeforeManifestFetch()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "manifest.json"
                    ? asset with { BrowserDownloadUrl = "https://evil.example/manifest.json" }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedReleaseMetadata, result.Failure);
        Assert.Equal(0, source.ManifestCalls);
    }

    [Fact]
    public async Task ManifestDeclaredOverOneMiB_IsRefusedBeforeFetch()
    {
        var source = new FakeReleaseSource();
        var release = new RecoveryReleaseSummary(
            100,
            OriginTag,
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            false,
            false,
            Array.AsReadOnly(new[]
            {
                new RecoveryReleaseAssetSummary(
                    1001,
                    "manifest.json",
                    RecoveryHistoryResolver.MaximumManifestBytes + 1L,
                    AssetUrl(OriginTag, "manifest.json"),
                    new string('d', 64))
            }));
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.BoundedExhaustion, result.Status);
        Assert.Equal(RecoveryResolutionFailure.ManifestBoundExceeded, result.Failure);
        Assert.Equal(0, source.ManifestCalls);
    }

    [Fact]
    public async Task ManifestByteLengthMustEqualReleaseCoordinate()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "manifest.json"
                    ? asset with { Size = asset.Size + 1 }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.TamperedArtifactCoordinate, result.Failure);
    }

    [Fact]
    public async Task ManifestBytesMustMatchExactNumericAssetDigest()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "manifest.json"
                    ? asset with { DigestSha256 = new string('0', 64) }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.TamperedArtifactCoordinate, result.Failure);
        Assert.Contains("manifest bytes", result.Message);
    }

    [Fact]
    public async Task ZipGitHubDigestMustEqualRecoveryDeclaredSha256()
    {
        var source = new FakeReleaseSource();
        var release = AddFixtureRelease(source, 100, OriginTag);
        release = release with
        {
            Assets = Array.AsReadOnly(release.Assets
                .Select(asset => asset.Name == "mx.zip"
                    ? asset with { DigestSha256 = new string('0', 64) }
                    : asset)
                .ToArray())
        };
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.TamperedArtifactCoordinate, result.Failure);
        Assert.Contains("GitHub digest", result.Message);
    }

    [Fact]
    public async Task DuplicateRecoveryProperty_IsRejectedWithoutLastValueWins()
    {
        var source = new FakeReleaseSource();
        var manifest = Encoding.UTF8.GetString(BuildFixtureManifest(OriginTag));
        manifest = manifest.Replace(
            "\"recovery\": {",
            "\"recovery\": null, \"recovery\": {",
            StringComparison.Ordinal);
        var release = AddRawManifestRelease(
            source,
            100,
            OriginTag,
            Encoding.UTF8.GetBytes(manifest));
        source.SetSinglePage(release);

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.ContractFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.MalformedManifest, result.Failure);
        Assert.Contains("duplicate property 'recovery'", result.Message);
    }

    [Fact]
    public async Task HistoryChangeDuringEtagRevalidation_FailsClosed()
    {
        var source = new FakeReleaseSource
        {
            Revalidation = RecoveryPageRevalidation.Changed
        };
        source.SetSinglePage(AddFixtureRelease(source, 100, OriginTag));

        var result = await Resolve(source);

        Assert.Equal(RecoveryResolutionStatus.RemoteFailure, result.Status);
        Assert.Equal(RecoveryResolutionFailure.HistoryChangedDuringScan, result.Failure);
        Assert.Null(result.Artifact);
    }

    [Theory]
    [InlineData(RecoveryReleaseSourceFailure.Remote, RecoveryResolutionStatus.RemoteFailure)]
    [InlineData(RecoveryReleaseSourceFailure.Contract, RecoveryResolutionStatus.ContractFailure)]
    [InlineData(RecoveryReleaseSourceFailure.HistoryBoundExceeded, RecoveryResolutionStatus.BoundedExhaustion)]
    public async Task SourceFailuresRemainTyped(
        RecoveryReleaseSourceFailure sourceFailure,
        RecoveryResolutionStatus expectedStatus)
    {
        var source = new FakeReleaseSource { ThrowOnPage = sourceFailure };

        var result = await Resolve(source);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Artifact);
    }

    [Fact]
    public async Task CancellationPropagatesAndDoesNotBecomeRemoteFailure()
    {
        var source = new FakeReleaseSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecoveryHistoryResolver(source).ResolveAsync(Query(), cancellation.Token));
        Assert.Equal(0, source.PageCalls);
    }

    [Fact]
    public async Task CancellationDuringManifestScanPropagates()
    {
        var source = new FakeReleaseSource();
        source.SetSinglePage(AddFixtureRelease(source, 100, OriginTag));
        using var cancellation = new CancellationTokenSource();
        source.OnManifest = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecoveryHistoryResolver(source).ResolveAsync(Query(), cancellation.Token));
        Assert.Equal(1, source.ManifestCalls);
        Assert.Equal(0, source.RevalidationCalls);
    }

    private static Task<RecoveryHistoryResolution> Resolve(FakeReleaseSource source) =>
        new RecoveryHistoryResolver(source).ResolveAsync(Query());

    private static RecoveryHistoryQuery Query() =>
        new(Repository, ModId, WorkshopId, SourceCommit);

    private static RecoveryReleaseSummary AddFixtureRelease(
        FakeReleaseSource source,
        long id,
        string containerTag,
        DateTimeOffset? publishedAt = null,
        bool includeZip = true,
        bool includeRecovery = true,
        Action<JsonObject>? recoveryMutate = null,
        Action<JsonObject>? rowMutate = null,
        string recoveryFixture = "valid-tracked.json")
    {
        var recovery = JsonNode.Parse(FixtureJson(recoveryFixture))!.AsObject();
        recoveryMutate?.Invoke(recovery);
        var manifest = BuildFixtureManifest(
            containerTag,
            includeRecovery,
            recoveryMutate: null,
            rowMutate: rowMutate,
            recoveryFixture: recoveryFixture,
            preparedRecovery: recovery);
        var assets = new List<RecoveryReleaseAssetSummary>
        {
            new(
                id * 10 + 1,
                "manifest.json",
                manifest.Length,
                AssetUrl(containerTag, "manifest.json"),
                Sha256(manifest))
        };
        if (includeZip)
        {
            assets.Add(new RecoveryReleaseAssetSummary(
                id * 10 + 2,
                "mx.zip",
                recovery["asset"]!["length"]!.GetValue<long>(),
                AssetUrl(containerTag, "mx.zip"),
                recovery["asset"]!["sha256"]!.GetValue<string>()));
        }
        source.Manifests[(id, id * 10 + 1)] = new RecoveryManifestFetch(
            RecoveryManifestFetchStatus.Found,
            manifest);
        return new RecoveryReleaseSummary(
            id,
            containerTag,
            publishedAt ?? DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            false,
            false,
            Array.AsReadOnly(assets.ToArray()));
    }

    private static RecoveryReleaseSummary AddRawManifestRelease(
        FakeReleaseSource source,
        long id,
        string tag,
        byte[] manifest)
    {
        source.Manifests[(id, id * 10 + 1)] = new RecoveryManifestFetch(
            RecoveryManifestFetchStatus.Found,
            manifest);
        return new RecoveryReleaseSummary(
            id,
            tag,
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            false,
            false,
            Array.AsReadOnly(new[]
            {
                new RecoveryReleaseAssetSummary(
                    id * 10 + 1,
                    "manifest.json",
                    manifest.Length,
                    AssetUrl(tag, "manifest.json"),
                    Sha256(manifest))
            }));
    }

    private static RecoveryReleaseSummary AddProducerManifestRelease(
        FakeReleaseSource source,
        long id,
        byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var releaseTag = root.GetProperty("release_tag").GetString()!;
        var recovery = root.GetProperty("mods")[0].GetProperty("recovery");
        var asset = recovery.GetProperty("asset");
        var assetFilename = asset.GetProperty("filename").GetString()!;
        source.Manifests[(id, id * 10 + 1)] = new RecoveryManifestFetch(
            RecoveryManifestFetchStatus.Found,
            manifest);
        return new RecoveryReleaseSummary(
            id,
            releaseTag,
            DateTimeOffset.Parse(root.GetProperty("published_at").GetString()!),
            false,
            false,
            Array.AsReadOnly(new[]
            {
                new RecoveryReleaseAssetSummary(
                    id * 10 + 1,
                    "manifest.json",
                    manifest.Length,
                    AssetUrl(releaseTag, "manifest.json"),
                    Sha256(manifest)),
                new RecoveryReleaseAssetSummary(
                    id * 10 + 2,
                    assetFilename,
                    asset.GetProperty("length").GetInt64(),
                    AssetUrl(releaseTag, assetFilename),
                    asset.GetProperty("sha256").GetString()!)
            }));
    }

    private static byte[] BuildFixtureManifest(
        string containerTag,
        bool includeRecovery = true,
        Action<JsonObject>? recoveryMutate = null,
        Action<JsonObject>? rowMutate = null,
        string recoveryFixture = "valid-tracked.json",
        JsonObject? preparedRecovery = null)
    {
        var recovery = preparedRecovery ??
            JsonNode.Parse(FixtureJson(recoveryFixture))!.AsObject();
        recoveryMutate?.Invoke(recovery);
        var bundleFiles = new JsonArray();
        foreach (var output in recovery["output"]!["files"]!.AsArray())
        {
            bundleFiles.Add(new JsonObject
            {
                ["filename"] = output!["filename"]!.DeepClone(),
                ["sha256"] = output["sha256"]!.DeepClone()
            });
        }

        var row = new JsonObject
        {
            ["mod_id"] = recovery["mod_id"]!.DeepClone(),
            ["friendly_name"] = "Fixture Mod",
            ["workshop_id"] = recovery["workshop_id"]!.DeepClone(),
            ["version"] = recovery["version"]!.DeepClone(),
            ["asset_filename"] = recovery["asset"]!["filename"]!.DeepClone(),
            ["sha256"] = recovery["asset"]!["sha256"]!.DeepClone(),
            ["visibility"] = "public",
            ["source_commit"] = recovery["source"]!["commit"]!.DeepClone(),
            ["source_state"] = recovery["source"]!["state"]!.DeepClone(),
            ["bundle_authority"] = recovery["bundle_authority"]!.DeepClone(),
            ["builder"] = recovery["builder"]!.DeepClone(),
            ["root_bundle"] = recovery["root_bundle"]!.DeepClone(),
            ["descriptor_name"] = recovery["descriptor"]!["filename"]!.DeepClone(),
            ["bundle_files"] = bundleFiles,
            ["publication_authorization"] = new JsonObject
            {
                ["mode"] = "hosted_qa",
                ["source_commit"] = recovery["source"]!["commit"]!.DeepClone(),
                ["checked_at_utc"] = "2026-08-26T11:59:00Z",
                ["default_branch"] = "main",
                ["default_branch_commit"] =
                    recovery["source"]!["commit"]!.DeepClone(),
                ["merged_pr_number"] = 1430,
                ["qa_check"] = "qa-gate",
                ["qa_check_url"] =
                    "https://github.com/Ensrick/vermintide-2-tweaker/actions/runs/1/job/2",
                ["qa_completed_at_utc"] = "2026-08-26T11:58:00Z"
            }
        };
        if (includeRecovery)
            row["recovery"] = recovery;
        rowMutate?.Invoke(row);

        var manifest = new JsonObject
        {
            ["manifest_schema"] = 2,
            ["release_tag"] = containerTag,
            ["published_at"] = "2026-08-26T12:00:00.0000000Z",
            ["mods"] = new JsonArray(row)
        };
        return Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static byte[] BuildLegacyManifest(
        string tag,
        int rowCount,
        bool includeSchema = true)
    {
        var rows = new JsonArray();
        for (var index = 0; index < rowCount; index++)
            rows.Add(new JsonObject { ["mod_id"] = $"legacy_{index}" });
        var manifest = new JsonObject
        {
            ["release_tag"] = tag,
            ["published_at"] = "2026-08-26T12:00:00.0000000Z",
            ["mods"] = rows
        };
        if (includeSchema)
            manifest.Insert(0, "manifest_schema", 2);
        return Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }

    private static RecoveryReleaseSummary EmptyRelease(long id) =>
        new(
            id,
            $"mods-empty-{id}",
            DateTimeOffset.Parse("2026-08-26T12:00:00Z").AddSeconds(id),
            false,
            false,
            Array.Empty<RecoveryReleaseAssetSummary>());

    private static RecoveryReleasePage Page(
        int pageNumber,
        bool hasNext,
        params RecoveryReleaseSummary[] releases) =>
        new(
            Repository,
            pageNumber,
            RecoveryHistoryResolver.ReleasesPerPage,
            $"\"etag-{pageNumber}\"",
            hasNext,
            Array.AsReadOnly(releases));

    private static string FixtureJson(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryRecords",
        name));

    private static byte[] ProducerManifestBytes(string name) => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryManifests",
        name));

    private static string AssetUrl(string releaseTag, string assetName) =>
        $"https://github.com/{Repository}/releases/download/{releaseTag}/{assetName}";

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeReleaseSource : IRecoveryReleaseSource
    {
        public Dictionary<int, RecoveryReleasePage> Pages { get; } = new();
        public Dictionary<(long ReleaseId, long AssetId), RecoveryManifestFetch> Manifests
        { get; } = new();
        public RecoveryPageRevalidation Revalidation { get; set; } =
            RecoveryPageRevalidation.Unchanged;
        public RecoveryReleaseSourceFailure? ThrowOnPage { get; set; }
        public Action? OnManifest { get; set; }
        public int PageCalls { get; private set; }
        public int ManifestCalls { get; private set; }
        public int RevalidationCalls { get; private set; }
        public List<int> AssetBudgets { get; } = new();
        public List<int> ManifestByteBudgets { get; } = new();

        public void SetSinglePage(params RecoveryReleaseSummary[] releases) =>
            Pages[1] = Page(1, false, releases);

        public Task<RecoveryReleasePage> GetReleasePageAsync(
            string repository,
            int pageNumber,
            int pageSize,
            int maximumAssets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageCalls++;
            AssetBudgets.Add(maximumAssets);
            if (ThrowOnPage is { } failure)
                throw new RecoveryReleaseSourceException(failure, "fixture source failure");
            if (!Pages.TryGetValue(pageNumber, out var page))
                page = Page(pageNumber, false);
            return Task.FromResult(page);
        }

        public Task<RecoveryPageRevalidation> RevalidateReleasePageAsync(
            string repository,
            int pageNumber,
            int pageSize,
            string entityTag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidationCalls++;
            return Task.FromResult(Revalidation);
        }

        public Task<RecoveryManifestFetch> GetManifestAsync(
            string repository,
            long releaseId,
            string releaseTag,
            long assetId,
            string assetName,
            string browserDownloadUrl,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManifestCalls++;
            ManifestByteBudgets.Add(maximumBytes);
            OnManifest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Manifests.TryGetValue((releaseId, assetId), out var fetch)
                ? fetch
                : RecoveryManifestFetch.Gone);
        }
    }
}
