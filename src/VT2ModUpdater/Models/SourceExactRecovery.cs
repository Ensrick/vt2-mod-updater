using VT2ModUpdater.Services;

namespace VT2ModUpdater.Models;

/// <summary>
/// One explicit source-exact recovery request. This is not a latest-version or
/// version-only lookup: the repository, mod, Workshop item, and exact source
/// commit are all mandatory authority coordinates.
/// </summary>
public sealed record SourceExactRecoveryRequest(
    string Repository,
    string ModId,
    string WorkshopId,
    string SourceCommit,
    string WorkshopContentRoot);

internal static class SourceExactRecoveryRequestContract
{
    internal static bool IsCanonicalSourceCommit(string? value) =>
        value is { Length: 40 } &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>
/// Terminal coordinator classification. Every invocation returns exactly one
/// state; caller cancellation is data rather than an escaping exception.
/// </summary>
public enum SourceExactRecoveryStatus
{
    Succeeded,
    InvalidRequest,
    ArtifactGone,
    BoundsExceeded,
    RemoteFailure,
    ContractFailure,
    RecoveryFailure,
    StageFailure,
    InstallFailure,
    Cancelled
}

/// <summary>
/// Stable machine-readable reason within a terminal coordinator state.
/// Phase prefixes preserve whether a disappearing artifact, remote error, or
/// contract refusal happened during resolution or during archive staging.
/// </summary>
public enum SourceExactRecoveryFailure
{
    None,
    InvalidRequest,
    InvalidRepository,
    InvalidModId,
    InvalidWorkshopId,
    InvalidSourceCommit,
    InvalidWorkshopContentRoot,
    ResolutionNoSourceExactRecord,
    ResolutionNoSurvivingArchive,
    ResolutionHistoryBoundExceeded,
    ResolutionManifestBoundExceeded,
    ResolutionRowBoundExceeded,
    ResolutionAssetBoundExceeded,
    ResolutionRemoteFailure,
    ResolutionContractFailure,
    StageArtifactGone,
    StageBoundExceeded,
    StageRemoteFailure,
    StageContractFailure,
    StageVerificationFailure,
    RecoveryTransactionFailure,
    InstallTransactionFailure,
    Cancelled
}

/// <summary>
/// Frozen result from the explicit coordinator. <see cref="TargetPath"/> is
/// populated only after the request passes pure validation and is always the
/// synthetic Workshop folder derived by <c>Deployer.GetSyntheticFolder</c>.
/// Successful outcomes also carry the resolved historical version so UI
/// read-back can bind the installed marker to the exact recovery proof.
/// </summary>
internal sealed record SourceExactRecoveryOutcome(
    SourceExactRecoveryStatus Status,
    SourceExactRecoveryFailure Failure,
    string Message,
    string? TargetPath = null,
    RecoveryResolutionEvidence? ResolutionEvidence = null,
    RecoveryResolutionFailure? ResolutionFailure = null,
    SourceExactStageFailure? StageFailure = null,
    SourceExactTransactionFailure? TransactionFailure = null,
    string? ResolvedVersion = null);
