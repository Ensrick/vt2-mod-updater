using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Disabled composition root for source-exact recovery. No production caller
/// constructs this type yet; it cannot enter the ordinary latest-update path.
/// </summary>
internal sealed class SourceExactRecoveryCoordinator : IDisposable
{
    private const int MaximumModIdLength = 128;
    private static readonly Regex ModIdPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9_-]*\\z",
        RegexOptions.CultureInvariant);

    private readonly ISourceExactRecoveryComposition _composition;
    private int _disposed;

    /// <summary>
    /// Composes only the reviewed bounded-history resolver, streaming stager,
    /// and journaled directory transaction. This constructor remains disabled
    /// because no application or UI call site references the coordinator.
    /// </summary>
    internal SourceExactRecoveryCoordinator()
        : this(new GitHubSourceExactRecoveryComposition()) { }

    /// <summary>Focused-test seam; the coordinator owns the composition.</summary>
    internal SourceExactRecoveryCoordinator(
        ISourceExactRecoveryComposition composition) =>
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));

    internal async Task<SourceExactRecoveryOutcome> RecoverAsync(
        SourceExactRecoveryRequest? request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var validation = ValidateRequest(request, out var validatedTarget);
        if (validation is not null)
            return validation;

        var target = validatedTarget!;
        var validRequest = request!;
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(target);

        // Crash recovery is a mandatory authorization boundary and therefore
        // precedes the resolver, every network request, and private staging.
        try
        {
            _composition.Recover(target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(target);
        }
        catch (SourceExactTransactionException ex)
        {
            return Failure(
                SourceExactRecoveryStatus.RecoveryFailure,
                SourceExactRecoveryFailure.RecoveryTransactionFailure,
                ex.Message,
                target,
                transactionFailure: ex.Failure);
        }
        catch (Exception ex)
        {
            return Failure(
                SourceExactRecoveryStatus.RecoveryFailure,
                SourceExactRecoveryFailure.RecoveryTransactionFailure,
                $"source-exact transaction recovery failed: {ex.Message}",
                target);
        }
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(target);

        var query = new RecoveryHistoryQuery(
            validRequest.Repository,
            validRequest.ModId,
            validRequest.WorkshopId,
            validRequest.SourceCommit);
        RecoveryHistoryResolution resolution;
        try
        {
            resolution = await _composition.ResolveAsync(
                query,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(target);
        }
        catch (RecoveryReleaseSourceException ex)
        {
            return MapReleaseSourceException(ex, target);
        }
        catch (Exception ex)
        {
            return Failure(
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure,
                $"source-exact history resolver escaped its typed boundary: {ex.Message}",
                target);
        }

        if (resolution is null)
        {
            return Failure(
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure,
                "source-exact history resolver returned no result",
                target);
        }
        if (resolution.Status != RecoveryResolutionStatus.SourceExactSurvivingArtifact)
            return MapResolutionFailure(resolution, target);
        if (resolution.Failure != RecoveryResolutionFailure.None ||
            resolution.Artifact is null)
        {
            return Failure(
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure,
                "source-exact history success omitted its exact surviving artifact",
                target,
                resolution.Evidence);
        }
        if (!ArtifactMatchesQuery(resolution.Artifact, query))
        {
            return Failure(
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure,
                "source-exact history artifact differs from the requested exact tuple",
                target,
                resolution.Evidence);
        }
        if (cancellationToken.IsCancellationRequested)
            return Cancelled(target, resolution.Evidence);

        ISourceExactRecoveryStageLease stage;
        try
        {
            stage = await _composition.StageAsync(
                resolution.Artifact,
                target,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(target, resolution.Evidence);
        }
        catch (SourceExactStageException ex)
        {
            return MapStageFailure(ex, target, resolution.Evidence);
        }
        catch (Exception ex)
        {
            return Failure(
                SourceExactRecoveryStatus.StageFailure,
                SourceExactRecoveryFailure.StageVerificationFailure,
                $"source-exact staging escaped its typed boundary: {ex.Message}",
                target,
                resolution.Evidence);
        }

        if (stage is null)
        {
            return Failure(
                SourceExactRecoveryStatus.StageFailure,
                SourceExactRecoveryFailure.StageVerificationFailure,
                "source-exact staging returned no lease",
                target,
                resolution.Evidence);
        }

        using (stage)
        {
            string leaseTarget;
            try
            {
                leaseTarget = stage.IntendedTargetPath;
            }
            catch (Exception ex)
            {
                return Failure(
                    SourceExactRecoveryStatus.ContractFailure,
                    SourceExactRecoveryFailure.StageContractFailure,
                    $"source-exact stage lease omitted its target authority: {ex.Message}",
                    target,
                    resolution.Evidence);
            }
            if (!string.Equals(leaseTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    SourceExactRecoveryStatus.ContractFailure,
                    SourceExactRecoveryFailure.StageContractFailure,
                    "source-exact stage lease targets a foreign directory",
                    target,
                    resolution.Evidence);
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _composition.Install(
                    stage,
                    resolution.Artifact,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(target, resolution.Evidence);
            }
            catch (SourceExactTransactionException ex)
            {
                return Failure(
                    SourceExactRecoveryStatus.InstallFailure,
                    SourceExactRecoveryFailure.InstallTransactionFailure,
                    ex.Message,
                    target,
                    resolution.Evidence,
                    transactionFailure: ex.Failure);
            }
            catch (Exception ex)
            {
                return Failure(
                    SourceExactRecoveryStatus.InstallFailure,
                    SourceExactRecoveryFailure.InstallTransactionFailure,
                    $"source-exact installation failed: {ex.Message}",
                    target,
                    resolution.Evidence);
            }
        }

        return new SourceExactRecoveryOutcome(
            SourceExactRecoveryStatus.Succeeded,
            SourceExactRecoveryFailure.None,
            "source-exact archive installed through the journaled directory transaction",
            target,
            resolution.Evidence);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _composition.Dispose();
    }

    private static SourceExactRecoveryOutcome? ValidateRequest(
        SourceExactRecoveryRequest? request,
        out string? target)
    {
        target = null;
        if (request is null)
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidRequest,
                "source-exact recovery request is required");
        }
        if (!string.Equals(
                request.Repository,
                RecoveryRecordContract.Repository,
                StringComparison.Ordinal))
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidRepository,
                $"repository must be exactly '{RecoveryRecordContract.Repository}'");
        }
        if (string.IsNullOrWhiteSpace(request.ModId) ||
            request.ModId.Length > MaximumModIdLength ||
            !string.Equals(request.ModId, request.ModId.Trim(), StringComparison.Ordinal) ||
            !ModIdPattern.IsMatch(request.ModId))
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidModId,
                "mod_id is not canonical");
        }
        if (string.IsNullOrEmpty(request.WorkshopId) ||
            request.WorkshopId.Length > 20 ||
            request.WorkshopId[0] == '0' ||
            request.WorkshopId.Any(character => character is < '0' or > '9') ||
            !ulong.TryParse(
                request.WorkshopId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var workshopId) ||
            workshopId == 0)
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidWorkshopId,
                "workshop_id must be a canonical positive UInt64 string");
        }
        if (request.SourceCommit is null || request.SourceCommit.Length != 40 ||
            request.SourceCommit.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidSourceCommit,
                "source_commit must be exactly 40 lowercase hexadecimal characters");
        }
        if (string.IsNullOrWhiteSpace(request.WorkshopContentRoot) ||
            !Path.IsPathFullyQualified(request.WorkshopContentRoot))
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidWorkshopContentRoot,
                "Workshop content root must be a fully qualified path");
        }

        try
        {
            var workshopRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(request.WorkshopContentRoot));
            target = Deployer.GetSyntheticFolder(workshopRoot, request.WorkshopId);
        }
        catch (DeployException)
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidWorkshopId,
                "workshop_id must identify a real, not synthetic, Workshop item");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return Invalid(
                SourceExactRecoveryFailure.InvalidWorkshopContentRoot,
                "Workshop content root is not a canonical filesystem path");
        }

        return null;
    }

    private static bool ArtifactMatchesQuery(
        SourceExactRecoveryArtifact artifact,
        RecoveryHistoryQuery query)
    {
        var record = artifact.Proof?.Record;
        return record?.Source is not null &&
            string.Equals(artifact.Repository, query.Repository, StringComparison.Ordinal) &&
            string.Equals(record.ModId, query.ModId, StringComparison.Ordinal) &&
            string.Equals(record.WorkshopId, query.WorkshopId, StringComparison.Ordinal) &&
            string.Equals(record.Source.Commit, query.SourceCommit, StringComparison.Ordinal);
    }

    private static SourceExactRecoveryOutcome MapResolutionFailure(
        RecoveryHistoryResolution resolution,
        string target)
    {
        var mapping = (resolution.Status, resolution.Failure) switch
        {
            (RecoveryResolutionStatus.ArtifactGone,
                RecoveryResolutionFailure.NoSourceExactRecord) => (
                SourceExactRecoveryStatus.ArtifactGone,
                SourceExactRecoveryFailure.ResolutionNoSourceExactRecord),
            (RecoveryResolutionStatus.ArtifactGone,
                RecoveryResolutionFailure.NoSurvivingArchive) => (
                SourceExactRecoveryStatus.ArtifactGone,
                SourceExactRecoveryFailure.ResolutionNoSurvivingArchive),
            (RecoveryResolutionStatus.BoundedExhaustion,
                RecoveryResolutionFailure.HistoryBoundExceeded) => (
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionHistoryBoundExceeded),
            (RecoveryResolutionStatus.BoundedExhaustion,
                RecoveryResolutionFailure.ManifestBoundExceeded) => (
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionManifestBoundExceeded),
            (RecoveryResolutionStatus.BoundedExhaustion,
                RecoveryResolutionFailure.RowBoundExceeded) => (
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionRowBoundExceeded),
            (RecoveryResolutionStatus.BoundedExhaustion,
                RecoveryResolutionFailure.AssetBoundExceeded) => (
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionAssetBoundExceeded),
            (RecoveryResolutionStatus.RemoteFailure,
                RecoveryResolutionFailure.RemoteUnavailable or
                RecoveryResolutionFailure.HistoryChangedDuringScan) => (
                SourceExactRecoveryStatus.RemoteFailure,
                SourceExactRecoveryFailure.ResolutionRemoteFailure),
            (RecoveryResolutionStatus.ContractFailure, _) => (
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure),
            _ => (
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure)
        };
        var (status, failure) = mapping;
        return Failure(
            status,
            failure,
            resolution.Message,
            target,
            resolution.Evidence,
            resolutionFailure: resolution.Failure);
    }

    private static SourceExactRecoveryOutcome MapReleaseSourceException(
        RecoveryReleaseSourceException exception,
        string target) => exception.Failure switch
        {
            RecoveryReleaseSourceFailure.HistoryBoundExceeded => Failure(
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionHistoryBoundExceeded,
                exception.Message,
                target),
            RecoveryReleaseSourceFailure.ManifestBoundExceeded => Failure(
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionManifestBoundExceeded,
                exception.Message,
                target),
            RecoveryReleaseSourceFailure.AssetBoundExceeded => Failure(
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.ResolutionAssetBoundExceeded,
                exception.Message,
                target),
            RecoveryReleaseSourceFailure.Remote => Failure(
                SourceExactRecoveryStatus.RemoteFailure,
                SourceExactRecoveryFailure.ResolutionRemoteFailure,
                exception.Message,
                target),
            _ => Failure(
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure,
                exception.Message,
                target)
        };

    private static SourceExactRecoveryOutcome MapStageFailure(
        SourceExactStageException exception,
        string target,
        RecoveryResolutionEvidence evidence)
    {
        var (status, failure) = exception.Failure switch
        {
            SourceExactStageFailure.ArtifactGone => (
                SourceExactRecoveryStatus.ArtifactGone,
                SourceExactRecoveryFailure.StageArtifactGone),
            SourceExactStageFailure.CompressedLimitExceeded or
                SourceExactStageFailure.EntryLimitExceeded or
                SourceExactStageFailure.OutputLimitExceeded => (
                    SourceExactRecoveryStatus.BoundsExceeded,
                    SourceExactRecoveryFailure.StageBoundExceeded),
            SourceExactStageFailure.Remote => (
                SourceExactRecoveryStatus.RemoteFailure,
                SourceExactRecoveryFailure.StageRemoteFailure),
            SourceExactStageFailure.InvalidArtifact or
                SourceExactStageFailure.ProofDrift or
                SourceExactStageFailure.InvalidTarget => (
                    SourceExactRecoveryStatus.ContractFailure,
                    SourceExactRecoveryFailure.StageContractFailure),
            _ => (
                SourceExactRecoveryStatus.StageFailure,
                SourceExactRecoveryFailure.StageVerificationFailure)
        };
        return Failure(
            status,
            failure,
            exception.Message,
            target,
            evidence,
            stageFailure: exception.Failure);
    }

    private static SourceExactRecoveryOutcome Invalid(
        SourceExactRecoveryFailure failure,
        string message) => new(
            SourceExactRecoveryStatus.InvalidRequest,
            failure,
            message);

    private static SourceExactRecoveryOutcome Cancelled(
        string target,
        RecoveryResolutionEvidence? evidence = null) => new(
            SourceExactRecoveryStatus.Cancelled,
            SourceExactRecoveryFailure.Cancelled,
            "source-exact recovery was cancelled",
            target,
            evidence);

    private static SourceExactRecoveryOutcome Failure(
        SourceExactRecoveryStatus status,
        SourceExactRecoveryFailure failure,
        string message,
        string target,
        RecoveryResolutionEvidence? evidence = null,
        RecoveryResolutionFailure? resolutionFailure = null,
        SourceExactStageFailure? stageFailure = null,
        SourceExactTransactionFailure? transactionFailure = null) => new(
            status,
            failure,
            message,
            target,
            evidence,
            resolutionFailure,
            stageFailure,
            transactionFailure);
}

/// <summary>
/// The complete coordinator dependency surface. There is intentionally no
/// latest-release client, legacy deployer, fallback selector, or Workshop
/// mutation operation on this interface.
/// </summary>
internal interface ISourceExactRecoveryComposition : IDisposable
{
    void Recover(string targetPath);

    Task<RecoveryHistoryResolution> ResolveAsync(
        RecoveryHistoryQuery query,
        CancellationToken cancellationToken);

    Task<ISourceExactRecoveryStageLease> StageAsync(
        SourceExactRecoveryArtifact artifact,
        string targetPath,
        CancellationToken cancellationToken);

    void Install(
        ISourceExactRecoveryStageLease stage,
        SourceExactRecoveryArtifact artifact,
        CancellationToken cancellationToken);
}

/// <summary>
/// One disposable staging lease. Production wraps exactly one Phase 3 stage;
/// tests can prove cancellation cleanup without fabricating filesystem state.
/// </summary>
internal interface ISourceExactRecoveryStageLease : IDisposable
{
    string IntendedTargetPath { get; }
}

/// <summary>Exact production composition, presently unreachable by the app.</summary>
internal sealed class GitHubSourceExactRecoveryComposition :
    ISourceExactRecoveryComposition
{
    private readonly GitHubRecoveryReleaseSource _releaseSource = new();
    private readonly GitHubSourceExactArchiveSource _archiveSource = new();
    private readonly RecoveryHistoryResolver _resolver;
    private readonly SourceExactZipStager _stager;
    private readonly SourceExactDirectoryTransaction _transaction = new();
    private int _disposed;

    internal GitHubSourceExactRecoveryComposition()
    {
        _resolver = new RecoveryHistoryResolver(_releaseSource);
        _stager = new SourceExactZipStager(_archiveSource);
    }

    public void Recover(string targetPath) => _transaction.Recover(targetPath);

    public Task<RecoveryHistoryResolution> ResolveAsync(
        RecoveryHistoryQuery query,
        CancellationToken cancellationToken) =>
        _resolver.ResolveAsync(query, cancellationToken);

    public async Task<ISourceExactRecoveryStageLease> StageAsync(
        SourceExactRecoveryArtifact artifact,
        string targetPath,
        CancellationToken cancellationToken) =>
        new SourceExactRecoveryStageLease(await _stager.StageAsync(
            artifact,
            targetPath,
            cancellationToken).ConfigureAwait(false));

    public void Install(
        ISourceExactRecoveryStageLease stage,
        SourceExactRecoveryArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (stage is not SourceExactRecoveryStageLease productionStage)
        {
            throw new InvalidOperationException(
                "source-exact production composition received a foreign stage lease");
        }
        _transaction.Install(
            productionStage.BeginInstall(),
            artifact,
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _archiveSource.Dispose();
        _releaseSource.Dispose();
    }

    private sealed class SourceExactRecoveryStageLease :
        ISourceExactRecoveryStageLease
    {
        private readonly SourceExactZipStage _stage;
        private int _installAttempted;
        private int _disposed;

        internal SourceExactRecoveryStageLease(SourceExactZipStage stage) =>
            _stage = stage ?? throw new ArgumentNullException(nameof(stage));

        public string IntendedTargetPath => _stage.IntendedTargetPath;

        internal SourceExactZipStage BeginInstall()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Interlocked.Exchange(ref _installAttempted, 1) != 0)
            {
                throw new InvalidOperationException(
                    "source-exact stage lease may be installed only once");
            }
            return _stage;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _stage.Dispose();
        }
    }
}
