using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VT2ModUpdater.Services;

/// <summary>
/// Disabled Phase 4 primitive. It consumes one verified Phase 3 stage and
/// replaces one sibling directory through a process-death-recoverable NTFS
/// transaction. There is deliberately no UI, updater, or deployment call site.
/// </summary>
internal sealed class SourceExactDirectoryTransaction
{
    private const string JournalMarker = "VT2_SOURCE_EXACT_JOURNAL_V2";
    private const string JournalPrefix = ".vt2-source-exact-journal-";
    private const string BackupPrefix = ".vt2-source-exact-backup-";
    private const string LockPrefix = ".vt2-source-exact-lock-";
    private const int MaximumJournalBytes = 16 * 1024 * 1024;
    private const int MaximumWitnesses = 4;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _lockTimeout;
    private readonly Action<string>? _checkpoint;

    internal SourceExactDirectoryTransaction(
        TimeSpan? lockTimeout = null,
        Action<string>? checkpoint = null)
    {
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        if (_lockTimeout <= TimeSpan.Zero || _lockTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        _checkpoint = checkpoint;
    }

    internal SourceExactInstallResult Install(
        SourceExactZipStage stage,
        VT2ModUpdater.Models.SourceExactRecoveryArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(artifact);
        var target = SourceExactTransactionFileSystem.Normalize(stage.IntendedTargetPath);
        var parentPath = Directory.GetParent(target)?.FullName ??
            throw Failure(SourceExactTransactionFailure.InvalidTarget, "target parent is missing");
        var targetLeaf = Path.GetFileName(target);
        if (!SourceExactTransactionFileSystem.SafeTargetLeaf(targetLeaf))
            throw Failure(SourceExactTransactionFailure.InvalidTarget,
                "target leaf is unsafe for a source-exact transaction");
        SourceExactTransactionFileSystem.RequireNtfs(parentPath);

        using var crossSessionLock = AcquireLock(target);
        Checkpoint("lock-acquired");
        RecoverLocked(target);
        cancellationToken.ThrowIfCancellationRequested();
        using var transfer = AcquireStageTransfer(stage, artifact);

        var journalOwnsStage = false;
        var cleanupSnapshot = transfer.VerifiedSnapshot;
        SourceExactTransactionFileSystem.ExactDirectoryGuard? stageGuard = null;
        SourceExactTransactionFileSystem.ExactDirectoryGuard? priorGuard = null;
        try
        {
            using var parent = SourceExactTransactionFileSystem.OpenParentDirectory(parentPath);
            if (!string.Equals(Path.GetDirectoryName(transfer.StageDirectory), parent.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
                throw Failure(SourceExactTransactionFailure.InvalidTarget,
                    "stage and target must be siblings under the pinned parent");
            if (transfer.VerifiedSnapshot.Identity.VolumeSerialNumber !=
                parent.Identity.VolumeSerialNumber)
                throw Failure(SourceExactTransactionFailure.InvalidTarget,
                    "stage and target are not on one volume");

            try
            {
                transfer.Lease.RequireCurrentPath();
                if (transfer.Lease.Identity != transfer.VerifiedSnapshot.Identity)
                    throw new InvalidDataException(
                        "source-exact Phase 3 lease identity differs from its snapshot");
                using var initialGuard =
                    SourceExactTransactionFileSystem.GuardDirectory(transfer.StageDirectory);
                initialGuard.RequireExact(transfer.VerifiedSnapshot);
                RequireTransferredStage(transfer, initialGuard.Snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw Failure(SourceExactTransactionFailure.StageChanged,
                    "source-exact stage identity or bytes changed before transfer", ex);
            }

            var installedBytes = SourceExactInstalledState.Serialize(transfer.InstalledState);
            try
            {
                using (SourceExactTransactionFileSystem.CreateDurableNewFile(
                           transfer.Lease,
                           SourceExactInstalledState.Filename,
                           installedBytes,
                           "sidecar-parent-pinned",
                           Checkpoint)) { }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw Failure(SourceExactTransactionFailure.StageChanged,
                    "source-exact installed-state sidecar could not be created in the leased stage",
                    ex);
            }
            Checkpoint("before-stage-guard");

            ExactDirectorySnapshot newSnapshot;
            try
            {
                stageGuard = SourceExactTransactionFileSystem.GuardDirectory(
                    transfer.StageDirectory);
                newSnapshot = stageGuard.Snapshot;
                cleanupSnapshot = newSnapshot;
                RequireSnapshotExtension(
                    transfer.VerifiedSnapshot, newSnapshot, installedBytes);
                SourceExactInstalledState.RequireSnapshotBinding(
                    transfer.InstalledState, newSnapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                stageGuard?.Dispose();
                stageGuard = null;
                throw Failure(SourceExactTransactionFailure.StageChanged,
                    "source-exact stage changed while adding installed-state authority", ex);
            }
            Checkpoint("stage-guarded");

            ExactDirectorySnapshot? priorSnapshot = null;
            var operation = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
            var backup = Path.Combine(parentPath, BackupPrefix + operation);
            var journalBase = JournalBase(parentPath, targetLeaf, operation);
            var witnesses = new List<JournalWitness>();
            var record = new TransactionJournal(
                operation,
                TransactionState.Prepared,
                target,
                transfer.StageDirectory,
                backup,
                newSnapshot,
                null);

            RejectJournalOrBackupCollision(parentPath, targetLeaf, backup);
            if (Directory.Exists(target))
            {
                priorGuard = SourceExactTransactionFileSystem.GuardDirectory(target);
                if (priorGuard.Snapshot.Identity.VolumeSerialNumber != parent.Identity.VolumeSerialNumber)
                    throw Failure(SourceExactTransactionFailure.InvalidTarget,
                        "existing target is not on the transaction volume");
                priorSnapshot = priorGuard.Snapshot;
                record = record with { Prior = priorSnapshot };
            }
            else if (File.Exists(target))
            {
                throw Failure(SourceExactTransactionFailure.InvalidTarget,
                    "source-exact target is occupied by a file");
            }

            witnesses.Add(WriteWitness(journalBase, parentPath, record));
            journalOwnsStage = true;
            // Once Prepared is atomically visible, the journal is the sole
            // cleanup authority. Release the transferred Phase 3 lease before
            // any checkpoint can unwind through in-process rollback.
            transfer.Dispose();
            Checkpoint("prepared");
            cancellationToken.ThrowIfCancellationRequested();

            if (priorGuard is not null)
            {
                Checkpoint("before-prior-rename");
                priorGuard.RenameTo(parent, backup);
                Checkpoint("after-prior-rename");
            }
            record = record with { State = TransactionState.PriorMoved };
            witnesses.Add(WriteWitness(journalBase, parentPath, record));
            Checkpoint("prior-moved");
            cancellationToken.ThrowIfCancellationRequested();

            Checkpoint("before-stage-rename");
            stageGuard.RenameTo(parent, target);
            Checkpoint("after-stage-rename");
            record = record with { State = TransactionState.StagePromoted };
            witnesses.Add(WriteWitness(journalBase, parentPath, record));
            Checkpoint("stage-promoted");

            stageGuard.RequireExact(newSnapshot);
            VerifyInstalledStateAtTarget(target, installedBytes, newSnapshot);
            record = record with { State = TransactionState.Committed };
            witnesses.Add(WriteWitness(journalBase, parentPath, record));
            Checkpoint("committed");

            // RenameTo seals the accepted target against external writes,
            // deletes, and renames. Retain that physical guard until every
            // rollback and journal witness is durably retired.
            stageGuard.RequireExact(newSnapshot);
            Checkpoint("commit-target-pinned");
            stageGuard.RequireExact(newSnapshot);
            if (priorGuard is not null)
            {
                priorGuard.RequireExact(priorSnapshot!);
                priorGuard.DeleteAll("backup", Checkpoint);
                priorGuard.Dispose();
                priorGuard = null;
            }
            stageGuard.RequireExact(newSnapshot);
            DeleteWitnesses(witnesses);
            stageGuard.RequireExact(newSnapshot);
            stageGuard.Dispose();
            stageGuard = null;
            return new SourceExactInstallResult(target, transfer.InstalledState, newSnapshot);
        }
        catch (SourceExactSimulatedCrashException)
        {
            // Lightweight same-process seam retained for focused unit tests. The
            // release gate also kills a child process at these checkpoints.
            throw;
        }
        catch (Exception original)
        {
            stageGuard?.Dispose();
            stageGuard = null;
            priorGuard?.Dispose();
            priorGuard = null;
            try
            {
                if (journalOwnsStage)
                {
                    RecoverLocked(target);
                }
                else if (original is SourceExactTransactionException
                {
                    Failure: SourceExactTransactionFailure.StageChanged
                })
                {
                    // A changed pre-journal stage is untrusted evidence. Preserve
                    // it instead of converting the exact refusal into a cleanup
                    // failure or deleting by pathname.
                }
                else
                {
                    SourceExactTransactionFileSystem.DeleteOwnedExactDirectory(
                        transfer.Lease,
                        cleanupSnapshot,
                        "unpublished-stage",
                        Checkpoint);
                }
            }
            catch (Exception rollback)
            {
                throw Failure(
                    SourceExactTransactionFailure.RollbackFailed,
                    "source-exact transaction failed and rollback could not restore the prior state",
                    new AggregateException(original, rollback));
            }
            if (original is OperationCanceledException) throw;
            if (original is SourceExactTransactionException) throw;
            throw Failure(SourceExactTransactionFailure.FileSystem,
                "source-exact transaction failed before commit", original);
        }
        finally
        {
            stageGuard?.Dispose();
            priorGuard?.Dispose();
        }
    }

    internal SourceExactRecoveryResult Recover(string intendedTargetPath)
    {
        var target = SourceExactTransactionFileSystem.Normalize(intendedTargetPath);
        var parentPath = Directory.GetParent(target)?.FullName ??
            throw Failure(SourceExactTransactionFailure.InvalidTarget, "target parent is missing");
        if (!SourceExactTransactionFileSystem.SafeTargetLeaf(Path.GetFileName(target)))
            throw Failure(SourceExactTransactionFailure.InvalidTarget,
                "target leaf is unsafe for a source-exact transaction");
        SourceExactTransactionFileSystem.RequireNtfs(parentPath);
        using var crossSessionLock = AcquireLock(target);
        return RecoverLocked(target);
    }

    private static SourceExactStageTransfer AcquireStageTransfer(
        SourceExactZipStage stage,
        VT2ModUpdater.Models.SourceExactRecoveryArtifact artifact)
    {
        try
        {
            return stage.TransferOwnership(artifact);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw Failure(SourceExactTransactionFailure.StageChanged,
                "source-exact Phase 3 lease or artifact binding changed before transfer", ex);
        }
    }

    private SourceExactRecoveryResult RecoverLocked(string target)
    {
        var parentPath = Directory.GetParent(target)?.FullName ??
            throw Failure(SourceExactTransactionFailure.InvalidTarget, "target parent is missing");
        SourceExactTransactionFileSystem.RequireNtfs(parentPath);
        var scan = ReadJournalGroups(parentPath, Path.GetFileName(target));
        foreach (var orphan in scan.OrphanPartials)
            SourceExactTransactionFileSystem.DeleteExactFile(orphan);
        if (scan.Groups.Count == 0) return SourceExactRecoveryResult.NothingToRecover;
        if (scan.Groups.Count != 1)
            throw Failure(SourceExactTransactionFailure.JournalInvalid,
                "multiple source-exact journal operations target the same directory");
        var group = scan.Groups.Single();
        var record = group.Records.OrderBy(row => row.Record.State).Last().Record;
        ValidateJournalPaths(record, target, parentPath);
        using var parent = SourceExactTransactionFileSystem.OpenParentDirectory(parentPath);

        var targetNew = SourceExactTransactionFileSystem.InspectSnapshot(target, record.New);
        var targetPrior = record.Prior is null
            ? ExactSnapshotMatch.Absent
            : SourceExactTransactionFileSystem.InspectSnapshot(target, record.Prior);
        var backupPrior = record.Prior is null
            ? ExactSnapshotMatch.Absent
            : SourceExactTransactionFileSystem.InspectSnapshot(record.Backup, record.Prior);
        var stageNew = SourceExactTransactionFileSystem.InspectSnapshot(record.Stage, record.New);

        if (targetNew == ExactSnapshotMatch.Exact)
        {
            using var targetGuard = PinRecoveryDirectory(
                target, record.New, "promoted target changed before recovery cleanup");
            VerifyInstalledStateAtTargetFromSnapshot(target, record.New);
            Checkpoint("recovery-target-new-pinned");
            RequireRecoveryExact(
                targetGuard, record.New, "promoted target changed before backup cleanup");
            if (backupPrior is ExactSnapshotMatch.Exact or ExactSnapshotMatch.ExactSubset)
                SourceExactTransactionFileSystem.DeleteExactDirectoryRestartable(
                    record.Backup, record.Prior!, allowPartial: true, "backup", Checkpoint);
            else if (backupPrior != ExactSnapshotMatch.Absent)
                throw Failure(SourceExactTransactionFailure.ForeignMutation,
                    "recorded backup was replaced after source-exact promotion");
            if (stageNew != ExactSnapshotMatch.Absent ||
                SourceExactTransactionFileSystem.InspectSnapshot(
                    record.Stage, record.New) != ExactSnapshotMatch.Absent)
                throw Failure(SourceExactTransactionFailure.ForeignMutation,
                    "recorded stage path was replaced after source-exact promotion");
            RequireRecoveryExact(
                targetGuard, record.New, "promoted target changed during backup cleanup");
            Checkpoint("recovery-target-new-final");
            DeleteWitnesses(group.Records);
            targetGuard.RequireExact(record.New);
            return SourceExactRecoveryResult.CommittedRecovered;
        }

        if (targetPrior == ExactSnapshotMatch.Exact)
        {
            var prior = record.Prior ?? throw Failure(
                SourceExactTransactionFailure.JournalInvalid,
                "prior-target recovery has no recorded prior snapshot");
            using var targetGuard = PinRecoveryDirectory(
                target, prior, "prior target changed before rollback cleanup");
            Checkpoint("recovery-target-prior-pinned");
            RequireRecoveryExact(
                targetGuard, prior, "prior target changed before staged-output cleanup");
            if (backupPrior != ExactSnapshotMatch.Absent)
                throw Failure(SourceExactTransactionFailure.ForeignMutation,
                    "prior source-exact identity exists at both target and backup");
            if (stageNew is ExactSnapshotMatch.Exact or ExactSnapshotMatch.ExactSubset)
                SourceExactTransactionFileSystem.DeleteExactDirectoryRestartable(
                    record.Stage, record.New, allowPartial: true, "stage", Checkpoint);
            else if (stageNew != ExactSnapshotMatch.Absent)
                throw Failure(SourceExactTransactionFailure.ForeignMutation,
                    "recorded stage was replaced before rollback");
            if (SourceExactTransactionFileSystem.InspectSnapshot(
                    record.Stage, record.New) != ExactSnapshotMatch.Absent)
                throw Failure(SourceExactTransactionFailure.ForeignMutation,
                    "recorded stage reappeared before rollback evidence cleanup");
            RequireRecoveryExact(
                targetGuard, prior, "prior target changed during staged-output cleanup");
            Checkpoint("recovery-target-prior-final");
            DeleteWitnesses(group.Records);
            targetGuard.RequireExact(prior);
            return SourceExactRecoveryResult.RolledBack;
        }

        if (targetNew == ExactSnapshotMatch.Absent &&
            targetPrior == ExactSnapshotMatch.Absent)
        {
            if (record.Prior is not null && backupPrior == ExactSnapshotMatch.Exact)
            {
                using var backup = SourceExactTransactionFileSystem.GuardDirectory(record.Backup);
                backup.RequireExact(record.Prior);
                backup.RenameTo(parent, target);
                backup.RequireExact(record.Prior);
                Checkpoint("recovery-restored-prior-pinned");
                RequireRecoveryExact(
                    backup, record.Prior, "restored target changed before staged-output cleanup");
                if (stageNew is ExactSnapshotMatch.Exact or ExactSnapshotMatch.ExactSubset)
                    SourceExactTransactionFileSystem.DeleteExactDirectoryRestartable(
                        record.Stage, record.New, allowPartial: true, "stage", Checkpoint);
                else if (stageNew != ExactSnapshotMatch.Absent)
                    throw Failure(SourceExactTransactionFailure.ForeignMutation,
                        "recorded stage was replaced during rollback");
                if (SourceExactTransactionFileSystem.InspectSnapshot(
                        record.Stage, record.New) != ExactSnapshotMatch.Absent)
                    throw Failure(SourceExactTransactionFailure.ForeignMutation,
                        "recorded stage reappeared before restored-target evidence cleanup");
                RequireRecoveryExact(
                    backup, record.Prior, "restored target changed during staged-output cleanup");
                Checkpoint("recovery-restored-prior-final");
                DeleteWitnesses(group.Records);
                backup.RequireExact(record.Prior);
                return SourceExactRecoveryResult.RolledBack;
            }
            if (record.Prior is null)
            {
                if (stageNew is ExactSnapshotMatch.Exact or ExactSnapshotMatch.ExactSubset)
                    SourceExactTransactionFileSystem.DeleteExactDirectoryRestartable(
                        record.Stage, record.New, allowPartial: true, "stage", Checkpoint);
                else if (stageNew != ExactSnapshotMatch.Absent)
                    throw Failure(SourceExactTransactionFailure.ForeignMutation,
                        "recorded stage was replaced during absent-target rollback");
                if (SourceExactTransactionFileSystem.InspectSnapshot(
                        target, record.New) != ExactSnapshotMatch.Absent ||
                    SourceExactTransactionFileSystem.InspectSnapshot(
                        record.Stage, record.New) != ExactSnapshotMatch.Absent)
                    throw Failure(SourceExactTransactionFailure.ForeignMutation,
                        "target or stage appeared before absent-target evidence cleanup");
                Checkpoint("recovery-absent-target-final");
                DeleteWitnesses(group.Records);
                return SourceExactRecoveryResult.RolledBack;
            }
        }

        throw Failure(SourceExactTransactionFailure.ForeignMutation,
            "source-exact target/backup/stage no longer match the process-death journal");
    }

    private static SourceExactTransactionFileSystem.ExactDirectoryGuard PinRecoveryDirectory(
        string path,
        ExactDirectorySnapshot expected,
        string message)
    {
        SourceExactTransactionFileSystem.ExactDirectoryGuard? guard = null;
        try
        {
            guard = SourceExactTransactionFileSystem.PinDirectory(path);
            guard.RequireExact(expected);
            return guard;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            guard?.Dispose();
            throw Failure(SourceExactTransactionFailure.ForeignMutation, message, ex);
        }
    }

    private static void RequireRecoveryExact(
        SourceExactTransactionFileSystem.ExactDirectoryGuard guard,
        ExactDirectorySnapshot expected,
        string message)
    {
        try { guard.RequireExact(expected); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw Failure(SourceExactTransactionFailure.ForeignMutation, message, ex);
        }
    }

    private static void RequireTransferredStage(
        SourceExactStageTransfer transfer,
        ExactDirectorySnapshot actual)
    {
        if (!FixedTimeHexEquals(transfer.ArchiveSha256, transfer.InstalledState.AssetSha256))
            throw Failure(SourceExactTransactionFailure.StageChanged,
                "verified stage archive hash differs from its recovery coordinate");
        var markerBytes = Encoding.ASCII.GetBytes(transfer.Version);
        var markerSha = SourceExactInstalledState.Sha256(markerBytes);
        var marker = actual.Files.SingleOrDefault(file =>
            file.Name == SourceExactZipStager.VersionMarkerFilename);
        if (marker is null || marker.Length != markerBytes.Length || marker.Sha256 != markerSha)
            throw Failure(SourceExactTransactionFailure.StageChanged,
                "source-exact stage version marker changed after ZIP verification");
        var rows = actual.Files
            .Where(file => file.Name != SourceExactZipStager.VersionMarkerFilename)
            .Select(file => new SourceExactStagedOutput(file.Name, file.Length, file.Sha256))
            .ToArray();
        if (!rows.SequenceEqual(transfer.Outputs))
            throw Failure(SourceExactTransactionFailure.StageChanged,
                "source-exact stage outputs changed after ZIP verification");
    }

    private static void RequireSnapshotExtension(
        ExactDirectorySnapshot before,
        ExactDirectorySnapshot after,
        ReadOnlySpan<byte> sidecarBytes)
    {
        if (before.Identity != after.Identity || after.Files.Count != before.Files.Count + 1 ||
            !before.Files.All(row => after.Files.Contains(row)))
            throw Failure(SourceExactTransactionFailure.StageChanged,
                "source-exact stage changed while adding installed-state proof");
        var sidecar = after.Files.SingleOrDefault(file =>
            file.Name == SourceExactInstalledState.Filename);
        if (sidecar is null || sidecar.Length != sidecarBytes.Length ||
            sidecar.Sha256 != SourceExactInstalledState.Sha256(sidecarBytes))
            throw Failure(SourceExactTransactionFailure.StageChanged,
                "source-exact installed-state sidecar was not durably staged");
    }

    private static void VerifyInstalledStateAtTarget(
        string target,
        ReadOnlySpan<byte> expected,
        ExactDirectorySnapshot snapshot)
    {
        try
        {
            var path = Path.Combine(target, SourceExactInstalledState.Filename);
            var actual = SourceExactTransactionFileSystem.ReadBoundedExactFile(
                path, SourceExactInstalledState.MaximumBytes).Bytes;
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(actual), SHA256.HashData(expected)))
                throw new InvalidDataException(
                    "source-exact installed-state sidecar differs after promotion");
            var document = SourceExactInstalledState.Parse(actual);
            SourceExactInstalledState.RequireSnapshotBinding(document, snapshot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw Failure(SourceExactTransactionFailure.InstalledStateInvalid,
                "source-exact installed-state authority is invalid after promotion", ex);
        }
    }

    private static void VerifyInstalledStateAtTargetFromSnapshot(
        string target,
        ExactDirectorySnapshot expected)
    {
        try
        {
            var row = expected.Files.SingleOrDefault(file =>
                file.Name == SourceExactInstalledState.Filename) ??
                throw new InvalidDataException(
                    "source-exact committed snapshot has no installed-state sidecar");
            var exact = SourceExactTransactionFileSystem.ReadBoundedExactFile(
                Path.Combine(target, row.Name), SourceExactInstalledState.MaximumBytes);
            if (exact.Bytes.LongLength != row.Length ||
                SourceExactInstalledState.Sha256(exact.Bytes) != row.Sha256)
                throw new InvalidDataException(
                    "source-exact installed-state bytes differ from journal proof");
            var document = SourceExactInstalledState.Parse(exact.Bytes);
            SourceExactInstalledState.RequireSnapshotBinding(document, expected);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw Failure(SourceExactTransactionFailure.InstalledStateInvalid,
                "source-exact recovered installed-state authority is invalid", ex);
        }
    }

    private static string JournalBase(string parent, string targetLeaf, string operation) =>
        Path.Combine(parent, JournalPrefix + TargetKey(targetLeaf) + "-" + operation);

    private static string TargetKey(string targetLeaf) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            targetLeaf.ToUpperInvariant()))).ToLowerInvariant();

    private static void RejectJournalOrBackupCollision(
        string parent,
        string targetLeaf,
        string backup)
    {
        if (Directory.Exists(backup) || File.Exists(backup))
            throw Failure(SourceExactTransactionFailure.JournalCollision,
                "source-exact backup path is already occupied");
        var prefix = JournalPrefix + TargetKey(targetLeaf) + "-";
        if (Directory.EnumerateFileSystemEntries(parent, prefix + "*").Any())
            throw Failure(SourceExactTransactionFailure.JournalCollision,
                "source-exact transaction journal already exists");
    }

    private JournalWitness WriteWitness(
        string journalBase,
        string parentPath,
        TransactionJournal record)
    {
        var bytes = JournalCodec.Serialize(record);
        var state = ((int)record.State).ToString(CultureInfo.InvariantCulture);
        var path = journalBase + "-" + state + ".txn";
        var witness = SourceExactTransactionFileSystem.CreateDurableAtomicFile(
            parentPath,
            path,
            bytes,
            $"witness-{state}-temp",
            $"witness-{state}-published",
            Checkpoint);
        return new JournalWitness(witness, record, IsPartial: false);
    }

    private static JournalScan ReadJournalGroups(string parent, string targetLeaf)
    {
        var prefix = JournalPrefix + TargetKey(targetLeaf) + "-";
        var official = Directory.EnumerateFiles(parent, prefix + "*.txn")
            .Take(MaximumWitnesses + 1).ToArray();
        if (official.Length > MaximumWitnesses)
            throw Failure(SourceExactTransactionFailure.JournalInvalid,
                "source-exact journal exceeds its four-witness bound");
        var partials = Directory.EnumerateFiles(parent, prefix + "*.txn.partial-*")
            .Take(2).ToArray();
        if (partials.Length > 1)
            throw Failure(SourceExactTransactionFailure.JournalInvalid,
                "source-exact journal has multiple unpublished witnesses");

        var records = new List<JournalWitness>();
        var orphans = new List<FileWitness>();
        foreach (var path in official.Concat(partials))
        {
            var partial = partials.Contains(path);
            BoundedFile exact;
            try
            {
                exact = SourceExactTransactionFileSystem.ReadBoundedExactFile(
                    path, MaximumJournalBytes, allowEmpty: partial);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal witness violates its bounded-read contract", ex);
            }
            var fileWitness = new FileWitness(exact.Path, exact.Bytes, exact.Identity);
            TransactionJournal record;
            try { record = JournalCodec.Parse(exact.Bytes); }
            catch (SourceExactTransactionException) when (partial)
            {
                orphans.Add(fileWitness);
                continue;
            }
            var expectedBase = Path.GetFileName(
                JournalBase(parent, targetLeaf, record.Operation)) + "-" +
                (int)record.State + ".txn";
            var actualName = Path.GetFileName(path);
            var nameMatches = partial
                ? actualName.StartsWith(expectedBase + ".partial-", StringComparison.Ordinal) &&
                  actualName.Length == expectedBase.Length + ".partial-".Length + 16 &&
                  actualName[(expectedBase.Length + ".partial-".Length)..]
                      .All(IsLowerHex)
                : actualName == expectedBase;
            if (!nameMatches)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal filename does not match its payload");
            records.Add(new JournalWitness(fileWitness, record, partial));
        }

        if (records.Count > MaximumWitnesses)
            throw Failure(SourceExactTransactionFailure.JournalInvalid,
                "source-exact journal exceeds its total witness bound");
        var groups = records
            .GroupBy(record => record.Record.Operation, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(row => row.Record.State).ToArray();
                if (ordered.Select(row => row.Record.State).Distinct().Count() != ordered.Length)
                    throw Failure(SourceExactTransactionFailure.JournalInvalid,
                        "source-exact journal contains duplicate state witnesses");
                for (var index = 0; index < ordered.Length; index++)
                {
                    if ((int)ordered[index].Record.State != index)
                        throw Failure(SourceExactTransactionFailure.JournalInvalid,
                            "source-exact journal state sequence is incomplete");
                }
                var canonical = ordered[0].Record with { State = TransactionState.Prepared };
                foreach (var row in ordered)
                {
                    if (!JournalEquivalent(
                            row.Record with { State = TransactionState.Prepared }, canonical))
                        throw Failure(SourceExactTransactionFailure.JournalInvalid,
                            "source-exact journal witnesses disagree");
                }
                return new JournalGroup(group.Key, ordered);
            })
            .ToArray();
        return new JournalScan(groups, orphans);
    }

    private static void ValidateJournalPaths(
        TransactionJournal record,
        string target,
        string parent)
    {
        if (!SourceExactTransactionFileSystem.SamePath(record.Target, target) ||
            SourceExactTransactionFileSystem.SamePath(record.Stage, record.Target) ||
            SourceExactTransactionFileSystem.SamePath(record.Backup, record.Target) ||
            SourceExactTransactionFileSystem.SamePath(record.Stage, record.Backup) ||
            !string.Equals(Path.GetDirectoryName(record.Stage), parent,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(record.Backup), parent,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(record.Stage).StartsWith(
                ".vt2-source-exact-stage-", StringComparison.Ordinal) ||
            !SourceExactTransactionFileSystem.SafeLeaf(Path.GetFileName(record.Stage)) ||
            Path.GetFileName(record.Backup) != BackupPrefix + record.Operation)
            throw Failure(SourceExactTransactionFailure.JournalInvalid,
                "source-exact journal paths escape their target parent or namespace");
    }

    private void DeleteWitnesses(IEnumerable<JournalWitness> witnesses)
    {
        foreach (var witness in witnesses.OrderByDescending(row => row.Record.State))
        {
            SourceExactTransactionFileSystem.DeleteExactFile(witness.File);
            Checkpoint("cleanup-witness-" + (int)witness.Record.State);
        }
    }

    private SourceExactTransactionFileSystem.ExclusiveFileLock AcquireLock(string target)
    {
        var parent = Directory.GetParent(target)?.FullName ??
            throw Failure(SourceExactTransactionFailure.InvalidTarget, "target parent is missing");
        try
        {
            // Validate the physical parent before OpenAlways is allowed to create
            // the persistent lock leaf. This keeps an ancestor alias/reparse point
            // from redirecting even the lock-file write outside the intended parent.
            using var parentLease = SourceExactTransactionFileSystem.OpenParentDirectory(parent);
            parentLease.RequireCurrentPath();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                target.ToUpperInvariant()))).ToLowerInvariant();
            var path = Path.Combine(parent, LockPrefix + digest + ".lck");
            var acquired = SourceExactTransactionFileSystem.AcquireCrossSessionLock(
                path, _lockTimeout);
            try
            {
                parentLease.RequireCurrentPath();
                return acquired;
            }
            catch
            {
                acquired.Dispose();
                throw;
            }
        }
        catch (SourceExactLockUnavailableException ex)
        {
            throw Failure(SourceExactTransactionFailure.Locked,
                "another source-exact transaction owns this target", ex);
        }
        catch (InvalidDataException ex)
        {
            throw Failure(SourceExactTransactionFailure.InvalidTarget,
                "source-exact transaction parent or lock authority is aliased", ex);
        }
        catch (IOException ex)
        {
            throw Failure(SourceExactTransactionFailure.FileSystem,
                "source-exact cross-session lock could not be acquired", ex);
        }
    }

    private void Checkpoint(string name) => _checkpoint?.Invoke(name);

    private static bool JournalEquivalent(TransactionJournal left, TransactionJournal right) =>
        left.Operation == right.Operation &&
        SourceExactTransactionFileSystem.SamePath(left.Target, right.Target) &&
        SourceExactTransactionFileSystem.SamePath(left.Stage, right.Stage) &&
        SourceExactTransactionFileSystem.SamePath(left.Backup, right.Backup) &&
        left.New.EqualsByValue(right.New) &&
        ((left.Prior is null && right.Prior is null) ||
         (left.Prior is not null && right.Prior is not null &&
          left.Prior.EqualsByValue(right.Prior)));

    private static bool FixedTimeHexEquals(string left, string right) =>
        left.Length == 64 && right.Length == 64 &&
        left.All(IsLowerHex) && right.All(IsLowerHex) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static SourceExactTransactionException Failure(
        SourceExactTransactionFailure failure,
        string message,
        Exception? inner = null) => new(failure, message, inner);

    private enum TransactionState
    {
        Prepared = 0,
        PriorMoved = 1,
        StagePromoted = 2,
        Committed = 3
    }

    private sealed record TransactionJournal(
        string Operation,
        TransactionState State,
        string Target,
        string Stage,
        string Backup,
        ExactDirectorySnapshot New,
        ExactDirectorySnapshot? Prior);

    private sealed record JournalWitness(
        FileWitness File,
        TransactionJournal Record,
        bool IsPartial);

    private sealed record JournalGroup(
        string Operation,
        IReadOnlyList<JournalWitness> Records);

    private sealed record JournalScan(
        IReadOnlyList<JournalGroup> Groups,
        IReadOnlyList<FileWitness> OrphanPartials);

    private static class JournalCodec
    {
        internal static byte[] Serialize(TransactionJournal journal)
        {
            var lines = new List<string>
            {
                JournalMarker,
                "operation=" + journal.Operation,
                "state=" + ((int)journal.State).ToString(CultureInfo.InvariantCulture),
                "target=" + B64(journal.Target),
                "stage=" + B64(journal.Stage),
                "backup=" + B64(journal.Backup)
            };
            AddSnapshot(lines, "new", journal.New);
            lines.Add("prior=" + (journal.Prior is null ? "0" : "1"));
            if (journal.Prior is not null) AddSnapshot(lines, "old", journal.Prior);
            var body = string.Join('\n', lines) + "\n";
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                .ToLowerInvariant();
            var bytes = Encoding.UTF8.GetBytes(body + "checksum=" + checksum + "\n");
            if (bytes.Length > MaximumJournalBytes)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal exceeds its byte bound");
            return bytes;
        }

        internal static TransactionJournal Parse(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaximumJournalBytes)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal witness has an invalid byte length");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException ex)
            {
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal is not strict UTF-8", ex);
            }
            if (!text.EndsWith('\n') || text.Contains('\r'))
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal line endings are invalid");
            var lines = text.Split('\n');
            if (lines.Length < 10 || lines[^1] != "")
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal is truncated");
            var checksumLine = lines[^2];
            if (!checksumLine.StartsWith("checksum=", StringComparison.Ordinal))
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal checksum is missing");
            var body = string.Join('\n', lines.Take(lines.Length - 2)) + "\n";
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                .ToLowerInvariant();
            if (checksumLine != "checksum=" + expected)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal checksum differs");
            var cursor = new LineCursor(lines.Take(lines.Length - 2).ToArray());
            cursor.Exact(JournalMarker);
            var operation = cursor.Value("operation");
            if (operation.Length != 32 || !operation.All(IsLowerHex))
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal operation id is invalid");
            var stateValue = ParseLong(cursor.Value("state"));
            if (stateValue < 0 || stateValue > 3)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal state is invalid");
            var target = FromB64(cursor.Value("target"));
            var stage = FromB64(cursor.Value("stage"));
            var backup = FromB64(cursor.Value("backup"));
            var next = ReadSnapshot(cursor, "new");
            var hasPrior = cursor.Value("prior");
            ExactDirectorySnapshot? prior = hasPrior switch
            {
                "0" => null,
                "1" => ReadSnapshot(cursor, "old"),
                _ => throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal prior flag is invalid")
            };
            cursor.End();
            return new TransactionJournal(
                operation,
                (TransactionState)stateValue,
                NormalizeJournalPath(target),
                NormalizeJournalPath(stage),
                NormalizeJournalPath(backup),
                next,
                prior);
        }

        private static void AddSnapshot(
            List<string> lines,
            string prefix,
            ExactDirectorySnapshot value)
        {
            lines.Add(prefix + "_dir=" + Identity(value.Identity));
            lines.Add(prefix + "_count=" +
                value.Files.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var file in value.Files)
            {
                lines.Add(prefix + "_file=" + string.Join('|',
                    B64(file.Name),
                    file.Length.ToString(CultureInfo.InvariantCulture),
                    file.Sha256,
                    Identity(file.Identity)));
            }
        }

        private static ExactDirectorySnapshot ReadSnapshot(
            LineCursor cursor,
            string prefix)
        {
            var identity = ParseIdentity(cursor.Value(prefix + "_dir"));
            var count = ParseLong(cursor.Value(prefix + "_count"));
            if (count < 0 || count > SourceExactZipStager.MaximumEntries + 1)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal snapshot count is invalid");
            var files = new List<ExactFileSnapshot>((int)count);
            for (var index = 0; index < count; index++)
            {
                var fields = cursor.Value(prefix + "_file").Split('|');
                if (fields.Length != 4)
                    throw Failure(SourceExactTransactionFailure.JournalInvalid,
                        "source-exact journal file row is invalid");
                var name = FromB64(fields[0]);
                var length = ParseLong(fields[1]);
                if (!SourceExactTransactionFileSystem.SafeLeaf(name) || length < 0 ||
                    fields[2].Length != 64 || !fields[2].All(IsLowerHex))
                    throw Failure(SourceExactTransactionFailure.JournalInvalid,
                        "source-exact journal file proof is invalid");
                files.Add(new ExactFileSnapshot(
                    name, length, fields[2], ParseIdentity(fields[3])));
            }
            if (!files.SequenceEqual(files.OrderBy(file => file.Name, StringComparer.Ordinal)) ||
                files.Select(file => file.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal snapshot ordering is invalid");
            return new ExactDirectorySnapshot(identity, files.AsReadOnly());
        }

        private static string Identity(ExactIdentity value) => string.Join(':',
            value.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture),
            value.FileIdLow.ToString("x16", CultureInfo.InvariantCulture),
            value.FileIdHigh.ToString("x16", CultureInfo.InvariantCulture));

        private static ExactIdentity ParseIdentity(string value)
        {
            var fields = value.Split(':');
            if (fields.Length != 3 || fields.Any(field => field.Length != 16) ||
                !ulong.TryParse(fields[0], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var volume) ||
                !ulong.TryParse(fields[1], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var low) ||
                !ulong.TryParse(fields[2], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out var high))
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal physical identity is invalid");
            return new ExactIdentity(volume, low, high);
        }

        private static string B64(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        private static string FromB64(string value)
        {
            try
            {
                return new UTF8Encoding(false, true)
                    .GetString(Convert.FromBase64String(value));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal string encoding is invalid", ex);
            }
        }

        private static string NormalizeJournalPath(string value)
        {
            try
            {
                return SourceExactTransactionFileSystem.Normalize(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                PathTooLongException)
            {
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal path is not canonical", ex);
            }
        }

        private static long ParseLong(string value)
        {
            if (!long.TryParse(value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var result))
                throw Failure(SourceExactTransactionFailure.JournalInvalid,
                    "source-exact journal integer is invalid");
            return result;
        }

        private sealed class LineCursor(string[] lines)
        {
            private int _index;
            internal void Exact(string expected)
            {
                if (_index >= lines.Length || lines[_index++] != expected)
                    throw Failure(SourceExactTransactionFailure.JournalInvalid,
                        "source-exact journal marker/order is invalid");
            }
            internal string Value(string key)
            {
                if (_index >= lines.Length ||
                    !lines[_index].StartsWith(key + "=", StringComparison.Ordinal))
                    throw Failure(SourceExactTransactionFailure.JournalInvalid,
                        "source-exact journal field/order is invalid");
                return lines[_index++][(key.Length + 1)..];
            }
            internal void End()
            {
                if (_index != lines.Length)
                    throw Failure(SourceExactTransactionFailure.JournalInvalid,
                        "source-exact journal has trailing fields");
            }
        }
    }
}

internal sealed record SourceExactInstallResult(
    string TargetPath,
    SourceExactInstalledStateDocument InstalledState,
    ExactDirectorySnapshot Snapshot);

internal enum SourceExactRecoveryResult
{
    NothingToRecover,
    RolledBack,
    CommittedRecovered
}

internal enum SourceExactTransactionFailure
{
    InvalidTarget,
    Locked,
    StageChanged,
    JournalCollision,
    JournalInvalid,
    ForeignMutation,
    InstalledStateInvalid,
    FileSystem,
    RollbackFailed
}

internal sealed class SourceExactTransactionException : Exception
{
    internal SourceExactTransactionException(
        SourceExactTransactionFailure failure,
        string message,
        Exception? innerException = null) : base(message, innerException) => Failure = failure;
    internal SourceExactTransactionFailure Failure { get; }
}

/// <summary>Internal same-process fixture seam; production never throws this type.</summary>
internal sealed class SourceExactSimulatedCrashException(string checkpoint) : Exception(checkpoint);
