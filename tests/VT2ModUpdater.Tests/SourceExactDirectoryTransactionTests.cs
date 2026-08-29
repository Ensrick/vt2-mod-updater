using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public sealed class SourceExactDirectoryTransactionTests : IDisposable
{
    private const string DescriptorSha256 =
        "6db3ae2ce8ed0d57f22fb35a5beaa8cb0ec35ec9d560b829e582dd4d63ea78f3";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "vt2-source-exact-transaction-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;

    public SourceExactDirectoryTransactionTests()
    {
        Directory.CreateDirectory(_root);
        _target = Path.Combine(_root, "103712896117");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ExistingTargetIsReplacedAsOneExactDirectory()
    {
        WritePriorTarget();
        var priorIdentity = File.ReadAllBytes(Path.Combine(_target, "old.mod_bundle"));
        using var stage = CreateStage();
        var artifact = Artifact();

        var result = new SourceExactDirectoryTransaction().Install(stage, artifact);

        Assert.Equal(Path.GetFullPath(_target), result.TargetPath);
        Assert.False(File.Exists(Path.Combine(_target, "old.mod_bundle")));
        Assert.True(File.Exists(Path.Combine(_target, "modx.mod")));
        Assert.Equal(new byte[] { 9, 8, 7 }, priorIdentity);
        var installedBytes = File.ReadAllBytes(Path.Combine(
            _target, SourceExactInstalledState.Filename));
        var installed = SourceExactInstalledState.Parse(installedBytes);
        Assert.Equal(SourceExactInstalledState.Authority, installed.Authority);
        Assert.Equal(artifact.Proof.Record.Source.Commit, installed.SourceCommit);
        Assert.Equal(3, installed.Outputs.Count);
        AssertNoArtifacts();
    }

    [Fact]
    public void AbsentTargetInstallsWithoutInventingPriorState()
    {
        using var stage = CreateStage();
        var result = new SourceExactDirectoryTransaction().Install(stage, Artifact());
        Assert.True(Directory.Exists(result.TargetPath));
        Assert.True(File.Exists(Path.Combine(result.TargetPath, SourceExactInstalledState.Filename)));
        AssertNoArtifacts();
    }

    [Theory]
    [InlineData("prepared", false)]
    [InlineData("after-prior-rename", false)]
    [InlineData("prior-moved", false)]
    [InlineData("after-stage-rename", true)]
    [InlineData("stage-promoted", true)]
    [InlineData("committed", true)]
    public void DurableRecoveryClosesEveryRenameWindow(
        string crashAt,
        bool expectCommitted)
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == crashAt) throw new SourceExactSimulatedCrashException(point);
        });

        Assert.Throws<SourceExactSimulatedCrashException>(() =>
            transaction.Install(stage, Artifact()));
        var recovered = new SourceExactDirectoryTransaction().Recover(_target);

        Assert.Equal(
            expectCommitted ? SourceExactRecoveryResult.CommittedRecovered : SourceExactRecoveryResult.RolledBack,
            recovered);
        Assert.Equal(expectCommitted, File.Exists(Path.Combine(_target, "modx.mod")));
        Assert.Equal(!expectCommitted, File.Exists(Path.Combine(_target, "old.mod_bundle")));
        AssertNoArtifacts();
    }

    [Theory]
    [InlineData("prepared", false)]
    [InlineData("prior-moved", false)]
    [InlineData("after-stage-rename", true)]
    [InlineData("stage-promoted", true)]
    [InlineData("committed", true)]
    public void AbsentTargetRecoveryClosesEveryApplicableCrashWindow(
        string crashAt,
        bool expectCommitted)
    {
        using var stage = CreateStage();
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == crashAt) throw new SourceExactSimulatedCrashException(point);
        });

        Assert.Throws<SourceExactSimulatedCrashException>(() =>
            transaction.Install(stage, Artifact()));
        var recovered = new SourceExactDirectoryTransaction().Recover(_target);

        Assert.Equal(
            expectCommitted ? SourceExactRecoveryResult.CommittedRecovered : SourceExactRecoveryResult.RolledBack,
            recovered);
        Assert.Equal(expectCommitted, Directory.Exists(_target));
        Assert.Equal(expectCommitted, File.Exists(Path.Combine(_target, "modx.mod")));
        AssertNoArtifacts();
    }

    [Fact]
    public void CancellationAfterPriorRenameRestoresExactPriorTarget()
    {
        WritePriorTarget();
        var before = Snapshot(_target);
        using var stage = CreateStage();
        using var cancellation = new CancellationTokenSource();
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "prior-moved") cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            transaction.Install(stage, Artifact(), cancellation.Token));

        Assert.Equal(before, Snapshot(_target));
        AssertNoArtifacts();
    }

    [Fact]
    public void ForeignJournalCollisionRefusesBeforeStageOrTargetMutation()
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var beforeTarget = Snapshot(_target);
        var beforeStage = Snapshot(stage.StageDirectory);
        File.WriteAllText(
            Path.Combine(_root, ".vt2-source-exact-journal-" +
                Sha256(Encoding.UTF8.GetBytes(
                    Path.GetFileName(_target).ToUpperInvariant())) +
                "-foreign-0.txn"),
            "forged");

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Install(stage, Artifact()));

        Assert.Equal(SourceExactTransactionFailure.JournalInvalid, exception.Failure);
        Assert.Equal(beforeTarget, Snapshot(_target));
        Assert.Equal(beforeStage, Snapshot(stage.StageDirectory));
    }

    [Fact]
    public void ForeignTargetAfterCrashIsPreservedAndRecoveryFailsClosed()
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "after-prior-rename")
                throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() =>
            transaction.Install(stage, Artifact()));
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_target, "foreign.txt"), "preserve me");

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Recover(_target));

        Assert.Equal(SourceExactTransactionFailure.ForeignMutation, exception.Failure);
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(_target, "foreign.txt")));
        Assert.NotEmpty(Directory.EnumerateDirectories(_root, ".vt2-source-exact-backup-*"));
        Assert.NotEmpty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void IncompleteJournalSequenceCannotAuthorizeRecovery()
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point == "stage-promoted")
                throw new SourceExactSimulatedCrashException(point);
        });
        Assert.Throws<SourceExactSimulatedCrashException>(() =>
            transaction.Install(stage, Artifact()));
        var middle = Assert.Single(Directory.EnumerateFiles(
            _root, ".vt2-source-exact-journal-*-1.txn"));
        File.Delete(middle);

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Recover(_target));

        Assert.Equal(SourceExactTransactionFailure.JournalInvalid, exception.Failure);
        Assert.True(File.Exists(Path.Combine(_target, "modx.mod")));
        Assert.NotEmpty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void RollbackFailureRetainsEveryRecoveryArtifact()
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var transaction = new SourceExactDirectoryTransaction(checkpoint: point =>
        {
            if (point != "after-prior-rename") return;
            var backup = Assert.Single(Directory.EnumerateDirectories(
                _root, ".vt2-source-exact-backup-*"));
            File.WriteAllText(Path.Combine(backup, "foreign.txt"), "block rollback");
            throw new IOException("fixture transaction failure");
        });

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            transaction.Install(stage, Artifact()));

        Assert.Equal(SourceExactTransactionFailure.RollbackFailed, exception.Failure);
        Assert.False(Directory.Exists(_target));
        Assert.NotEmpty(Directory.EnumerateDirectories(_root, ".vt2-source-exact-backup-*"));
        Assert.NotEmpty(Directory.EnumerateDirectories(_root, ".vt2-source-exact-stage-*"));
        Assert.NotEmpty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void ChangedStageRefusesBeforeTargetMutation()
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var before = Snapshot(_target);
        File.WriteAllText(Path.Combine(stage.StageDirectory, "foreign.txt"), "foreign");

        var exception = Assert.Throws<SourceExactTransactionException>(() =>
            new SourceExactDirectoryTransaction().Install(stage, Artifact()));

        Assert.Equal(SourceExactTransactionFailure.StageChanged, exception.Failure);
        Assert.Equal(before, Snapshot(_target));
        Assert.Empty(Directory.EnumerateFiles(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void StrictInstalledStateRejectsUnknownDuplicateAndWrongAuthority()
    {
        using var stage = CreateStage();
        var document = SourceExactInstalledState.Create(Artifact(), stage.Outputs);
        var bytes = SourceExactInstalledState.Serialize(document);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Parse(
            Encoding.UTF8.GetBytes("{\"unknown\":1," + text[1..])));
        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Parse(
            Encoding.UTF8.GetBytes(text.Replace(
                "\"schema_version\":1",
                "\"schema_version\":1,\"schema_version\":1"))));
        Assert.Throws<InvalidDataException>(() => SourceExactInstalledState.Parse(
            Encoding.UTF8.GetBytes(text.Replace(
                "\"authority\":\"source_exact\"",
                "\"authority\":\"legacy_manifest_bound\""))));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT9.mod")]
    [InlineData("weapon.mod:payload")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Win32DeviceAndAdsLeavesAreRejected(string leaf)
    {
        Assert.False(SourceExactTransactionFileSystem.SafeLeaf(leaf));
    }

    [Fact]
    public void PreCancelledInstallLeavesTargetAndStageByteExact()
    {
        WritePriorTarget();
        using var stage = CreateStage();
        var beforeTarget = Snapshot(_target);
        var beforeStage = Snapshot(stage.StageDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new SourceExactDirectoryTransaction().Install(stage, Artifact(), cancellation.Token));

        Assert.Equal(beforeTarget, Snapshot(_target));
        Assert.Equal(beforeStage, Snapshot(stage.StageDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".vt2-source-exact-backup-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".vt2-source-exact-journal-*"));
    }

    [Fact]
    public void StageOutsidePinnedParentIsRefusedBeforeTargetMutation()
    {
        WritePriorTarget();
        var beforeTarget = Snapshot(_target);
        var foreignRoot = Path.Combine(Path.GetTempPath(),
            "vt2-source-exact-foreign-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreignRoot);
        var path = Path.Combine(foreignRoot, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var original = CreateStage();
            foreach (var file in Directory.EnumerateFiles(original.StageDirectory))
                File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
            var artifact = Artifact();
            using var foreign = new SourceExactZipStage(
                path, _target, artifact, original.ArchiveSha256, original.Outputs);

            var exception = Assert.Throws<SourceExactTransactionException>(() =>
                new SourceExactDirectoryTransaction().Install(foreign, Artifact()));

            Assert.Equal(SourceExactTransactionFailure.InvalidTarget, exception.Failure);
            Assert.Equal(beforeTarget, Snapshot(_target));
        }
        finally
        {
            if (Directory.Exists(foreignRoot)) Directory.Delete(foreignRoot, recursive: true);
        }
    }

    [Fact]
    public void ReparseTargetIsRefusedAndItsDestinationIsUnchanged()
    {
        var destination = Path.Combine(_root, "foreign-destination");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "preserve.txt"), "preserve me");
        var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{_target}\" \"{destination}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        junction.WaitForExit();
        Assert.Equal(0, junction.ExitCode);
        using var stage = CreateStage();
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                new SourceExactDirectoryTransaction().Install(stage, Artifact()));

            Assert.Equal("preserve me", File.ReadAllText(Path.Combine(destination, "preserve.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "modx.mod")));
        }
        finally
        {
            if (Directory.Exists(_target)) Directory.Delete(_target);
        }
    }

    private SourceExactZipStage CreateStage()
    {
        var path = Path.Combine(
            _root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["0123456789abcdef.mod_bundle"] = new byte[] { 1, 3, 3, 7, 9, 11, 13, 17 },
            ["fedcba9876543210.mod_bundle"] = new byte[] { 2, 4, 6, 8, 10, 12, 14, 16 },
            ["modx.mod"] = Encoding.ASCII.GetBytes("fixture descriptor\n"),
            [SourceExactZipStager.VersionMarkerFilename] = Encoding.ASCII.GetBytes("1.2.3-dev")
        };
        foreach (var row in files) File.WriteAllBytes(Path.Combine(path, row.Key), row.Value);
        var outputs = files
            .Where(row => row.Key != SourceExactZipStager.VersionMarkerFilename)
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => new SourceExactStagedOutput(row.Key, row.Value.LongLength, Sha256(row.Value)))
            .ToArray();
        var artifact = Artifact();
        return new SourceExactZipStage(
            path, _target, artifact, artifact.AssetSha256, outputs);
    }

    private void WritePriorTarget()
    {
        Directory.CreateDirectory(_target);
        File.WriteAllBytes(Path.Combine(_target, "old.mod_bundle"), new byte[] { 9, 8, 7 });
        File.WriteAllText(Path.Combine(_target, "vt2updater_version.txt"), "old");
    }

    private SourceExactRecoveryArtifact Artifact()
    {
        var json = JsonNode.Parse(RecoveryFixture("valid-tracked.json"))!.AsObject();
        var sourceCommit = json["source"]!["commit"]!.GetValue<string>();
        var proof = RecoveryRecordContract.ParseAndValidate(
            json.ToJsonString(),
            Binding(json["asset"]!["sha256"]!.GetValue<string>(), sourceCommit));
        return new SourceExactRecoveryArtifact(
            RecoveryRecordContract.Repository,
            proof.Record.Release.Tag,
            100,
            "mods-container-2026-08-28",
            DateTimeOffset.Parse("2026-08-28T12:00:00Z"),
            200,
            proof.Record.Asset.Filename,
            proof.Record.Asset.Length,
            proof.Record.Asset.Sha256,
            "https://release-assets.githubusercontent.com/fixture",
            proof,
            1,
            1);
    }

    private static RecoveryManifestBinding Binding(string assetSha256, string sourceCommit) => new(
        "mx", "1234567890", "1.2.3-dev", "mx.zip", assetSha256, sourceCommit,
        "clean", "VMBLauncher", "9.8.7+fixture", "tracked",
        "0123456789abcdef.mod_bundle", "modx.mod",
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

    private void AssertNoArtifacts()
    {
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".vt2-source-exact-stage-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".vt2-source-exact-backup-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".vt2-source-exact-journal-*"));
    }

    private static string Snapshot(string path) => !Directory.Exists(path)
        ? "absent"
        : string.Join("\n", Directory.EnumerateFiles(path)
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Select(file => Path.GetFileName(file) + ":" + Sha256(File.ReadAllBytes(file))));

    private static string RecoveryFixture(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "RecoveryRecords", name));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
