using System.IO;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public sealed class SourceExactRecoveryCoordinatorTests
{
    private const string Repository = "Ensrick/vermintide-2-tweaker";
    private const string ModId = "modx";
    private const string WorkshopId = "3712929235";
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Blob = "0123456789abcdef0123456789abcdef01234567";

    private static readonly RecoveryResolutionEvidence Evidence = new(
        1,
        3,
        7,
        2,
        1024,
        4,
        1,
        1);

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidRequestReturnsTypedFailureBeforeAnyIo(
        SourceExactRecoveryRequest? request,
        SourceExactRecoveryFailure expectedFailure)
    {
        var composition = new RecordingComposition();
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(request);

        Assert.Equal(SourceExactRecoveryStatus.InvalidRequest, outcome.Status);
        Assert.Equal(expectedFailure, outcome.Failure);
        Assert.Null(outcome.TargetPath);
        Assert.Empty(composition.Calls);
    }

    public static IEnumerable<object?[]> InvalidRequests()
    {
        var valid = Request();
        yield return new object?[] { null, SourceExactRecoveryFailure.InvalidRequest };
        yield return new object?[]
        {
            valid with { Repository = "ensrick/vermintide-2-tweaker" },
            SourceExactRecoveryFailure.InvalidRepository
        };
        yield return new object?[]
        {
            valid with { ModId = "bad/mod" },
            SourceExactRecoveryFailure.InvalidModId
        };
        yield return new object?[]
        {
            valid with { WorkshopId = "03712929235" },
            SourceExactRecoveryFailure.InvalidWorkshopId
        };
        yield return new object?[]
        {
            valid with { WorkshopId = "103712929235" },
            SourceExactRecoveryFailure.InvalidWorkshopId
        };
        yield return new object?[]
        {
            valid with { SourceCommit = SourceCommit.ToUpperInvariant() },
            SourceExactRecoveryFailure.InvalidSourceCommit
        };
        yield return new object?[]
        {
            valid with { WorkshopContentRoot = "relative-workshop-root" },
            SourceExactRecoveryFailure.InvalidWorkshopContentRoot
        };
    }

    [Fact]
    public async Task ValidRequestUsesExactSyntheticTargetAndStrictCallOrder()
    {
        var composition = new RecordingComposition();
        using var coordinator = new SourceExactRecoveryCoordinator(composition);
        var request = Request();
        var expectedTarget = SyntheticTarget(request);

        var outcome = await coordinator.RecoverAsync(request);

        Assert.Equal(SourceExactRecoveryStatus.Succeeded, outcome.Status);
        Assert.Equal(SourceExactRecoveryFailure.None, outcome.Failure);
        Assert.Equal(expectedTarget, outcome.TargetPath);
        Assert.Equal(
            new[] { "recover", "resolve", "stage", "install" },
            composition.Calls);
        Assert.Equal(expectedTarget, composition.RecoveryTarget);
        Assert.Equal(
            new RecoveryHistoryQuery(Repository, ModId, WorkshopId, SourceCommit),
            composition.Query);
        Assert.Same(composition.Artifact, composition.StagedArtifact);
        Assert.Same(composition.Artifact, composition.InstalledArtifact);
        Assert.Equal(expectedTarget, composition.StageTarget);
        Assert.Same(composition.LastLease, composition.InstalledLease);
        Assert.Equal(1, composition.InstallCalls);
        Assert.Equal(1, composition.LastLease!.DisposeCalls);
        Assert.NotEqual(
            Path.Combine(request.WorkshopContentRoot, WorkshopId),
            outcome.TargetPath);
    }

    [Theory]
    [MemberData(nameof(ResolutionMappings))]
    public async Task NonSurvivingResolutionMapsTerminallyAndNeverStages(
        RecoveryResolutionStatus resolutionStatus,
        RecoveryResolutionFailure resolutionFailure,
        SourceExactRecoveryStatus expectedStatus,
        SourceExactRecoveryFailure expectedFailure)
    {
        var composition = new RecordingComposition
        {
            Resolution = new RecoveryHistoryResolution(
                resolutionStatus,
                resolutionFailure,
                "typed resolver result",
                Evidence)
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Equal(expectedFailure, outcome.Failure);
        Assert.Equal(resolutionFailure, outcome.ResolutionFailure);
        Assert.Same(Evidence, outcome.ResolutionEvidence);
        Assert.Equal(new[] { "recover", "resolve" }, composition.Calls);
        Assert.Equal(0, composition.StageCalls);
        Assert.Equal(0, composition.InstallCalls);
    }

    public static IEnumerable<object[]> ResolutionMappings()
    {
        yield return ResolutionMapping(
            RecoveryResolutionStatus.ArtifactGone,
            RecoveryResolutionFailure.NoSourceExactRecord,
            SourceExactRecoveryStatus.ArtifactGone,
            SourceExactRecoveryFailure.ResolutionNoSourceExactRecord);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.ArtifactGone,
            RecoveryResolutionFailure.NoSurvivingArchive,
            SourceExactRecoveryStatus.ArtifactGone,
            SourceExactRecoveryFailure.ResolutionNoSurvivingArchive);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.BoundedExhaustion,
            RecoveryResolutionFailure.HistoryBoundExceeded,
            SourceExactRecoveryStatus.BoundsExceeded,
            SourceExactRecoveryFailure.ResolutionHistoryBoundExceeded);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.BoundedExhaustion,
            RecoveryResolutionFailure.ManifestBoundExceeded,
            SourceExactRecoveryStatus.BoundsExceeded,
            SourceExactRecoveryFailure.ResolutionManifestBoundExceeded);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.BoundedExhaustion,
            RecoveryResolutionFailure.RowBoundExceeded,
            SourceExactRecoveryStatus.BoundsExceeded,
            SourceExactRecoveryFailure.ResolutionRowBoundExceeded);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.BoundedExhaustion,
            RecoveryResolutionFailure.AssetBoundExceeded,
            SourceExactRecoveryStatus.BoundsExceeded,
            SourceExactRecoveryFailure.ResolutionAssetBoundExceeded);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.RemoteFailure,
            RecoveryResolutionFailure.RemoteUnavailable,
            SourceExactRecoveryStatus.RemoteFailure,
            SourceExactRecoveryFailure.ResolutionRemoteFailure);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.RemoteFailure,
            RecoveryResolutionFailure.HistoryChangedDuringScan,
            SourceExactRecoveryStatus.RemoteFailure,
            SourceExactRecoveryFailure.ResolutionRemoteFailure);
        yield return ResolutionMapping(
            RecoveryResolutionStatus.ContractFailure,
            RecoveryResolutionFailure.AmbiguousSemanticProof,
            SourceExactRecoveryStatus.ContractFailure,
            SourceExactRecoveryFailure.ResolutionContractFailure);
    }

    [Theory]
    [InlineData(
        RecoveryReleaseSourceFailure.Remote,
        SourceExactRecoveryStatus.RemoteFailure,
        SourceExactRecoveryFailure.ResolutionRemoteFailure)]
    [InlineData(
        RecoveryReleaseSourceFailure.Contract,
        SourceExactRecoveryStatus.ContractFailure,
        SourceExactRecoveryFailure.ResolutionContractFailure)]
    [InlineData(
        RecoveryReleaseSourceFailure.HistoryBoundExceeded,
        SourceExactRecoveryStatus.BoundsExceeded,
        SourceExactRecoveryFailure.ResolutionHistoryBoundExceeded)]
    [InlineData(
        RecoveryReleaseSourceFailure.ManifestBoundExceeded,
        SourceExactRecoveryStatus.BoundsExceeded,
        SourceExactRecoveryFailure.ResolutionManifestBoundExceeded)]
    [InlineData(
        RecoveryReleaseSourceFailure.AssetBoundExceeded,
        SourceExactRecoveryStatus.BoundsExceeded,
        SourceExactRecoveryFailure.ResolutionAssetBoundExceeded)]
    public async Task ResolverSourceExceptionsRemainTypedAndNeverStage(
        RecoveryReleaseSourceFailure sourceFailure,
        SourceExactRecoveryStatus expectedStatus,
        SourceExactRecoveryFailure expectedFailure)
    {
        var composition = new RecordingComposition
        {
            ResolveException = new RecoveryReleaseSourceException(
                sourceFailure,
                "typed source failure")
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Equal(expectedFailure, outcome.Failure);
        Assert.Equal(new[] { "recover", "resolve" }, composition.Calls);
        Assert.Equal(0, composition.StageCalls);
    }

    [Fact]
    public async Task InconsistentResolutionStatusAndFailureFailsClosed()
    {
        var composition = new RecordingComposition
        {
            Resolution = new RecoveryHistoryResolution(
                RecoveryResolutionStatus.ArtifactGone,
                RecoveryResolutionFailure.AssetBoundExceeded,
                "inconsistent",
                Evidence)
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(SourceExactRecoveryStatus.ContractFailure, outcome.Status);
        Assert.Equal(
            SourceExactRecoveryFailure.ResolutionContractFailure,
            outcome.Failure);
        Assert.Equal(new[] { "recover", "resolve" }, composition.Calls);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("mod")]
    [InlineData("workshop")]
    [InlineData("commit")]
    public async Task ResolverSuccessWithForeignTupleNeverStages(string changedAxis)
    {
        var artifact = CoordinatorArtifact();
        artifact = changedAxis switch
        {
            "repository" => artifact with { Repository = "Ensrick/foreign" },
            "mod" => artifact with
            {
                Proof = artifact.Proof with
                {
                    Record = artifact.Proof.Record with { ModId = "foreign" }
                }
            },
            "workshop" => artifact with
            {
                Proof = artifact.Proof with
                {
                    Record = artifact.Proof.Record with { WorkshopId = "3716286199" }
                }
            },
            _ => artifact with
            {
                Proof = artifact.Proof with
                {
                    Record = artifact.Proof.Record with
                    {
                        Source = artifact.Proof.Record.Source with
                        {
                            Commit = "1123456789abcdef0123456789abcdef01234567"
                        }
                    }
                }
            }
        };
        var composition = new RecordingComposition(artifact);
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(SourceExactRecoveryStatus.ContractFailure, outcome.Status);
        Assert.Equal(
            SourceExactRecoveryFailure.ResolutionContractFailure,
            outcome.Failure);
        Assert.Equal(new[] { "recover", "resolve" }, composition.Calls);
    }

    [Fact]
    public async Task ForeignStageTargetIsDisposedAndNeverInstalled()
    {
        var composition = new RecordingComposition
        {
            LeaseTargetOverride = Path.Combine(Path.GetTempPath(), "foreign-target")
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(SourceExactRecoveryStatus.ContractFailure, outcome.Status);
        Assert.Equal(SourceExactRecoveryFailure.StageContractFailure, outcome.Failure);
        Assert.Equal(new[] { "recover", "resolve", "stage" }, composition.Calls);
        Assert.Equal(0, composition.InstallCalls);
        Assert.Equal(1, composition.LastLease!.DisposeCalls);
    }

    [Theory]
    [MemberData(nameof(StageMappings))]
    public async Task StageFailuresMapTerminallyAndNeverInstall(
        int stageFailureValue,
        SourceExactRecoveryStatus expectedStatus,
        SourceExactRecoveryFailure expectedFailure)
    {
        var stageFailure = (SourceExactStageFailure)stageFailureValue;
        var composition = new RecordingComposition
        {
            StageException = new SourceExactStageException(stageFailure, "typed stage failure")
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Equal(expectedFailure, outcome.Failure);
        Assert.Equal(stageFailure, outcome.StageFailure);
        Assert.Equal(new[] { "recover", "resolve", "stage" }, composition.Calls);
        Assert.Equal(0, composition.InstallCalls);
    }

    public static IEnumerable<object[]> StageMappings()
    {
        foreach (var failure in new[]
                 {
                     SourceExactStageFailure.InvalidArtifact,
                     SourceExactStageFailure.ProofDrift,
                     SourceExactStageFailure.InvalidTarget
                 })
        {
            yield return StageMapping(
                failure,
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.StageContractFailure);
        }
        yield return StageMapping(
            SourceExactStageFailure.ArtifactGone,
            SourceExactRecoveryStatus.ArtifactGone,
            SourceExactRecoveryFailure.StageArtifactGone);
        yield return StageMapping(
            SourceExactStageFailure.Remote,
            SourceExactRecoveryStatus.RemoteFailure,
            SourceExactRecoveryFailure.StageRemoteFailure);
        foreach (var failure in new[]
                 {
                     SourceExactStageFailure.CompressedLimitExceeded,
                     SourceExactStageFailure.EntryLimitExceeded,
                     SourceExactStageFailure.OutputLimitExceeded
                 })
        {
            yield return StageMapping(
                failure,
                SourceExactRecoveryStatus.BoundsExceeded,
                SourceExactRecoveryFailure.StageBoundExceeded);
        }
        foreach (var failure in new[]
                 {
                     SourceExactStageFailure.Timeout,
                     SourceExactStageFailure.UnsafeEntry,
                     SourceExactStageFailure.OutputSetMismatch,
                     SourceExactStageFailure.IntegrityMismatch,
                     SourceExactStageFailure.MalformedArchive,
                     SourceExactStageFailure.FileSystem
                 })
        {
            yield return StageMapping(
                failure,
                SourceExactRecoveryStatus.StageFailure,
                SourceExactRecoveryFailure.StageVerificationFailure);
        }
    }

    [Fact]
    public async Task RecoveryFailureIsTerminalBeforeResolverOrNetwork()
    {
        var composition = new RecordingComposition
        {
            RecoverException = new SourceExactTransactionException(
                SourceExactTransactionFailure.Locked,
                "locked")
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(SourceExactRecoveryStatus.RecoveryFailure, outcome.Status);
        Assert.Equal(
            SourceExactRecoveryFailure.RecoveryTransactionFailure,
            outcome.Failure);
        Assert.Equal(SourceExactTransactionFailure.Locked, outcome.TransactionFailure);
        Assert.Equal(new[] { "recover" }, composition.Calls);
    }

    [Fact]
    public async Task InstallFailureDisposesLeaseWithoutRetry()
    {
        var composition = new RecordingComposition
        {
            InstallException = new SourceExactTransactionException(
                SourceExactTransactionFailure.RollbackFailed,
                "rollback failed")
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(SourceExactRecoveryStatus.InstallFailure, outcome.Status);
        Assert.Equal(SourceExactRecoveryFailure.InstallTransactionFailure, outcome.Failure);
        Assert.Equal(
            SourceExactTransactionFailure.RollbackFailed,
            outcome.TransactionFailure);
        Assert.Equal(
            new[] { "recover", "resolve", "stage", "install" },
            composition.Calls);
        Assert.Equal(1, composition.InstallCalls);
        Assert.Equal(1, composition.LastLease!.DisposeCalls);
    }

    [Fact]
    public async Task CancellationBeforeRecoveryPerformsNoIo()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var composition = new RecordingComposition();
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request(), cancellation.Token);

        Assert.Equal(SourceExactRecoveryStatus.Cancelled, outcome.Status);
        Assert.Equal(SourceExactRecoveryFailure.Cancelled, outcome.Failure);
        Assert.Empty(composition.Calls);
    }

    [Fact]
    public async Task CancellationAfterRecoveryStopsBeforeResolverIo()
    {
        using var cancellation = new CancellationTokenSource();
        var composition = new RecordingComposition
        {
            AfterRecover = cancellation.Cancel
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request(), cancellation.Token);

        Assert.Equal(SourceExactRecoveryStatus.Cancelled, outcome.Status);
        Assert.Equal(new[] { "recover" }, composition.Calls);
    }

    [Fact]
    public async Task CancellationAfterStageDisposesLeaseAndNeverInstalls()
    {
        using var cancellation = new CancellationTokenSource();
        var composition = new RecordingComposition
        {
            AfterStage = cancellation.Cancel
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request(), cancellation.Token);

        Assert.Equal(SourceExactRecoveryStatus.Cancelled, outcome.Status);
        Assert.Equal(SourceExactRecoveryFailure.Cancelled, outcome.Failure);
        Assert.Equal(new[] { "recover", "resolve", "stage" }, composition.Calls);
        Assert.Equal(0, composition.InstallCalls);
        Assert.Equal(1, composition.LastLease!.DisposeCalls);
    }

    [Fact]
    public async Task CallerCancellationDuringInstallDisposesLeaseWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var composition = new RecordingComposition
        {
            BeforeInstall = cancellation.Cancel,
            InstallException = new OperationCanceledException(cancellation.Token)
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request(), cancellation.Token);

        Assert.Equal(SourceExactRecoveryStatus.Cancelled, outcome.Status);
        Assert.Equal(SourceExactRecoveryFailure.Cancelled, outcome.Failure);
        Assert.Equal(1, composition.InstallCalls);
        Assert.Equal(1, composition.LastLease!.DisposeCalls);
    }

    [Fact]
    public async Task UncorrelatedCancellationExceptionIsNotCallerCancellation()
    {
        var composition = new RecordingComposition
        {
            RecoverException = new OperationCanceledException("foreign cancellation")
        };
        using var coordinator = new SourceExactRecoveryCoordinator(composition);

        var outcome = await coordinator.RecoverAsync(Request());

        Assert.Equal(SourceExactRecoveryStatus.RecoveryFailure, outcome.Status);
        Assert.Equal(
            SourceExactRecoveryFailure.RecoveryTransactionFailure,
            outcome.Failure);
    }

    [Fact]
    public void CoordinatorHasNoLatestFallbackOrLegacyDeploySurface()
    {
        var methods = typeof(ISourceExactRecoveryComposition)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "Install", "Recover", "ResolveAsync", "StageAsync" },
            methods);
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(ISourceExactRecoveryComposition)));

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "VT2ModUpdater",
            "Services",
            "SourceExactRecoveryCoordinator.cs"));
        Assert.DoesNotContain("DeployZipBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRealFolder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHubReleaseClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLatestRelease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/latest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferOwnership(", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "Deployer.GetSyntheticFolder("));
        Assert.Equal(1, CountOccurrences(source, "_transaction.Install("));
    }

    private static object[] ResolutionMapping(
        RecoveryResolutionStatus status,
        RecoveryResolutionFailure failure,
        SourceExactRecoveryStatus expectedStatus,
        SourceExactRecoveryFailure expectedFailure) =>
        new object[] { status, failure, expectedStatus, expectedFailure };

    private static object[] StageMapping(
        SourceExactStageFailure failure,
        SourceExactRecoveryStatus expectedStatus,
        SourceExactRecoveryFailure expectedFailure) =>
        new object[] { (int)failure, expectedStatus, expectedFailure };

    private static SourceExactRecoveryRequest Request() => new(
        Repository,
        ModId,
        WorkshopId,
        SourceCommit,
        Path.Combine(Path.GetTempPath(), "vt2-source-exact-coordinator-workshop"));

    private static string SyntheticTarget(SourceExactRecoveryRequest request) =>
        Deployer.GetSyntheticFolder(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(request.WorkshopContentRoot)),
            request.WorkshopId);

    private static RecoveryHistoryResolution Surviving(
        SourceExactRecoveryArtifact artifact) => new(
            RecoveryResolutionStatus.SourceExactSurvivingArtifact,
            RecoveryResolutionFailure.None,
            "surviving",
            Evidence,
            artifact);

    private static SourceExactRecoveryArtifact CoordinatorArtifact(
        string repository = Repository,
        string modId = ModId,
        string workshopId = WorkshopId,
        string sourceCommit = SourceCommit)
    {
        var outputFiles = Array.AsReadOnly(new[]
        {
            new RecoveryOutputFile("modx.mod", 17, Hash, Blob)
        });
        var record = new RecoveryRecord(
            1,
            new RecoveryRelease(repository, "mods-origin-2026-08-28"),
            "modx",
            modId,
            workshopId,
            "1.2.3-dev",
            new RecoveryAsset("modx.zip", 546, Hash),
            new RecoverySource(sourceCommit, "clean", Hash, Blob),
            new RecoveryBuilder("VMBLauncher", "0.6.0"),
            "tracked",
            new RecoveryAuthorityProof("git_clean_blob", Blob, Blob),
            "0123456789abcdef.mod_bundle",
            new RecoveryDescriptor("modx.mod", Hash, Blob),
            new RecoveryOutput("output-algorithm", Hash, outputFiles),
            new RecoveryBuildReceipt(
                "modx/.build-receipt.json",
                3,
                Blob,
                Hash,
                "source-algorithm",
                Hash,
                "0123456789abcdef.mod_bundle",
                "modx.mod",
                Hash,
                "output-algorithm",
                Hash,
                "VMBLauncher",
                "0.6.0",
                new RecoveryNormalizationPolicy(
                    "normalization-algorithm",
                    Hash,
                    Array.Empty<RecoveryExcludedOutput>())));
        var proof = new ValidatedRecoveryRecord(
            record,
            "semantic-algorithm",
            Hash);
        return new SourceExactRecoveryArtifact(
            repository,
            record.Release.Tag,
            100,
            "mods-container-2026-08-29",
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
            200,
            record.Asset.Filename,
            record.Asset.Length,
            record.Asset.Sha256,
            $"https://github.com/{repository}/releases/download/" +
            "mods-container-2026-08-29/modx.zip",
            proof,
            1,
            1);
    }

    private static string RepositoryRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null &&
               !File.Exists(Path.Combine(cursor.FullName, "vt2-mod-updater.sln")))
        {
            cursor = cursor.Parent;
        }
        return cursor?.FullName ??
            throw new DirectoryNotFoundException("cannot locate updater repository root");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private sealed class RecordingComposition : ISourceExactRecoveryComposition
    {
        internal RecordingComposition(SourceExactRecoveryArtifact? artifact = null)
        {
            Artifact = artifact ?? CoordinatorArtifact();
            Resolution = Surviving(Artifact);
        }

        internal List<string> Calls { get; } = new();
        internal SourceExactRecoveryArtifact Artifact { get; }
        internal RecoveryHistoryResolution Resolution { get; set; }
        internal Exception? RecoverException { get; set; }
        internal Exception? ResolveException { get; set; }
        internal Exception? StageException { get; set; }
        internal Exception? InstallException { get; set; }
        internal Action? AfterRecover { get; set; }
        internal Action? AfterStage { get; set; }
        internal Action? BeforeInstall { get; set; }
        internal string? LeaseTargetOverride { get; set; }
        internal string? RecoveryTarget { get; private set; }
        internal RecoveryHistoryQuery? Query { get; private set; }
        internal string? StageTarget { get; private set; }
        internal SourceExactRecoveryArtifact? StagedArtifact { get; private set; }
        internal SourceExactRecoveryArtifact? InstalledArtifact { get; private set; }
        internal ISourceExactRecoveryStageLease? InstalledLease { get; private set; }
        internal RecordingStageLease? LastLease { get; private set; }
        internal int StageCalls { get; private set; }
        internal int InstallCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public void Recover(string targetPath)
        {
            Calls.Add("recover");
            RecoveryTarget = targetPath;
            if (RecoverException is not null)
                throw RecoverException;
            AfterRecover?.Invoke();
        }

        public Task<RecoveryHistoryResolution> ResolveAsync(
            RecoveryHistoryQuery query,
            CancellationToken cancellationToken)
        {
            Calls.Add("resolve");
            Query = query;
            return ResolveException is null
                ? Task.FromResult(Resolution)
                : Task.FromException<RecoveryHistoryResolution>(ResolveException);
        }

        public Task<ISourceExactRecoveryStageLease> StageAsync(
            SourceExactRecoveryArtifact artifact,
            string targetPath,
            CancellationToken cancellationToken)
        {
            Calls.Add("stage");
            StageCalls++;
            StageTarget = targetPath;
            StagedArtifact = artifact;
            if (StageException is not null)
            {
                return Task.FromException<ISourceExactRecoveryStageLease>(
                    StageException);
            }
            LastLease = new RecordingStageLease(LeaseTargetOverride ?? targetPath);
            AfterStage?.Invoke();
            return Task.FromResult<ISourceExactRecoveryStageLease>(LastLease);
        }

        public void Install(
            ISourceExactRecoveryStageLease stage,
            SourceExactRecoveryArtifact artifact,
            CancellationToken cancellationToken)
        {
            Calls.Add("install");
            InstallCalls++;
            InstalledLease = stage;
            InstalledArtifact = artifact;
            BeforeInstall?.Invoke();
            if (InstallException is not null)
                throw InstallException;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class RecordingStageLease(string intendedTargetPath) :
        ISourceExactRecoveryStageLease
    {
        public string IntendedTargetPath { get; } = intendedTargetPath;
        internal int DisposeCalls { get; private set; }

        public void Dispose() => DisposeCalls++;
    }
}
