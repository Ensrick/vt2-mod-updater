using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Performs a complete bounded scan of published release metadata and source-
/// exact recovery children. It never downloads a ZIP and never touches the
/// filesystem or the ordinary latest-update path.
/// </summary>
public sealed class RecoveryHistoryResolver
{
    public const int MaximumPages = 5;
    public const int ReleasesPerPage = 100;
    public const int MaximumManifestBytes = 1024 * 1024;
    public const long MaximumAggregateManifestBytes = 64L * 1024 * 1024;
    public const int MaximumRowsPerRelease = 256;
    public const int MaximumTotalRows = 16_384;
    public const int MaximumAssetsPerRelease = 512;
    public const int MaximumTotalAssets = 32_768;
    public const long MaximumDeclaredAssetBytes = 256L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ModIdPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9_-]*\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseTagPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant);

    private readonly IRecoveryReleaseSource _source;

    public RecoveryHistoryResolver(IRecoveryReleaseSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<RecoveryHistoryResolution> ResolveAsync(
        RecoveryHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var evidence = new MutableEvidence();

        if (!TryValidateQuery(query, out var queryError))
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.InvalidQuery,
                queryError,
                evidence);
        }

        try
        {
            var pages = new List<RecoveryReleasePage>(MaximumPages);
            var releases = new List<RecoveryReleaseSummary>();
            var releaseIds = new HashSet<long>();
            var releaseTags = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var assetIds = new HashSet<long>();

            for (var pageNumber = 1; pageNumber <= MaximumPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _source.GetReleasePageAsync(
                    query.Repository,
                    pageNumber,
                    ReleasesPerPage,
                    MaximumTotalAssets - evidence.AssetsScanned,
                    cancellationToken).ConfigureAwait(false);

                var pageFailure = ValidatePage(
                    page,
                    query.Repository,
                    pageNumber,
                    evidence,
                    releaseIds,
                    releaseTags,
                    assetIds);
                if (pageFailure is not null)
                    return pageFailure;

                pages.Add(page);
                releases.AddRange(page.Releases);
                evidence.PagesScanned++;
                evidence.ReleasesScanned += page.Releases.Count;

                if (!page.HasNextPage)
                    break;
                if (pageNumber == MaximumPages)
                {
                    return Failure(
                        RecoveryResolutionStatus.BoundedExhaustion,
                        RecoveryResolutionFailure.HistoryBoundExceeded,
                        $"release history exceeds {MaximumPages} pages of " +
                        $"{ReleasesPerPage} releases",
                        evidence);
                }
            }

            var matchingRecords = new List<ValidatedRecoveryRecord>();
            var surviving = new List<SurvivingCandidate>();

            foreach (var release in releases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (release.Draft)
                    continue;

                var manifestLookup = FindExactAsset(release, "manifest.json");
                if (manifestLookup.Failure is not null)
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedReleaseMetadata,
                        manifestLookup.Failure,
                        evidence);
                }
                if (manifestLookup.Asset is null)
                    continue;

                var manifestAsset = manifestLookup.Asset;
                if (manifestAsset.Size is <= 0 or > MaximumManifestBytes)
                {
                    return Failure(
                        RecoveryResolutionStatus.BoundedExhaustion,
                        RecoveryResolutionFailure.ManifestBoundExceeded,
                        $"release {release.Id} manifest size {manifestAsset.Size} is outside " +
                        $"the 1..{MaximumManifestBytes} byte bound",
                        evidence);
                }
                if (evidence.ManifestBytesRead >
                    MaximumAggregateManifestBytes - manifestAsset.Size)
                {
                    return Failure(
                        RecoveryResolutionStatus.BoundedExhaustion,
                        RecoveryResolutionFailure.ManifestBoundExceeded,
                        $"manifest history exceeds the {MaximumAggregateManifestBytes}-byte " +
                        "aggregate bound",
                        evidence);
                }
                var remainingManifestBytes =
                    MaximumAggregateManifestBytes - evidence.ManifestBytesRead;
                var manifestReadBound = checked((int)Math.Min(
                    manifestAsset.Size,
                    remainingManifestBytes));
                var fetch = await _source.GetManifestAsync(
                    query.Repository,
                    release.Id,
                    release.TagName,
                    manifestAsset.Id,
                    manifestAsset.Name,
                    manifestAsset.BrowserDownloadUrl,
                    manifestReadBound,
                    cancellationToken).ConfigureAwait(false);
                if (fetch is null)
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedReleaseMetadata,
                        $"release {release.Id} returned null manifest metadata",
                        evidence);
                }
                if (fetch.Status == RecoveryManifestFetchStatus.Gone)
                    continue;
                if (fetch.Status != RecoveryManifestFetchStatus.Found)
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedReleaseMetadata,
                        $"release {release.Id} returned an unsupported manifest fetch state",
                        evidence);
                }
                var remainingRows = MaximumTotalRows - evidence.RowsScanned;
                if (remainingRows <= 0)
                {
                    return Failure(
                        RecoveryResolutionStatus.BoundedExhaustion,
                        RecoveryResolutionFailure.RowBoundExceeded,
                        $"manifest history exceeds the {MaximumTotalRows}-row aggregate bound",
                        evidence);
                }
                var maximumRowsForManifest = Math.Min(MaximumRowsPerRelease, remainingRows);
                if (fetch.Bytes.Length != manifestAsset.Size)
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.TamperedArtifactCoordinate,
                        $"release {release.Id} manifest bytes differ from declared asset length",
                        evidence);
                }
                var manifestSha256 = Convert.ToHexString(
                    SHA256.HashData(fetch.Bytes.Span)).ToLowerInvariant();
                if (!string.Equals(
                        manifestSha256,
                        manifestAsset.DigestSha256,
                        StringComparison.Ordinal))
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.TamperedArtifactCoordinate,
                        $"release {release.Id} manifest bytes differ from the exact asset digest",
                        evidence);
                }

                evidence.ManifestsRead++;
                evidence.ManifestBytesRead += fetch.Bytes.Length;

                RecoveryManifestScan scan;
                try
                {
                    scan = RecoveryManifestContract.ParseAndValidate(
                        fetch.Bytes,
                        release.TagName,
                        maximumRowsForManifest,
                        cancellationToken);
                }
                catch (RecoveryManifestBoundException ex)
                {
                    return Failure(
                        RecoveryResolutionStatus.BoundedExhaustion,
                        RecoveryResolutionFailure.RowBoundExceeded,
                        ex.Message,
                        evidence);
                }
                catch (RecoveryManifestValidationException ex)
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedManifest,
                        $"release {release.Id} manifest failed validation: {ex.Message}",
                        evidence);
                }

                evidence.RowsScanned += scan.RowCount;

                foreach (var record in scan.RecoveryRecords)
                {
                    var proof = record.Record;
                    if (!string.Equals(proof.ModId, query.ModId, StringComparison.Ordinal) ||
                        !string.Equals(
                            proof.WorkshopId,
                            query.WorkshopId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            proof.Source.Commit,
                            query.SourceCommit,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchingRecords.Add(record);
                    evidence.MatchingRecords++;

                    var archiveLookup = FindExactAsset(release, proof.Asset.Filename);
                    if (archiveLookup.Failure is not null)
                    {
                        return Failure(
                            RecoveryResolutionStatus.ContractFailure,
                            RecoveryResolutionFailure.TamperedArtifactCoordinate,
                            archiveLookup.Failure,
                            evidence);
                    }
                    if (archiveLookup.Asset is null)
                        continue;
                    if (archiveLookup.Asset.Size != proof.Asset.Length)
                    {
                        return Failure(
                            RecoveryResolutionStatus.ContractFailure,
                            RecoveryResolutionFailure.TamperedArtifactCoordinate,
                            $"release {release.Id} asset '{proof.Asset.Filename}' length " +
                            $"{archiveLookup.Asset.Size} differs from recovery length " +
                            $"{proof.Asset.Length}",
                            evidence);
                    }
                    if (!string.Equals(
                            archiveLookup.Asset.DigestSha256,
                            proof.Asset.Sha256,
                            StringComparison.Ordinal))
                    {
                        return Failure(
                            RecoveryResolutionStatus.ContractFailure,
                            RecoveryResolutionFailure.TamperedArtifactCoordinate,
                            $"release {release.Id} asset '{proof.Asset.Filename}' GitHub digest " +
                            "differs from the recovery SHA-256",
                            evidence);
                    }

                    surviving.Add(new SurvivingCandidate(
                        release,
                        archiveLookup.Asset,
                        record));
                    evidence.SurvivingCoordinates++;
                }
            }

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var revalidation = await _source.RevalidateReleasePageAsync(
                    query.Repository,
                    page.PageNumber,
                    page.PageSize,
                    page.EntityTag,
                    cancellationToken).ConfigureAwait(false);
                if (revalidation != RecoveryPageRevalidation.Unchanged)
                {
                    return Failure(
                        RecoveryResolutionStatus.RemoteFailure,
                        RecoveryResolutionFailure.HistoryChangedDuringScan,
                        "release history changed while the bounded scan was in progress",
                        evidence);
                }
            }

            var semanticClasses = matchingRecords
                .Select(record => record.SemanticEquivalenceSha256)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (semanticClasses.Length > 1)
            {
                return Failure(
                    RecoveryResolutionStatus.ContractFailure,
                    RecoveryResolutionFailure.AmbiguousSemanticProof,
                    "the exact source tuple has more than one semantic recovery proof",
                    evidence);
            }
            if (matchingRecords.Count == 0)
            {
                return Failure(
                    RecoveryResolutionStatus.ArtifactGone,
                    RecoveryResolutionFailure.NoSourceExactRecord,
                    "no source-exact recovery record survives in the bounded complete history",
                    evidence);
            }
            if (surviving.Count == 0)
            {
                return Failure(
                    RecoveryResolutionStatus.ArtifactGone,
                    RecoveryResolutionFailure.NoSurvivingArchive,
                    "source-exact recovery records survive, but every exact ZIP asset is gone",
                    evidence);
            }

            // The audited history carried one exact tuple through as many as
            // 27 releases without semantic or ZIP drift. Only after proving a
            // single class across the complete bounded scan do we choose the
            // newest published surviving copy; numeric IDs make timestamp ties
            // deterministic without falling back to the latest release.
            var selected = surviving
                .OrderByDescending(candidate => candidate.Release.PublishedAt!.Value)
                .ThenByDescending(candidate => candidate.Release.Id)
                .ThenByDescending(candidate => candidate.Asset.Id)
                .ThenBy(candidate => candidate.Release.TagName, StringComparer.Ordinal)
                .First();
            var selectedProof = selected.Proof.Record;
            var artifact = new SourceExactRecoveryArtifact(
                query.Repository,
                selectedProof.Release.Tag,
                selected.Release.Id,
                selected.Release.TagName,
                selected.Release.PublishedAt!.Value,
                selected.Asset.Id,
                selected.Asset.Name,
                selected.Asset.Size,
                selectedProof.Asset.Sha256,
                selected.Asset.BrowserDownloadUrl,
                selected.Proof,
                matchingRecords.Count,
                surviving.Count);

            return new RecoveryHistoryResolution(
                RecoveryResolutionStatus.SourceExactSurvivingArtifact,
                RecoveryResolutionFailure.None,
                "one source-exact semantic proof has a surviving hosted ZIP coordinate",
                evidence.Freeze(),
                artifact);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecoveryReleaseSourceException ex)
        {
            return ex.Failure switch
            {
                RecoveryReleaseSourceFailure.HistoryBoundExceeded => Failure(
                    RecoveryResolutionStatus.BoundedExhaustion,
                    RecoveryResolutionFailure.HistoryBoundExceeded,
                    ex.Message,
                    evidence),
                RecoveryReleaseSourceFailure.ManifestBoundExceeded => Failure(
                    RecoveryResolutionStatus.BoundedExhaustion,
                    RecoveryResolutionFailure.ManifestBoundExceeded,
                    ex.Message,
                    evidence),
                RecoveryReleaseSourceFailure.AssetBoundExceeded => Failure(
                    RecoveryResolutionStatus.BoundedExhaustion,
                    RecoveryResolutionFailure.AssetBoundExceeded,
                    ex.Message,
                    evidence),
                RecoveryReleaseSourceFailure.Contract => Failure(
                    RecoveryResolutionStatus.ContractFailure,
                    RecoveryResolutionFailure.MalformedReleaseMetadata,
                    ex.Message,
                    evidence),
                _ => Failure(
                    RecoveryResolutionStatus.RemoteFailure,
                    RecoveryResolutionFailure.RemoteUnavailable,
                    ex.Message,
                    evidence)
            };
        }
    }

    private static RecoveryHistoryResolution? ValidatePage(
        RecoveryReleasePage? page,
        string expectedRepository,
        int expectedPage,
        MutableEvidence evidence,
        HashSet<long> releaseIds,
        Dictionary<string, long> releaseTags,
        HashSet<long> assetIds)
    {
        if (page is null)
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.MalformedReleaseMetadata,
                "release source returned null page metadata",
                evidence);
        }
        if (!string.Equals(page.Repository, expectedRepository, StringComparison.Ordinal) ||
            page.PageNumber != expectedPage ||
            page.PageSize != ReleasesPerPage)
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.MalformedReleaseMetadata,
                "release source returned foreign or non-sequential page identity",
                evidence);
        }
        if (!IsCanonicalBoundedText(page.EntityTag, 256) ||
            !EntityTagHeaderValue.TryParse(page.EntityTag, out var parsedEntityTag) ||
            string.Equals(parsedEntityTag.Tag, "*", StringComparison.Ordinal))
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.MalformedReleaseMetadata,
                "release page is missing a bounded canonical ETag",
                evidence);
        }
        if (page.Releases is null)
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.MalformedReleaseMetadata,
                "release page has a null release collection",
                evidence);
        }
        if (page.Releases.Count > ReleasesPerPage)
        {
            return Failure(
                RecoveryResolutionStatus.BoundedExhaustion,
                RecoveryResolutionFailure.HistoryBoundExceeded,
                $"release page exceeds the {ReleasesPerPage}-release bound",
                evidence);
        }
        if (page.HasNextPage && page.Releases.Count != ReleasesPerPage)
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.MalformedReleaseMetadata,
                "release pagination claims another page before a full page boundary",
                evidence);
        }
        if (!page.HasNextPage && page.Releases.Count == ReleasesPerPage)
        {
            return Failure(
                RecoveryResolutionStatus.ContractFailure,
                RecoveryResolutionFailure.MalformedReleaseMetadata,
                "a full release page lacks a validated next-page relation",
                evidence);
        }

        var remainingAssets = MaximumTotalAssets - evidence.AssetsScanned;
        var pageAssetCount = 0;
        foreach (var release in page.Releases)
        {
            if (release?.Assets is null)
            {
                return Failure(
                    RecoveryResolutionStatus.ContractFailure,
                    RecoveryResolutionFailure.MalformedReleaseMetadata,
                    "release metadata contains a null release or asset collection",
                    evidence);
            }
            if (release.Assets.Count > MaximumAssetsPerRelease)
            {
                return Failure(
                    RecoveryResolutionStatus.BoundedExhaustion,
                    RecoveryResolutionFailure.AssetBoundExceeded,
                    $"release {release.Id} exceeds the {MaximumAssetsPerRelease}-asset bound",
                    evidence);
            }
            if (pageAssetCount > remainingAssets - release.Assets.Count)
            {
                return Failure(
                    RecoveryResolutionStatus.BoundedExhaustion,
                    RecoveryResolutionFailure.AssetBoundExceeded,
                    $"release history exceeds the {MaximumTotalAssets}-asset aggregate bound",
                    evidence);
            }
            pageAssetCount += release.Assets.Count;
        }

        foreach (var release in page.Releases)
        {
            if (release is null || release.Id <= 0 ||
                !IsCanonicalBoundedText(release.TagName, 128) ||
                !ReleaseTagPattern.IsMatch(release.TagName) ||
                (!release.Draft &&
                 (release.PublishedAt is null ||
                  release.PublishedAt.Value.Offset != TimeSpan.Zero)) ||
                release.Assets is null)
            {
                return Failure(
                    RecoveryResolutionStatus.ContractFailure,
                    RecoveryResolutionFailure.MalformedReleaseMetadata,
                    "release metadata contains a non-canonical identity",
                    evidence);
            }
            if (!releaseIds.Add(release.Id))
            {
                return Failure(
                    RecoveryResolutionStatus.ContractFailure,
                    RecoveryResolutionFailure.MalformedReleaseMetadata,
                    $"release id {release.Id} is repeated across pagination",
                    evidence);
            }
            if (releaseTags.TryGetValue(release.TagName, out var priorId))
            {
                return Failure(
                    RecoveryResolutionStatus.ContractFailure,
                    RecoveryResolutionFailure.MalformedReleaseMetadata,
                    $"release tags '{release.TagName}' collide between ids {priorId} and " +
                    $"{release.Id}",
                    evidence);
            }
            releaseTags.Add(release.TagName, release.Id);

            var assetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in release.Assets)
            {
                if (asset is null || asset.Id <= 0 || asset.Size < 0 ||
                    !IsCanonicalBoundedText(asset.Name, 256) ||
                    !IsLowerSha256(asset.DigestSha256) ||
                    !IsExactBrowserDownloadUrl(
                        asset.BrowserDownloadUrl,
                        expectedRepository,
                        release.TagName,
                        asset.Name))
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedReleaseMetadata,
                        $"release {release.Id} contains a non-canonical asset coordinate",
                        evidence);
                }
                if (asset.Size > MaximumDeclaredAssetBytes)
                {
                    return Failure(
                        RecoveryResolutionStatus.BoundedExhaustion,
                        RecoveryResolutionFailure.AssetBoundExceeded,
                        $"release {release.Id} asset '{asset.Name}' exceeds the " +
                        $"{MaximumDeclaredAssetBytes}-byte declared-size bound",
                        evidence);
                }
                if (!assetIds.Add(asset.Id))
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedReleaseMetadata,
                        $"release history repeats asset id {asset.Id}",
                        evidence);
                }
                if (assetNames.TryGetValue(asset.Name, out var priorName))
                {
                    return Failure(
                        RecoveryResolutionStatus.ContractFailure,
                        RecoveryResolutionFailure.MalformedReleaseMetadata,
                        $"release {release.Id} contains case-colliding assets " +
                        $"'{priorName}' and '{asset.Name}'",
                        evidence);
                }
                assetNames.Add(asset.Name, asset.Name);
            }
            evidence.AssetsScanned += release.Assets.Count;
        }

        return null;
    }

    private static AssetLookup FindExactAsset(
        RecoveryReleaseSummary release,
        string expectedName)
    {
        RecoveryReleaseAssetSummary? exact = null;
        RecoveryReleaseAssetSummary? wrongCase = null;
        foreach (var asset in release.Assets)
        {
            if (string.Equals(asset.Name, expectedName, StringComparison.Ordinal))
                exact = asset;
            else if (string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                wrongCase = asset;
        }

        if (exact is not null && wrongCase is not null)
        {
            return new AssetLookup(
                null,
                $"release {release.Id} has both exact and case-colliding '{expectedName}' assets");
        }
        if (exact is null && wrongCase is not null)
        {
            return new AssetLookup(
                null,
                $"release {release.Id} has wrong-case asset '{wrongCase.Name}' instead of " +
                $"'{expectedName}'");
        }
        return new AssetLookup(exact, null);
    }

    private static bool TryValidateQuery(
        RecoveryHistoryQuery query,
        out string error)
    {
        if (!string.Equals(
                query.Repository,
                RecoveryRecordContract.Repository,
                StringComparison.Ordinal))
        {
            error = $"repository must be exactly '{RecoveryRecordContract.Repository}'";
            return false;
        }
        if (!IsCanonicalBoundedText(query.ModId, 128) ||
            !ModIdPattern.IsMatch(query.ModId))
        {
            error = "mod_id is not canonical";
            return false;
        }
        if (string.IsNullOrEmpty(query.WorkshopId) || query.WorkshopId.Length > 20 ||
            query.WorkshopId[0] == '0' ||
            query.WorkshopId.Any(character => character is < '0' or > '9') ||
            !ulong.TryParse(
                query.WorkshopId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var workshopId) ||
            workshopId == 0)
        {
            error = "workshop_id must be a canonical positive UInt64 string";
            return false;
        }
        if (query.SourceCommit is null || query.SourceCommit.Length != 40 ||
            query.SourceCommit.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            error = "source_commit must be exactly 40 lowercase hexadecimal characters";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsExactBrowserDownloadUrl(
        string? value,
        string repository,
        string releaseTag,
        string assetName)
    {
        if (!IsCanonicalBoundedText(value, 2048) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var actual))
        {
            return false;
        }
        var expected = new Uri(
            $"https://github.com/{repository}/releases/download/" +
            Uri.EscapeDataString(releaseTag) + "/" + Uri.EscapeDataString(assetName),
            UriKind.Absolute);
        return string.Equals(
            actual.AbsoluteUri,
            expected.AbsoluteUri,
            StringComparison.Ordinal);
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsCanonicalBoundedText(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumUtf8Bytes ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return false;
        }
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static RecoveryHistoryResolution Failure(
        RecoveryResolutionStatus status,
        RecoveryResolutionFailure failure,
        string message,
        MutableEvidence evidence) =>
        new(status, failure, message, evidence.Freeze());

    private sealed class MutableEvidence
    {
        public int PagesScanned { get; set; }
        public int ReleasesScanned { get; set; }
        public int AssetsScanned { get; set; }
        public int ManifestsRead { get; set; }
        public long ManifestBytesRead { get; set; }
        public int RowsScanned { get; set; }
        public int MatchingRecords { get; set; }
        public int SurvivingCoordinates { get; set; }

        public RecoveryResolutionEvidence Freeze() => new(
            PagesScanned,
            ReleasesScanned,
            AssetsScanned,
            ManifestsRead,
            ManifestBytesRead,
            RowsScanned,
            MatchingRecords,
            SurvivingCoordinates);
    }

    private sealed record AssetLookup(
        RecoveryReleaseAssetSummary? Asset,
        string? Failure);

    private sealed record SurvivingCandidate(
        RecoveryReleaseSummary Release,
        RecoveryReleaseAssetSummary Asset,
        ValidatedRecoveryRecord Proof);
}
