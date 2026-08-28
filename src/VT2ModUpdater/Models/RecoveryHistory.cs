namespace VT2ModUpdater.Models;

/// <summary>
/// Terminal classification for a bounded, read-only source-exact history lookup.
/// A successful lookup proves only that one matching archive still has a hosted
/// coordinate; archive bytes are verified by a later consumer phase.
/// </summary>
public enum RecoveryResolutionStatus
{
    SourceExactSurvivingArtifact,
    ArtifactGone,
    BoundedExhaustion,
    RemoteFailure,
    ContractFailure
}

public enum RecoveryResolutionFailure
{
    None,
    NoSourceExactRecord,
    NoSurvivingArchive,
    HistoryBoundExceeded,
    ManifestBoundExceeded,
    RowBoundExceeded,
    AssetBoundExceeded,
    RemoteUnavailable,
    HistoryChangedDuringScan,
    MalformedReleaseMetadata,
    MalformedManifest,
    AmbiguousSemanticProof,
    TamperedArtifactCoordinate,
    InvalidQuery
}

/// <summary>
/// The only supported source-exact lookup key. Every field is compared using
/// ordinal semantics; version-only or latest-release fallbacks do not exist.
/// </summary>
public sealed record RecoveryHistoryQuery(
    string Repository,
    string ModId,
    string WorkshopId,
    string SourceCommit);

/// <summary>
/// A surviving hosted coordinate selected from one semantic equivalence class.
/// <see cref="OriginReleaseTag"/> comes from the signed recovery child; the
/// container release and numeric IDs identify the exact currently hosted copy.
/// </summary>
public sealed record SourceExactRecoveryArtifact(
    string Repository,
    string OriginReleaseTag,
    long ContainerReleaseId,
    string ContainerReleaseTag,
    DateTimeOffset ContainerPublishedAt,
    long AssetId,
    string AssetFilename,
    long AssetLength,
    string AssetSha256,
    string AssetDownloadUrl,
    ValidatedRecoveryRecord Proof,
    int EquivalentRecordCount,
    int SurvivingCoordinateCount);

public sealed record RecoveryResolutionEvidence(
    int PagesScanned,
    int ReleasesScanned,
    int AssetsScanned,
    int ManifestsRead,
    long ManifestBytesRead,
    int RowsScanned,
    int MatchingRecords,
    int SurvivingCoordinates);

public sealed record RecoveryHistoryResolution(
    RecoveryResolutionStatus Status,
    RecoveryResolutionFailure Failure,
    string Message,
    RecoveryResolutionEvidence Evidence,
    SourceExactRecoveryArtifact? Artifact = null);
