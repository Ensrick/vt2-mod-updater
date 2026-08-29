using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public sealed class SourceExactZipStagerTests : IDisposable
{
    private const string ArchiveSha256 =
        "7d1f642208d5851b8cfa748e4207093c24de70a2a6377b2473b1b1996d86b4e0";
    private const string DescriptorSha256 =
        "6db3ae2ce8ed0d57f22fb35a5beaa8cb0ec35ec9d560b829e582dd4d63ea78f3";
    private const string ContainerTag = "mods-container-2026-08-28";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "vt2-source-exact-stager-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;

    public SourceExactZipStagerTests()
    {
        Directory.CreateDirectory(_root);
        _target = Path.Combine(_root, "current-install");
        Directory.CreateDirectory(Path.Combine(_target, "nested"));
        File.WriteAllBytes(Path.Combine(_target, "keep.bin"), new byte[] { 9, 8, 7, 6 });
        File.WriteAllText(Path.Combine(_target, "nested", "keep.txt"), "do not touch");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("producer-tracked.zip", "valid-tracked.json", "tracked")]
    [InlineData("producer-receipt.zip", "valid-receipt.json", "receipt")]
    public async Task ProducerFixtureStagesExactOutputsAndLeavesTargetUntouched(
        string archiveFixture,
        string recordFixture,
        string authority)
    {
        var before = SnapshotTarget();
        var bytes = ArchiveFixture(archiveFixture);
        Assert.Equal(546, bytes.Length);
        Assert.Equal(ArchiveSha256, Sha256(bytes));
        var artifact = ArtifactFor(bytes, recordFixture, authority);
        var source = new ByteSource(bytes, artifact.AssetLength);
        var stager = new SourceExactZipStager(source);

        using (var stage = await stager.StageAsync(artifact, _target))
        {
            Assert.Equal(1, source.Calls);
            Assert.Equal(Path.GetDirectoryName(_target), Path.GetDirectoryName(stage.StageDirectory));
            Assert.NotEqual(Path.GetFullPath(_target), Path.GetFullPath(stage.StageDirectory));
            Assert.Equal(artifact.AssetSha256, stage.ArchiveSha256);
            Assert.Equal(3, stage.Outputs.Count);
            Assert.Equal(
                new[]
                {
                    "0123456789abcdef.mod_bundle",
                    "fedcba9876543210.mod_bundle",
                    "modx.mod",
                    SourceExactZipStager.VersionMarkerFilename
                },
                Directory.EnumerateFiles(stage.StageDirectory)
                    .Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(
                "1.2.3-dev",
                File.ReadAllText(Path.Combine(
                    stage.StageDirectory,
                    SourceExactZipStager.VersionMarkerFilename), Encoding.ASCII));
            AssertTargetUnchanged(before);
        }

        AssertTargetUnchanged(before);
        AssertNoPrivateStageRemains();
    }

    [Fact]
    public async Task ProducerStageTransfersItsExactLeaseAndAuthorityIntoPhase4Install()
    {
        var target = Path.Combine(_root, "phase4-install");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.mod_bundle"), "prior");
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var source = new ByteSource(bytes, bytes.LongLength);
        var stager = new SourceExactZipStager(source);
        var stage = await stager.StageAsync(artifact, target);
        var stageDirectory = stage.StageDirectory;

        var result = new SourceExactDirectoryTransaction().Install(stage, artifact);
        stage.Dispose();

        Assert.Equal(1, source.Calls);
        Assert.Equal(target, result.TargetPath);
        Assert.Equal(artifact.AssetSha256, result.InstalledState.AssetSha256);
        Assert.Equal(artifact.AssetLength, result.InstalledState.AssetLength);
        Assert.Equal(artifact.AssetId, result.InstalledState.AssetId);
        Assert.Equal(artifact.ContainerReleaseId, result.InstalledState.ContainerReleaseId);
        Assert.Equal(artifact.Proof.Record.Output.FingerprintSha256,
            result.InstalledState.OutputFingerprint);
        Assert.Equal("1.2.3-dev", File.ReadAllText(Path.Combine(
            target, SourceExactZipStager.VersionMarkerFilename), Encoding.ASCII));
        Assert.False(File.Exists(Path.Combine(target, "old.mod_bundle")));
        Assert.False(Directory.Exists(stageDirectory));

        var sidecar = SourceExactInstalledState.Parse(File.ReadAllBytes(
            Path.Combine(target, SourceExactInstalledState.Filename)));
        Assert.Equal(result.InstalledState.AssetSha256, sidecar.AssetSha256);
        Assert.Equal(result.InstalledState.OutputFingerprint, sidecar.OutputFingerprint);
        Assert.Equal(
            result.InstalledState.Outputs.Select(row =>
                (row.Filename, row.Length, row.Sha256)),
            sidecar.Outputs.Select(row =>
                (row.Filename, row.Length, row.Sha256)));
        foreach (var output in sidecar.Outputs)
        {
            var path = Path.Combine(target, output.Filename);
            Assert.True(File.Exists(path));
            Assert.Equal(output.Length, new FileInfo(path).Length);
            Assert.Equal(output.Sha256, Sha256(File.ReadAllBytes(path)));
        }

        // The caller's stale Phase 3 owner is inert after the one-shot transfer.
        stage.Dispose();
        Assert.True(File.Exists(Path.Combine(target, "modx.mod")));
        AssertNoPrivateStageRemains();
    }

    [Fact]
    public async Task SuccessfulStageDoesNotCreateAnAbsentIntendedTarget()
    {
        var absentTarget = Path.Combine(_root, "not-installed-yet");
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var stager = new SourceExactZipStager(new ByteSource(bytes, bytes.Length));

        using (var stage = await stager.StageAsync(artifact, absentTarget))
        {
            Assert.False(Directory.Exists(absentTarget));
            Assert.True(Directory.Exists(stage.StageDirectory));
        }

        Assert.False(Directory.Exists(absentTarget));
        AssertNoPrivateStageRemains();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TrailingSeparatorSuccessAndCleanupUseSameVolumeSibling(
        bool existingTarget,
        bool alternateSeparator)
    {
        var target = TargetForMatrix(existingTarget);
        var suppliedTarget = WithTrailingSeparator(target, alternateSeparator);
        var before = SnapshotTarget(target);
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var stager = new SourceExactZipStager(new ByteSource(bytes, bytes.Length));
        string stageDirectory;

        using (var stage = await stager.StageAsync(artifact, suppliedTarget))
        {
            stageDirectory = stage.StageDirectory;
            Assert.Equal(Path.GetFullPath(target), stage.IntendedTargetPath);
            AssertSameVolumeSibling(stageDirectory, target);
            AssertTargetUnchanged(target, before);
        }

        Assert.False(Directory.Exists(stageDirectory));
        AssertTargetUnchanged(target, before);
        AssertNoPrivateStageRemains();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TrailingSeparatorArchiveValidationFailureCleansSiblingOnly(
        bool existingTarget,
        bool alternateSeparator)
    {
        var target = TargetForMatrix(existingTarget);
        var suppliedTarget = WithTrailingSeparator(target, alternateSeparator);
        var before = SnapshotTarget(target);
        var malformed = Encoding.ASCII.GetBytes("not a zip archive");
        var artifact = ArtifactFor(malformed);
        string? observedStage = null;
        var source = new ObservingSource(
            () => new MemoryStream(malformed, writable: false),
            malformed.Length,
            () => observedStage = ObserveOnlyPrivateStage(target));

        var exception = await Assert.ThrowsAsync<SourceExactStageException>(() =>
            new SourceExactZipStager(source).StageAsync(artifact, suppliedTarget));

        Assert.Equal(SourceExactStageFailure.MalformedArchive, exception.Failure);
        Assert.NotNull(observedStage);
        AssertSameVolumeSibling(observedStage!, target);
        Assert.False(Directory.Exists(observedStage));
        AssertTargetUnchanged(target, before);
        AssertNoPrivateStageRemains();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TrailingSeparatorCancellationCleansSiblingOnly(
        bool existingTarget,
        bool alternateSeparator)
    {
        var target = TargetForMatrix(existingTarget);
        var suppliedTarget = WithTrailingSeparator(target, alternateSeparator);
        var before = SnapshotTarget(target);
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        string? observedStage = null;
        var source = new ObservingSource(
            () => new StallingStream(),
            artifact.AssetLength,
            () => observedStage = ObserveOnlyPrivateStage(target));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SourceExactZipStager(source, TimeSpan.FromSeconds(5))
                .StageAsync(artifact, suppliedTarget, cancellation.Token));

        Assert.NotNull(observedStage);
        AssertSameVolumeSibling(observedStage!, target);
        Assert.False(Directory.Exists(observedStage));
        AssertTargetUnchanged(target, before);
        AssertNoPrivateStageRemains();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesystemRootWithTrailingSeparatorRemainsRootAndIsRejected(
        bool alternateSeparator)
    {
        var root = Path.GetPathRoot(_root)!;
        var suppliedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) +
            (alternateSeparator
                ? Path.AltDirectorySeparatorChar
                : Path.DirectorySeparatorChar);
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var source = new ByteSource(bytes, bytes.Length);

        var exception = await Assert.ThrowsAsync<SourceExactStageException>(() =>
            new SourceExactZipStager(source).StageAsync(artifact, suppliedRoot));

        Assert.Equal(SourceExactStageFailure.InvalidTarget, exception.Failure);
        Assert.Equal(0, source.Calls);
        AssertNoPrivateStageRemains();
    }

    [Fact]
    public async Task NumericGitHubTransportComposesWithExactStagerWithoutTargetMutation()
    {
        var before = SnapshotTarget();
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var handler = new SingleResponseHandler(bytes);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubSourceExactArchiveSource(http);
        var stager = new SourceExactZipStager(source);

        using var stage = await stager.StageAsync(artifact, _target);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(
            $"https://api.github.com/repos/{RecoveryRecordContract.Repository}/" +
                $"releases/assets/{artifact.AssetId}",
            handler.LastRequestUri!.AbsoluteUri);
        Assert.Equal(3, stage.Outputs.Count);
        AssertTargetUnchanged(before);
    }

    [Fact]
    public async Task TruncatedDownloadFailsBeforeZipOpenAndLeavesTargetUntouched()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var source = new ByteSource(bytes[..^10], artifact.AssetLength);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.IntegrityMismatch, exception.Failure);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task UnderdeclaredContentLengthIsRejectedWithoutReadingBody()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var stream = new RecordingStream(bytes);
        var source = new StreamSource(() => stream, artifact.AssetLength - 1);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.IntegrityMismatch, exception.Failure);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task OneBitArchiveTamperIsRejectedBeforeExtraction()
    {
        var expected = ArchiveFixture("producer-tracked.zip");
        var tampered = expected.ToArray();
        tampered[100] ^= 0x01;
        var artifact = ArtifactFor(expected);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(tampered, tampered.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.IntegrityMismatch, exception.Failure);
    }

    [Fact]
    public async Task DownloadOverrunConsumesOnlyOneSentinelByte()
    {
        var expected = ArchiveFixture("producer-tracked.zip");
        var overrun = expected.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        var artifact = ArtifactFor(expected);
        var stream = new RecordingStream(overrun);
        var source = new StreamSource(() => stream, declaredLength: null);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.CompressedLimitExceeded, exception.Failure);
        Assert.Equal(artifact.AssetLength + 1, stream.BytesRead);
    }

    [Fact]
    public async Task CallerCancellationDuringStallIsPreservedAndCleansStage()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var before = SnapshotTarget();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        var stager = new SourceExactZipStager(
            new StreamSource(() => new StallingStream(), artifact.AssetLength),
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stager.StageAsync(artifact, _target, cancellation.Token));

        AssertTargetUnchanged(before);
        AssertNoPrivateStageRemains();
    }

    [Fact]
    public async Task StalledDownloadHitsTypedLinkedDeadlineAndCleansStage()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var stager = new SourceExactZipStager(
            new StreamSource(() => new StallingStream(), artifact.AssetLength),
            TimeSpan.FromMilliseconds(75));

        var exception = await AssertFailureUntouchedAsync(stager, artifact);

        Assert.Equal(SourceExactStageFailure.Timeout, exception.Failure);
    }

    [Fact]
    public async Task MalformedZipWithExactArchiveProofIsRejected()
    {
        var bytes = Encoding.ASCII.GetBytes("this is not a zip");
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.MalformedArchive, exception.Failure);
    }

    [Fact]
    public async Task CompressedCoordinateOverBoundNeverDownloads()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(
            bytes,
            declaredAssetLength: SourceExactZipStager.MaximumCompressedBytes + 1);
        var source = new ByteSource(bytes, bytes.Length);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.CompressedLimitExceeded, exception.Failure);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task EntryCountBombIsRejectedBeforeExtraction()
    {
        var entries = Enumerable.Range(0, SourceExactZipStager.MaximumEntries + 1)
            .Select(index => new ZipSpec($"entry-{index:D4}", Array.Empty<byte>()))
            .ToArray();
        var bytes = CreateZip(entries);
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.EntryLimitExceeded, exception.Failure);
    }

    [Fact]
    public async Task DeclaredPerOutputBombIsRejectedBeforeEntryRead()
    {
        var bytes = PatchUncompressedLengths(
            ArchiveFixture("producer-tracked.zip"),
            new Dictionary<string, uint>(StringComparer.Ordinal)
            {
                ["0123456789abcdef.mod_bundle"] =
                    checked((uint)SourceExactZipStager.MaximumOutputBytes + 1)
            });
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.OutputLimitExceeded, exception.Failure);
    }

    [Fact]
    public async Task DeclaredAggregateBombIsRejectedBeforeEntryRead()
    {
        const uint EightHundredMiB = 800U * 1024 * 1024;
        var bytes = PatchUncompressedLengths(
            ArchiveFixture("producer-tracked.zip"),
            new Dictionary<string, uint>(StringComparer.Ordinal)
            {
                ["0123456789abcdef.mod_bundle"] = EightHundredMiB,
                ["fedcba9876543210.mod_bundle"] = EightHundredMiB,
                ["modx.mod"] = EightHundredMiB
            });
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.OutputLimitExceeded, exception.Failure);
    }

    [Fact]
    public async Task MissingDeclaredOutputIsRejected()
    {
        var bytes = CreateZip(ValidEntries().Where(spec => spec.Name != "modx.mod").ToArray());
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.OutputSetMismatch, exception.Failure);
    }

    [Fact]
    public async Task ExtraOutputIsRejected()
    {
        var bytes = CreateZip(ValidEntries()
            .Append(new ZipSpec("extra.mod_bundle", new byte[] { 1 }))
            .ToArray());
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.OutputSetMismatch, exception.Failure);
    }

    [Theory]
    [InlineData("nested/modx.mod")]
    [InlineData("nested\\modx.mod")]
    [InlineData("modx.mod:stream")]
    [InlineData("CON")]
    public async Task NestedAdsAndDeviceEntriesAreRejected(string hostileName)
    {
        var bytes = CreateZip(ValidEntries()
            .Append(new ZipSpec(hostileName, new byte[] { 1 }))
            .ToArray());
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.UnsafeEntry, exception.Failure);
    }

    [Theory]
    [InlineData("modx.mod")]
    [InlineData("MODX.MOD")]
    public async Task DuplicateAndCaseCollidingEntriesAreRejected(string duplicateName)
    {
        var bytes = CreateZip(ValidEntries()
            .Append(new ZipSpec(duplicateName, Encoding.ASCII.GetBytes("duplicate")))
            .ToArray());
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.UnsafeEntry, exception.Failure);
    }

    [Theory]
    [InlineData(unchecked((int)0xA0000000))]
    [InlineData((int)FileAttributes.ReparsePoint)]
    public async Task SymlinkAndReparseMetadataAreRejected(int externalAttributes)
    {
        var bytes = CreateZip(ValidEntries()
            .Append(new ZipSpec(
                "hostile-link",
                Encoding.ASCII.GetBytes("target"),
                externalAttributes))
            .ToArray());
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.UnsafeEntry, exception.Failure);
    }

    [Fact]
    public async Task WrongVersionMarkerIsRejected()
    {
        var entries = ValidEntries()
            .Select(spec => spec.Name == SourceExactZipStager.VersionMarkerFilename
                ? spec with { Bytes = Encoding.ASCII.GetBytes("9.9.9-dev") }
                : spec)
            .ToArray();
        var bytes = CreateZip(entries);
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.IntegrityMismatch, exception.Failure);
    }

    [Fact]
    public async Task WrongOutputBytesWithExactArchiveProofAreRejected()
    {
        var entries = ValidEntries()
            .Select(spec => spec.Name == "modx.mod"
                ? spec with { Bytes = Encoding.ASCII.GetBytes("fixture descript0r\n") }
                : spec)
            .ToArray();
        var bytes = CreateZip(entries);
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ByteSource(bytes, bytes.Length)),
            artifact);

        Assert.Equal(SourceExactStageFailure.IntegrityMismatch, exception.Failure);
    }

    [Fact]
    public async Task CoordinateProofDriftIsRejectedBeforeDownload()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes) with { AssetLength = bytes.Length + 1 };
        var source = new ByteSource(bytes, bytes.Length);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.ProofDrift, exception.Failure);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task BrowserCoordinateDriftIsRejectedBeforeDownload()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes) with
        {
            AssetDownloadUrl =
                "https://github.com/Ensrick/vermintide-2-tweaker/releases/download/" +
                "another-container/mx.zip"
        };
        var source = new ByteSource(bytes, bytes.Length);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.ProofDrift, exception.Failure);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task ArtifactGoneIsTypedAndLeavesTargetUntouched()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(new ThrowingSource(
                SourceExactArchiveSourceFailure.ArtifactGone)),
            artifact);

        Assert.Equal(SourceExactStageFailure.ArtifactGone, exception.Failure);
    }

    [Fact]
    public async Task NestedProofMutationIsRejectedBeforeDownload()
    {
        var bytes = ArchiveFixture("producer-tracked.zip");
        var artifact = ArtifactFor(bytes);
        var changedRecord = artifact.Proof.Record with { Version = "1.2.4-dev" };
        artifact = artifact with
        {
            Proof = artifact.Proof with { Record = changedRecord }
        };
        var source = new ByteSource(bytes, bytes.Length);

        var exception = await AssertFailureUntouchedAsync(
            new SourceExactZipStager(source),
            artifact);

        Assert.Equal(SourceExactStageFailure.ProofDrift, exception.Failure);
        Assert.Equal(0, source.Calls);
    }

    private async Task<SourceExactStageException> AssertFailureUntouchedAsync(
        SourceExactZipStager stager,
        SourceExactRecoveryArtifact artifact)
    {
        var before = SnapshotTarget();
        var exception = await Assert.ThrowsAsync<SourceExactStageException>(() =>
            stager.StageAsync(artifact, _target));
        AssertTargetUnchanged(before);
        AssertNoPrivateStageRemains();
        return exception;
    }

    private SourceExactRecoveryArtifact ArtifactFor(
        byte[] archiveBytes,
        string fixtureName = "valid-tracked.json",
        string authority = "tracked",
        long? declaredAssetLength = null)
    {
        var assetLength = declaredAssetLength ?? archiveBytes.LongLength;
        var assetSha256 = Sha256(archiveBytes);
        var json = JsonNode.Parse(RecoveryFixture(fixtureName))!.AsObject();
        json["asset"]!["length"] = assetLength;
        json["asset"]!["sha256"] = assetSha256;
        var sourceCommit = json["source"]!["commit"]!.GetValue<string>();
        var proof = RecoveryRecordContract.ParseAndValidate(
            json.ToJsonString(),
            Binding(assetSha256, sourceCommit, authority));

        return new SourceExactRecoveryArtifact(
            RecoveryRecordContract.Repository,
            proof.Record.Release.Tag,
            100,
            ContainerTag,
            DateTimeOffset.Parse("2026-08-28T12:00:00Z"),
            200,
            proof.Record.Asset.Filename,
            proof.Record.Asset.Length,
            proof.Record.Asset.Sha256,
            $"https://github.com/{RecoveryRecordContract.Repository}/releases/download/" +
                $"{ContainerTag}/{proof.Record.Asset.Filename}",
            proof,
            1,
            1);
    }

    private static RecoveryManifestBinding Binding(
        string assetSha256,
        string sourceCommit,
        string authority) => new(
        "mx",
        "1234567890",
        "1.2.3-dev",
        "mx.zip",
        assetSha256,
        sourceCommit,
        "clean",
        "VMBLauncher",
        "9.8.7+fixture",
        authority,
        "0123456789abcdef.mod_bundle",
        "modx.mod",
        Array.AsReadOnly(new[]
        {
            new RecoveryManifestBundleFile(
                "0123456789abcdef.mod_bundle",
                "57f4bc8fc7f9a9271afe6d3d0aed6afc675f06b6f6fb738b838d4f53da60f5c6"),
            new RecoveryManifestBundleFile(
                "fedcba9876543210.mod_bundle",
                "92b80db12ef207bda13fb28ade13297e316259f357d339c2fc84393854402cb5"),
            new RecoveryManifestBundleFile("modx.mod", DescriptorSha256)
        }));

    private static ZipSpec[] ValidEntries() =>
    [
        new("0123456789abcdef.mod_bundle", new byte[] { 1, 3, 3, 7, 9, 11, 13, 17 }),
        new("fedcba9876543210.mod_bundle", new byte[] { 2, 4, 6, 8, 10, 12, 14, 16 }),
        new("modx.mod", Encoding.ASCII.GetBytes("fixture descriptor\n")),
        new(SourceExactZipStager.VersionMarkerFilename, Encoding.ASCII.GetBytes("1.2.3-dev"))
    ];

    private static byte[] CreateZip(IReadOnlyList<ZipSpec> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var spec in entries)
            {
                var entry = archive.CreateEntry(spec.Name, CompressionLevel.Optimal);
                if (spec.ExternalAttributes is not null)
                    entry.ExternalAttributes = spec.ExternalAttributes.Value;
                using var stream = entry.Open();
                stream.Write(spec.Bytes);
            }
        }
        return output.ToArray();
    }

    private static byte[] PatchUncompressedLengths(
        byte[] original,
        IReadOnlyDictionary<string, uint> lengths)
    {
        var bytes = original.ToArray();
        var patched = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset <= bytes.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) != 0x02014b50)
                continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
            var name = Encoding.UTF8.GetString(bytes, offset + 46, nameLength);
            if (!lengths.TryGetValue(name, out var length))
                continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), length);
            var localOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 42, 4)));
            Assert.Equal(
                0x04034b50U,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(localOffset, 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(localOffset + 22, 4), length);
            patched.Add(name);
        }
        Assert.Equal(lengths.Keys.OrderBy(x => x), patched.OrderBy(x => x));
        return bytes;
    }

    private TargetSnapshot SnapshotTarget() => SnapshotTarget(_target);

    private static TargetSnapshot SnapshotTarget(string target) =>
        !Directory.Exists(target)
            ? new TargetSnapshot(false, new Dictionary<string, string>(StringComparer.Ordinal))
            : new TargetSnapshot(
                true,
                Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                    .ToDictionary(
                        path => Path.GetRelativePath(target, path),
                        path => Sha256(File.ReadAllBytes(path)),
                        StringComparer.Ordinal));

    private void AssertTargetUnchanged(TargetSnapshot before) =>
        AssertTargetUnchanged(_target, before);

    private static void AssertTargetUnchanged(string target, TargetSnapshot before)
    {
        Assert.Equal(before.Exists, Directory.Exists(target));
        var after = SnapshotTarget(target);
        Assert.Equal(before.Exists, after.Exists);
        Assert.Equal(
            before.Files.OrderBy(row => row.Key),
            after.Files.OrderBy(row => row.Key));
    }

    private string TargetForMatrix(bool existingTarget) =>
        existingTarget ? _target : Path.Combine(_root, "absent-matrix-target");

    private static string WithTrailingSeparator(string target, bool alternateSeparator) =>
        target + (alternateSeparator
            ? Path.AltDirectorySeparatorChar
            : Path.DirectorySeparatorChar);

    private string ObserveOnlyPrivateStage(string target)
    {
        var stages = Directory.EnumerateFileSystemEntries(
            _root,
            ".vt2-source-exact-stage-*",
            SearchOption.TopDirectoryOnly).ToArray();
        var stage = Assert.Single(stages);
        AssertSameVolumeSibling(stage, target);
        return stage;
    }

    private static void AssertSameVolumeSibling(string stage, string target)
    {
        var canonicalStage = Path.GetFullPath(stage);
        var canonicalTarget = Path.GetFullPath(target);
        Assert.Equal(
            Path.GetPathRoot(canonicalTarget),
            Path.GetPathRoot(canonicalStage),
            ignoreCase: true);
        Assert.Equal(
            Directory.GetParent(canonicalTarget)!.FullName,
            Directory.GetParent(canonicalStage)!.FullName,
            ignoreCase: true);
        Assert.False(canonicalStage.StartsWith(
            canonicalTarget + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(canonicalStage.StartsWith(
            canonicalTarget + Path.AltDirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    private void AssertNoPrivateStageRemains() => Assert.Empty(
        Directory.EnumerateFileSystemEntries(
            _root,
            ".vt2-source-exact-stage-*",
            SearchOption.TopDirectoryOnly));

    private static byte[] ArchiveFixture(string name) => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryArchives",
        name));

    private static string RecoveryFixture(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryRecords",
        name));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed record ZipSpec(
        string Name,
        byte[] Bytes,
        int? ExternalAttributes = null);

    private sealed record TargetSnapshot(
        bool Exists,
        IReadOnlyDictionary<string, string> Files);

    private sealed class ByteSource : ISourceExactArchiveSource
    {
        private readonly byte[] _bytes;
        private readonly long? _declaredLength;

        internal ByteSource(byte[] bytes, long? declaredLength)
        {
            _bytes = bytes;
            _declaredLength = declaredLength;
        }

        internal int Calls { get; private set; }

        public Task<SourceExactArchiveDownload> OpenReadAsync(
            SourceExactRecoveryArtifact artifact,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SourceExactArchiveDownload(
                new MemoryStream(_bytes, writable: false),
                _declaredLength,
                new Uri("https://release-assets.githubusercontent.com/fixture")));
        }
    }

    private sealed class StreamSource : ISourceExactArchiveSource
    {
        private readonly Func<Stream> _factory;
        private readonly long? _declaredLength;

        internal StreamSource(Func<Stream> factory, long? declaredLength)
        {
            _factory = factory;
            _declaredLength = declaredLength;
        }

        internal int Calls { get; private set; }

        public Task<SourceExactArchiveDownload> OpenReadAsync(
            SourceExactRecoveryArtifact artifact,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SourceExactArchiveDownload(
                _factory(),
                _declaredLength,
                new Uri("https://release-assets.githubusercontent.com/fixture")));
        }
    }

    private sealed class ThrowingSource : ISourceExactArchiveSource
    {
        private readonly SourceExactArchiveSourceFailure _failure;

        internal ThrowingSource(SourceExactArchiveSourceFailure failure) => _failure = failure;

        public Task<SourceExactArchiveDownload> OpenReadAsync(
            SourceExactRecoveryArtifact artifact,
            CancellationToken cancellationToken) =>
            Task.FromException<SourceExactArchiveDownload>(
                new SourceExactArchiveSourceException(_failure, "fixture source failure"));
    }

    private sealed class ObservingSource : ISourceExactArchiveSource
    {
        private readonly Func<Stream> _factory;
        private readonly long? _declaredLength;
        private readonly Action _observe;

        internal ObservingSource(
            Func<Stream> factory,
            long? declaredLength,
            Action observe)
        {
            _factory = factory;
            _declaredLength = declaredLength;
            _observe = observe;
        }

        public Task<SourceExactArchiveDownload> OpenReadAsync(
            SourceExactRecoveryArtifact artifact,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _observe();
            return Task.FromResult(new SourceExactArchiveDownload(
                _factory(),
                _declaredLength,
                new Uri("https://release-assets.githubusercontent.com/fixture")));
        }
    }

    private sealed class RecordingStream : MemoryStream
    {
        internal RecordingStream(byte[] bytes) : base(bytes, writable: false) { }
        internal int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var pending = base.ReadAsync(buffer, cancellationToken);
            if (pending.IsCompletedSuccessfully)
            {
                var read = pending.Result;
                BytesRead += read;
                return ValueTask.FromResult(read);
            }
            return Awaited(pending);
        }

        private async ValueTask<int> Awaited(ValueTask<int> pending)
        {
            var read = await pending;
            BytesRead += read;
            return read;
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

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;

        internal SingleResponseHandler(byte[] bytes) => _bytes = bytes;

        internal int Calls { get; private set; }
        internal Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bytes),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }
}
