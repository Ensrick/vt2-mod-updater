using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public sealed class SourceExactDirectoryTransactionAdversarialTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "vt2-source-exact-adversarial-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;

    public SourceExactDirectoryTransactionAdversarialTests()
    {
        Directory.CreateDirectory(_root);
        _target = Path.Combine(_root, "103712896117");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("lock-acquired")]
    [InlineData("before-stage-guard")]
    [InlineData("stage-guarded")]
    public void RealProcessDeathBeforeFirstWitnessLeavesOnlyInertStageEvidence(
        string checkpoint)
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var before = SourceExactTransactionTestFixture.Snapshot(_target);
        var stagePath = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        SourceExactTransactionTestFixture.WriteRawStage(stagePath);

        RunChildToHardDeath(stagePath, checkpoint);
        var result = new SourceExactDirectoryTransaction().Recover(_target);

        Assert.Equal(SourceExactRecoveryResult.NothingToRecover, result);
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
        Assert.True(Directory.Exists(stagePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _root, ".vt2-source-exact-backup-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _root, ".vt2-source-exact-journal-*"));
    }

    [Theory]
    [InlineData("witness-0-temp", false)]
    [InlineData("witness-0-published", false)]
    [InlineData("prepared", false)]
    [InlineData("before-prior-rename", false)]
    [InlineData("after-prior-rename", false)]
    [InlineData("witness-1-temp", false)]
    [InlineData("witness-1-published", false)]
    [InlineData("prior-moved", false)]
    [InlineData("before-stage-rename", false)]
    [InlineData("after-stage-rename", true)]
    [InlineData("witness-2-temp", true)]
    [InlineData("witness-2-published", true)]
    [InlineData("stage-promoted", true)]
    [InlineData("witness-3-temp", true)]
    [InlineData("witness-3-published", true)]
    [InlineData("committed", true)]
    [InlineData("cleanup-backup-file-0", true)]
    [InlineData("cleanup-backup-file-1", true)]
    [InlineData("cleanup-backup-directory", true)]
    [InlineData("cleanup-witness-3", true)]
    [InlineData("cleanup-witness-2", true)]
    [InlineData("cleanup-witness-1", true)]
    [InlineData("cleanup-witness-0", true)]
    public void RealProcessDeathIsRecoverableAtEveryCommitWindow(
        string checkpoint,
        bool expectCommitted)
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var stagePath = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        SourceExactTransactionTestFixture.WriteRawStage(stagePath);

        RunChildToHardDeath(stagePath, checkpoint);
        var result = new SourceExactDirectoryTransaction().Recover(_target);

        if (expectCommitted)
        {
            Assert.True(result is SourceExactRecoveryResult.CommittedRecovered or
                SourceExactRecoveryResult.NothingToRecover);
            Assert.True(File.Exists(Path.Combine(_target, "modx.mod")));
            Assert.False(File.Exists(Path.Combine(_target, "old.mod_bundle")));
        }
        else
        {
            Assert.Equal(SourceExactRecoveryResult.RolledBack, result);
            Assert.True(File.Exists(Path.Combine(_target, "old.mod_bundle")));
            Assert.False(File.Exists(Path.Combine(_target, "modx.mod")));
        }
        AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("cleanup-stage-file-0")]
    [InlineData("cleanup-stage-file-1")]
    [InlineData("cleanup-stage-file-2")]
    [InlineData("cleanup-stage-file-3")]
    [InlineData("cleanup-stage-file-4")]
    [InlineData("cleanup-stage-directory")]
    public void RealProcessDeathIsRecoverableAtEveryRollbackCleanupLeaf(
        string checkpoint)
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var stagePath = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        SourceExactTransactionTestFixture.WriteRawStage(stagePath);

        RunChildToHardDeath(stagePath, "rollback:" + checkpoint);
        var result = new SourceExactDirectoryTransaction().Recover(_target);

        Assert.Equal(SourceExactRecoveryResult.RolledBack, result);
        Assert.True(File.Exists(Path.Combine(_target, "old.mod_bundle")));
        Assert.False(File.Exists(Path.Combine(_target, "modx.mod")));
        AssertNoTransactionArtifacts();
    }

    [Fact]
    public void SameParentLeaseSerializesSeparateProcesses()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var childStage = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        SourceExactTransactionTestFixture.WriteRawStage(childStage);
        using var child = StartHarness(childStage, "hold-lock");
        Assert.True(SpinWait.SpinUntil(
            () => File.Exists(Path.Combine(_root, "lock.ready")),
            TimeSpan.FromSeconds(10)));

        using var contender = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction(lockTimeout: TimeSpan.FromMilliseconds(100))
                .Install(contender, SourceExactTransactionTestFixture.Artifact()));

        Assert.Equal(SourceExactTransactionFailure.Locked, exception.Failure);
        Assert.True(File.Exists(Path.Combine(_target, "old.mod_bundle")));
        child.Kill(entireProcessTree: true);
        Assert.True(child.WaitForExit(10_000));
    }

    [Fact]
    public void OneCharacterTargetExercisesMinimumNativeRenameAllocation()
    {
        var target = Path.Combine(_root, "x");
        SourceExactTransactionTestFixture.WritePriorTarget(target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, target);

        var result = new SourceExactDirectoryTransaction().Install(
            stage, SourceExactTransactionTestFixture.Artifact());

        Assert.Equal(target, result.TargetPath);
        Assert.True(File.Exists(Path.Combine(target, "modx.mod")));
        Assert.False(File.Exists(Path.Combine(target, "old.mod_bundle")));
    }

    [Fact]
    public void HostedShapeExtendedPathsSupportInstallAndRecovery()
    {
        const int hostedShapeParentLength = 150;
        var paddingLength = hostedShapeParentLength - _root.Length - 1;
        Assert.InRange(paddingLength, 1, 200);
        var longParent = Path.Combine(_root, new string('p', paddingLength));
        Directory.CreateDirectory(longParent);
        var target = Path.Combine(longParent, "103712896117");
        var stageProbe = Path.Combine(
            longParent, ".vt2-source-exact-stage-" + new string('a', 32));
        var lockProbe = Path.Combine(
            longParent, ".vt2-source-exact-lock-" + new string('b', 64) + ".lck");
        var witnessProbe = Path.Combine(
            longParent,
            ".vt2-source-exact-journal-" + new string('c', 64) + "-" +
            new string('d', 32) + "-0.txn.partial-" + new string('e', 16));
        Assert.True(stageProbe.Length < 260);
        Assert.True(lockProbe.Length < 260);
        Assert.True(witnessProbe.Length > 260);
        SourceExactTransactionTestFixture.WritePriorTarget(target);
        using (var stage = SourceExactTransactionTestFixture.CreateStage(longParent, target))
        {
            var result = new SourceExactDirectoryTransaction().Install(
                stage, SourceExactTransactionTestFixture.Artifact());
            Assert.Equal(Path.GetFullPath(target), result.TargetPath);
            Assert.True(File.Exists(Path.Combine(target, "modx.mod")));
        }

        using (var stage = SourceExactTransactionTestFixture.CreateStage(longParent, target))
        {
            var interrupted = new SourceExactDirectoryTransaction(checkpoint: point =>
            {
                if (point == "committed")
                    throw new SourceExactSimulatedCrashException(point);
            });
            Assert.Throws<SourceExactSimulatedCrashException>(() => interrupted.Install(
                stage, SourceExactTransactionTestFixture.Artifact()));
        }

        Assert.Equal(
            SourceExactRecoveryResult.CommittedRecovered,
            new SourceExactDirectoryTransaction().Recover(target));
        using (var lease = SourceExactTransactionFileSystem.OpenDirectory(target))
        {
            lease.RequireCurrentPath();
            Assert.Equal(SourceExactTransactionFileSystem.Normalize(target), lease.CurrentPath);
        }
        Assert.True(File.Exists(Path.Combine(target, "modx.mod")));
        Assert.Empty(Directory.EnumerateFiles(
            longParent, ".vt2-source-exact-journal-*"));
        Assert.Empty(Directory.EnumerateDirectories(
            longParent, ".vt2-source-exact-backup-*"));
        Assert.Empty(Directory.EnumerateDirectories(
            longParent, ".vt2-source-exact-stage-*"));
    }

    [Fact]
    public void NativePathCanonicalizesDriveAndUncProofPaths()
    {
        Assert.Equal(
            "\\\\?\\C:\\source\\mods",
            SourceExactTransactionFileSystem.NativePath("C:\\source\\mods"));
        Assert.Equal(
            "C:\\source\\mods",
            SourceExactTransactionFileSystem.Normalize("\\\\?\\C:\\source\\mods"));
        Assert.Equal(
            "C:\\source\\mods",
            SourceExactTransactionFileSystem.Normalize(
                SourceExactTransactionFileSystem.Normalize("\\\\?\\C:\\source\\mods")));
        Assert.Equal(
            "\\\\?\\UNC\\server\\share\\mods",
            SourceExactTransactionFileSystem.NativePath("\\\\server\\share\\mods"));
        Assert.Equal(
            "\\\\server\\share\\mods",
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC\\server\\share\\mods"));
        Assert.Equal(
            "\\\\server\\share\\mods",
            SourceExactTransactionFileSystem.Normalize(
                SourceExactTransactionFileSystem.Normalize(
                    "\\\\?\\UNC\\server\\share\\mods")));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize("\\\\?\\C:\\source\\mods."));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize("\\\\?\\C:\\source\\mods "));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize("\\\\?\\C:\\source.\\mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC\\server\\share\\mods."));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC\\server\\share\\mods "));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC\\server\\share.\\mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\C:\\source\\a\\..\\mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC\\server\\share\\source\\a\\..\\mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\C:\\source\\\\mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC\\server\\share\\source\\\\mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\C:/source/mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\UNC/server/share/mods"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize(
                "\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy1"));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.Normalize("\\\\.\\PhysicalDrive0"));
    }

    [Fact]
    public void ArchiveMismatchRefusesBeforeTargetOrJournalMutation()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var before = SourceExactTransactionTestFixture.Snapshot(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(
            _root, _target, new string('b', 64));

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Install(
                stage, SourceExactTransactionTestFixture.Artifact()));

        Assert.Equal(SourceExactTransactionFailure.StageChanged, exception.Failure);
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
        Assert.Empty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void MarkerMutationAfterStageVerificationRefusesBeforeTargetMutation()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var before = SourceExactTransactionTestFixture.Snapshot(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        File.WriteAllText(
            Path.Combine(stage.StageDirectory, SourceExactZipStager.VersionMarkerFilename),
            "different");

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Install(
                stage, SourceExactTransactionTestFixture.Artifact()));

        Assert.Equal(SourceExactTransactionFailure.StageChanged, exception.Failure);
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
    }

    [Fact]
    public void CanonicalMutationBeforeFinalGuardPreservesEvidenceAndTarget()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var before = SourceExactTransactionTestFixture.Snapshot(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "before-stage-guard")
                File.WriteAllText(Path.Combine(stage.StageDirectory, "modx.mod"), "mutated");
        });

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            transaction.Install(stage, SourceExactTransactionTestFixture.Artifact()));

        Assert.Equal(SourceExactTransactionFailure.StageChanged, exception.Failure);
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
        Assert.Equal("mutated", File.ReadAllText(Path.Combine(stage.StageDirectory, "modx.mod")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void StagePathReplacementAfterSidecarParentPinNeverWritesIntoReplacement()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var before = SourceExactTransactionTestFixture.Snapshot(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var original = stage.StageDirectory;
        var moved = original + "-moved";
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "sidecar-parent-pinned") return;
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            File.WriteAllText(Path.Combine(original, "preserve.txt"), "preserve");
        });

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            transaction.Install(stage, SourceExactTransactionTestFixture.Artifact()));

        Assert.Equal(SourceExactTransactionFailure.StageChanged, exception.Failure);
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(original, "preserve.txt")));
        Assert.False(File.Exists(Path.Combine(original, SourceExactInstalledState.Filename)));
        Assert.True(File.Exists(Path.Combine(moved, SourceExactInstalledState.Filename)));
        Assert.Empty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void TransferredStageCannotBeTransferredTwiceOrDeleteAReplacement()
    {
        var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var original = stage.StageDirectory;
        _ = stage.TransferOwnership(SourceExactTransactionTestFixture.Artifact());
        Assert.Throws<InvalidOperationException>(() =>
            stage.TransferOwnership(SourceExactTransactionTestFixture.Artifact()));
        var moved = original + "-moved";
        Directory.Move(original, moved);
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "preserve.txt"), "preserve");

        stage.Dispose();

        Assert.Equal("preserve", File.ReadAllText(Path.Combine(original, "preserve.txt")));
        Assert.True(File.Exists(Path.Combine(moved, "modx.mod")));
    }

    [Fact]
    public void TransferRejectsCoordinateDriftWithoutConsumingTheLease()
    {
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var artifact = SourceExactTransactionTestFixture.Artifact();

        Assert.Throws<InvalidDataException>(() =>
            stage.TransferOwnership(artifact with { AssetId = artifact.AssetId + 1 }));
        using var accepted = stage.TransferOwnership(artifact);

        accepted.Lease.RequireCurrentPath();
        Assert.Equal(stage.VerifiedSnapshot.Identity, accepted.Lease.Identity);
    }

    [Fact]
    public void ReplacedStageBeforeConsumptionIsPreservedAndRejected()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var original = stage.StageDirectory;
        var moved = original + "-moved";
        Directory.Move(original, moved);
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "preserve.txt"), "preserve");

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Install(
                stage, SourceExactTransactionTestFixture.Artifact()));
        stage.Dispose();

        Assert.Equal(SourceExactTransactionFailure.StageChanged, exception.Failure);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(original, "preserve.txt")));
        Assert.True(File.Exists(Path.Combine(moved, "modx.mod")));
    }

    [Fact]
    public void FileAndDirectoryAlternateStreamsAreRejected()
    {
        var fileStage = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        SourceExactTransactionTestFixture.WriteRawStage(fileStage);
        File.WriteAllText(Path.Combine(fileStage, "modx.mod") + ":payload", "ads");
        Assert.ThrowsAny<Exception>(() => ConstructStage(fileStage));

        var directoryStage = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        SourceExactTransactionTestFixture.WriteRawStage(directoryStage);
        File.WriteAllText(directoryStage + ":payload", "ads");
        Assert.ThrowsAny<Exception>(() => ConstructStage(directoryStage));
    }

    [Fact]
    public void OversizedSparseLeafIsRejectedByMetadataBeforeContentHashing()
    {
        var stagePath = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagePath);
        var oversized = Path.Combine(stagePath, "oversized.mod_bundle");
        using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(SourceExactZipStager.MaximumOutputBytes + 1);

        Assert.Throws<InvalidDataException>(() =>
            SourceExactTransactionFileSystem.GuardDirectory(stagePath));
    }

    [Fact]
    public void ReadOnlyPriorIsRejectedBeforePreparedWitness()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        var prior = Path.Combine(_target, "old.mod_bundle");
        File.SetAttributes(prior, FileAttributes.ReadOnly);
        try
        {
            using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);

            var exception = Assert.Throws<SourceExactTransactionException>(() =>
                new SourceExactDirectoryTransaction().Install(
                    stage, SourceExactTransactionTestFixture.Artifact()));

            Assert.Equal(SourceExactTransactionFailure.FileSystem, exception.Failure);
            Assert.True(File.Exists(prior));
            Assert.Empty(Directory.EnumerateFiles(
                _root, ".vt2-source-exact-journal-*"));
            Assert.Empty(Directory.EnumerateDirectories(
                _root, ".vt2-source-exact-backup-*"));
        }
        finally
        {
            if (File.Exists(prior)) File.SetAttributes(prior, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("COM\u00b9")]
    [InlineData("COM\u00b2.txt")]
    [InlineData("COM\u00b3.mod")]
    [InlineData("LPT\u00b9")]
    [InlineData("LPT\u00b2.txt")]
    [InlineData("LPT\u00b3.mod")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$.txt")]
    [InlineData("CLOCK$.mod")]
    public void SuperscriptWin32DevicesAreRejected(string leaf)
    {
        Assert.False(SourceExactTransactionFileSystem.SafeLeaf(leaf));
    }

    [Theory]
    [InlineData(0U, false)]
    [InlineData(2U, false)]
    [InlineData(3U, true)]
    [InlineData(4U, false)]
    [InlineData(6U, false)]
    public void OnlyLocalFixedVolumesEnterTheNtfsTransactionContract(
        uint driveType,
        bool expected)
    {
        Assert.Equal(expected,
            SourceExactTransactionFileSystem.IsLocalFixedDriveType(driveType));
    }

    [Fact]
    public void AliasedParentIsRejectedBeforePersistentLockCreation()
    {
        var physical = Path.Combine(_root, "physical");
        var alias = Path.Combine(_root, "alias");
        Directory.CreateDirectory(physical);
        using var junction = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{alias}\" \"{physical}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        Assert.True(junction.WaitForExit(10_000));
        Assert.Equal(0, junction.ExitCode);
        try
        {
            var exception = Assert.Throws<SourceExactTransactionException>(() =>
                new SourceExactDirectoryTransaction().Recover(Path.Combine(alias, "1234567890")));

            Assert.Equal(SourceExactTransactionFailure.InvalidTarget, exception.Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                physical, ".vt2-source-exact-lock-*"));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
        }
    }

    [Fact]
    public void InstalledStateRejectsSelfInconsistentFingerprintAndLengths()
    {
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var valid = stage.InstalledState;
        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Serialize(
            valid with { OutputFingerprint = new string('a', 64) }));
        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Serialize(
            valid with { Outputs = Array.Empty<SourceExactInstalledOutput>() }));
        var zero = valid.Outputs.ToArray();
        zero[0] = zero[0] with { Length = 0 };
        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Serialize(
            valid with { Outputs = zero }));
    }

    [Fact]
    public void InstalledStateRejectsPhase3CountOverflow()
    {
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var rows = Enumerable.Range(0, SourceExactZipStager.MaximumEntries)
            .Select(index => new SourceExactInstalledOutput(
                $"f{index:D4}.mod_bundle",
                1,
                SourceExactTransactionTestFixture.Sha256(new byte[] { 1 })))
            .ToArray();
        var fingerprint = RecoveryRecordContract.ComputeOutputFingerprint(rows
            .Select(row => new RecoveryOutputFile(
                row.Filename, row.Length, row.Sha256, ""))
            .ToArray());

        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Serialize(
            stage.InstalledState with
            {
                Outputs = rows,
                OutputFingerprint = fingerprint
            }));
    }

    [Fact]
    public void InstalledOutputMapMustEqualPhysicalSnapshot()
    {
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var result = new SourceExactDirectoryTransaction().Install(
            stage, SourceExactTransactionTestFixture.Artifact());
        var document = result.InstalledState;
        SourceExactInstalledState.RequireSnapshotBinding(document, result.Snapshot);
        var rows = result.Snapshot.Files.ToArray();
        var outputIndex = Array.FindIndex(rows,
            row => row.Name == document.Outputs[0].Filename);
        rows[outputIndex] = rows[outputIndex] with { Length = rows[outputIndex].Length + 1 };

        Assert.Throws<InvalidDataException>(() =>
            SourceExactInstalledState.RequireSnapshotBinding(
                document,
                result.Snapshot with { Files = rows }));

        Assert.Throws<InvalidDataException>(() =>
            SourceExactInstalledState.RequireSnapshotBinding(
                document,
                result.Snapshot with
                {
                    Files = result.Snapshot.Files
                        .Where((_, index) => index != outputIndex).ToArray()
                }));
        Assert.Throws<InvalidDataException>(() =>
            SourceExactInstalledState.RequireSnapshotBinding(
                document,
                result.Snapshot with
                {
                    Files = result.Snapshot.Files.Append(new ExactFileSnapshot(
                        "foreign.mod_bundle",
                        1,
                        SourceExactTransactionTestFixture.Sha256(new byte[] { 1 }),
                        result.Snapshot.Identity)).ToArray()
                }));
        rows = result.Snapshot.Files.ToArray();
        rows[outputIndex] = rows[outputIndex] with { Sha256 = new string('0', 64) };
        Assert.Throws<InvalidDataException>(() =>
            SourceExactInstalledState.RequireSnapshotBinding(
                document,
                result.Snapshot with { Files = rows }));
    }

    [Fact]
    public void RecoveryInvokesInstalledOutputMapBinding()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "committed") throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() => transaction.Install(
            stage, SourceExactTransactionTestFixture.Artifact()));

        var sidecarPath = Path.Combine(_target, SourceExactInstalledState.Filename);
        var document = SourceExactInstalledState.Parse(File.ReadAllBytes(sidecarPath));
        var outputs = document.Outputs.Select(row => row with { }).ToArray();
        outputs[0] = outputs[0] with { Length = outputs[0].Length + 1 };
        var fingerprint = RecoveryRecordContract.ComputeOutputFingerprint(outputs
            .Select(row => new RecoveryOutputFile(
                row.Filename, row.Length, row.Sha256, ""))
            .ToArray());
        var changedBytes = SourceExactInstalledState.Serialize(document with
        {
            Outputs = Array.AsReadOnly(outputs),
            OutputFingerprint = fingerprint
        });
        File.WriteAllBytes(sidecarPath, changedBytes);
        foreach (var journal in Directory.EnumerateFiles(
                     _root, ".vt2-source-exact-journal-*.txn"))
            RewriteJournalSidecarProof(journal, changedBytes);

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Recover(_target));

        Assert.Equal(SourceExactTransactionFailure.InstalledStateInvalid, exception.Failure);
        Assert.True(File.Exists(Path.Combine(_target, "modx.mod")));
    }

    [Fact]
    public void GuardBlocksWriterAfterFinalStageProof()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var blocked = false;
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "stage-guarded") return;
            try
            {
                File.WriteAllText(Path.Combine(stage.StageDirectory, "modx.mod"), "mutate");
            }
            catch (IOException) { blocked = true; }
        });

        _ = transaction.Install(stage, SourceExactTransactionTestFixture.Artifact());

        Assert.True(blocked);
        Assert.Equal("fixture descriptor\n", File.ReadAllText(Path.Combine(_target, "modx.mod")));
    }

    [Fact]
    public void CommittedTargetRemainsMutationPinnedThroughEvidenceCleanup()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var writeBlocked = false;
        var deleteBlocked = false;
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "commit-target-pinned") return;
            try { File.WriteAllText(Path.Combine(_target, "modx.mod"), "mutate"); }
            catch (IOException) { writeBlocked = true; }
            try { File.Delete(Path.Combine(_target, "modx.mod")); }
            catch (IOException) { deleteBlocked = true; }
        });

        _ = transaction.Install(stage, SourceExactTransactionTestFixture.Artifact());

        Assert.True(writeBlocked);
        Assert.True(deleteBlocked);
        Assert.Equal("fixture descriptor\n", File.ReadAllText(Path.Combine(_target, "modx.mod")));
        AssertNoTransactionArtifacts();
    }

    [Theory]
    [InlineData("committed", "recovery-target-new-pinned", "modx.mod", true)]
    [InlineData("prepared", "recovery-target-prior-pinned", "old.mod_bundle", false)]
    public void RecoveryPinsAcceptedTargetUntilJournalAuthorityIsRetired(
        string crashPoint,
        string recoveryPoint,
        string acceptedLeaf,
        bool committed)
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var interrupted = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == crashPoint) throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() => interrupted.Install(
            stage, SourceExactTransactionTestFixture.Artifact()));
        var writeBlocked = false;
        var deleteBlocked = false;
        var recovery = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != recoveryPoint) return;
            try { File.WriteAllText(Path.Combine(_target, acceptedLeaf), "mutate"); }
            catch (IOException) { writeBlocked = true; }
            try { File.Delete(Path.Combine(_target, acceptedLeaf)); }
            catch (IOException) { deleteBlocked = true; }
        });

        var result = recovery.Recover(_target);

        Assert.Equal(
            committed
                ? SourceExactRecoveryResult.CommittedRecovered
                : SourceExactRecoveryResult.RolledBack,
            result);
        Assert.True(writeBlocked);
        Assert.True(deleteBlocked);
        AssertNoTransactionArtifacts();
    }

    [Fact]
    public void NewLeafBeforeRecoveryCleanupFailsClosedWithRollbackAuthorityIntact()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var interrupted = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "committed") throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() => interrupted.Install(
            stage, SourceExactTransactionTestFixture.Artifact()));
        var injected = false;
        var blocked = false;
        var recovery = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "recovery-target-new-pinned") return;
            try
            {
                File.WriteAllText(Path.Combine(_target, "foreign.mod_bundle"), "x");
                injected = true;
            }
            catch (IOException) { blocked = true; }
        });

        SourceExactTransactionException? failure = null;
        SourceExactRecoveryResult? result = null;
        try { result = recovery.Recover(_target); }
        catch (SourceExactTransactionException ex) { failure = ex; }

        if (blocked)
        {
            Assert.False(injected);
            Assert.Equal(SourceExactRecoveryResult.CommittedRecovered, result);
            AssertNoTransactionArtifacts();
        }
        else
        {
            Assert.True(injected);
            Assert.NotNull(failure);
            Assert.Equal(SourceExactTransactionFailure.ForeignMutation, failure!.Failure);
            Assert.NotEmpty(Directory.EnumerateDirectories(
                _root, ".vt2-source-exact-backup-*"));
            Assert.NotEmpty(Directory.EnumerateFiles(
                _root, ".vt2-source-exact-journal-*"));
        }
    }

    [Fact]
    public void NewLeafRaceCannotEnterThePromotedSnapshot()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var blocked = false;
        var injected = false;
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "stage-guarded") return;
            try
            {
                File.WriteAllText(Path.Combine(stage.StageDirectory, "foreign.mod_bundle"), "x");
                injected = true;
            }
            catch (IOException) { blocked = true; }
        });

        SourceExactTransactionException? failure = null;
        try
        {
            _ = transaction.Install(stage, SourceExactTransactionTestFixture.Artifact());
        }
        catch (SourceExactTransactionException ex)
        {
            failure = ex;
        }

        if (blocked)
        {
            Assert.Null(failure);
            Assert.False(File.Exists(Path.Combine(_target, "foreign.mod_bundle")));
        }
        else
        {
            Assert.NotNull(failure);
            Assert.True(injected);
            Assert.True(File.Exists(Path.Combine(_target, "old.mod_bundle")));
            Assert.False(File.Exists(Path.Combine(_target, "modx.mod")));
        }
    }

    [Fact]
    public void CleanupGuardBlocksMutationBetweenProofAndDeletion()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var blocked = false;
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "cleanup-backup-file-0") return;
            var backup = Assert.Single(Directory.EnumerateDirectories(
                _root, ".vt2-source-exact-backup-*"));
            try
            {
                File.WriteAllText(Path.Combine(backup, "vt2updater_version.txt"), "mutate");
            }
            catch (IOException) { blocked = true; }
        });

        _ = transaction.Install(stage, SourceExactTransactionTestFixture.Artifact());

        Assert.True(blocked);
        AssertNoTransactionArtifacts();
    }

    [Fact]
    public void MoreThanFourOrOversizeJournalWitnessesFailBeforeAllocation()
    {
        var key = SourceExactTransactionTestFixture.Sha256(Encoding.UTF8.GetBytes(
            Path.GetFileName(_target).ToUpperInvariant()));
        for (var index = 0; index < 5; index++)
            File.WriteAllText(Path.Combine(
                _root,
                $".vt2-source-exact-journal-{key}-{new string('a', 32)}-{index}.txn"),
                "x");
        var tooMany = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Recover(_target));
        Assert.Equal(SourceExactTransactionFailure.JournalInvalid, tooMany.Failure);
        foreach (var path in Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"))
            File.Delete(path);

        var oversized = Path.Combine(
            _root,
            $".vt2-source-exact-journal-{key}-{new string('b', 32)}-0.txn");
        using (var file = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
            file.SetLength(16L * 1024 * 1024 + 1);
        var tooLarge = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Recover(_target));
        Assert.Equal(SourceExactTransactionFailure.JournalInvalid, tooLarge.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void IncompleteAtomicPartialIsDeletedWithoutAuthorizingRecovery(int length)
    {
        var key = SourceExactTransactionTestFixture.Sha256(Encoding.UTF8.GetBytes(
            Path.GetFileName(_target).ToUpperInvariant()));
        var partial = Path.Combine(
            _root,
            $".vt2-source-exact-journal-{key}-{new string('c', 32)}-0.txn.partial-{new string('d', 16)}");
        File.WriteAllBytes(partial, Enumerable.Repeat((byte)'x', length).ToArray());

        var result = new SourceExactDirectoryTransaction().Recover(_target);

        Assert.Equal(SourceExactRecoveryResult.NothingToRecover, result);
        Assert.False(File.Exists(partial));
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public void CompleteForgedJournalCannotAuthorizeMutation()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "prepared") throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() => transaction.Install(
            stage, SourceExactTransactionTestFixture.Artifact()));
        var journal = Assert.Single(Directory.EnumerateFiles(
            _root, ".vt2-source-exact-journal-*.txn"));
        var text = File.ReadAllText(journal);
        var forgedTarget = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Path.Combine(_root, "different-target")));
        var lines = text.Split('\n').ToList();
        var targetIndex = lines.FindIndex(line => line.StartsWith("target=", StringComparison.Ordinal));
        lines[targetIndex] = "target=" + forgedTarget;
        File.WriteAllText(journal, Rechecksum(lines));
        var before = SourceExactTransactionTestFixture.Snapshot(_target);

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Recover(_target));

        Assert.Equal(SourceExactTransactionFailure.JournalInvalid, exception.Failure);
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
    }

    [Fact]
    public void ReplayedCompleteJournalIsOnlyIdempotent()
    {
        SourceExactTransactionTestFixture.WritePriorTarget(_target);
        using var stage = SourceExactTransactionTestFixture.CreateStage(_root, _target);
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "prepared") throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() => transaction.Install(
            stage, SourceExactTransactionTestFixture.Artifact()));
        var journal = Assert.Single(Directory.EnumerateFiles(
            _root, ".vt2-source-exact-journal-*.txn"));
        var name = Path.GetFileName(journal);
        var bytes = File.ReadAllBytes(journal);
        Assert.Equal(SourceExactRecoveryResult.RolledBack,
            new SourceExactDirectoryTransaction().Recover(_target));
        var before = SourceExactTransactionTestFixture.Snapshot(_target);
        File.WriteAllBytes(Path.Combine(_root, name), bytes);

        Assert.Equal(SourceExactRecoveryResult.RolledBack,
            new SourceExactDirectoryTransaction().Recover(_target));
        Assert.Equal(before, SourceExactTransactionTestFixture.Snapshot(_target));
        AssertNoTransactionArtifacts();
    }

    private void RunChildToHardDeath(string stagePath, string checkpoint)
    {
        using var child = StartHarness(stagePath, checkpoint);
        Assert.True(child.WaitForExit(15_000));
        Assert.Equal(197, child.ExitCode);
    }

    private SourceExactZipStage ConstructStage(string stagePath)
    {
        var artifact = SourceExactTransactionTestFixture.Artifact();
        return new SourceExactZipStage(
            stagePath,
            _target,
            artifact,
            artifact.AssetSha256,
            SourceExactTransactionTestFixture.Outputs(stagePath));
    }

    private Process StartHarness(string stagePath, string checkpoint)
    {
        var path = SourceExactTransactionTestFixture.HarnessPath();
        Assert.True(File.Exists(path), $"missing crash harness: {path}");
        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("install");
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(_target);
        start.ArgumentList.Add(stagePath);
        start.ArgumentList.Add(checkpoint);
        return Process.Start(start)!;
    }

    private void AssertNoTransactionArtifacts()
    {
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _root, ".vt2-source-exact-stage-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _root, ".vt2-source-exact-backup-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            _root, ".vt2-source-exact-journal-*"));
    }

    private static string Rechecksum(List<string> lines)
    {
        var checksum = lines.FindIndex(line =>
            line.StartsWith("checksum=", StringComparison.Ordinal));
        var body = string.Join('\n', lines.Take(checksum)) + "\n";
        lines[checksum] = "checksum=" + SourceExactTransactionTestFixture.Sha256(
            Encoding.UTF8.GetBytes(body));
        return string.Join('\n', lines);
    }

    private static void RewriteJournalSidecarProof(string journal, byte[] sidecarBytes)
    {
        var lines = File.ReadAllText(journal).Split('\n').ToList();
        var encodedName = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(SourceExactInstalledState.Filename));
        var rowIndex = lines.FindIndex(line =>
            line.StartsWith("new_file=" + encodedName + "|", StringComparison.Ordinal));
        Assert.True(rowIndex >= 0);
        var fields = lines[rowIndex]["new_file=".Length..].Split('|');
        Assert.Equal(4, fields.Length);
        fields[1] = sidecarBytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        fields[2] = SourceExactTransactionTestFixture.Sha256(sidecarBytes);
        lines[rowIndex] = "new_file=" + string.Join('|', fields);
        File.WriteAllText(journal, Rechecksum(lines));
    }
}
