using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Disabled Phase 3 primitive which downloads and verifies one already-resolved
/// source-exact ZIP into a private sibling directory. It has no install,
/// replacement, rollback, sidecar, UI, or ordinary-update call site.
/// </summary>
internal sealed class SourceExactZipStager
{
    internal const long MaximumCompressedBytes = 256L * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    internal const long MaximumOutputBytes = 1024L * 1024 * 1024;
    internal const long MaximumAggregateOutputBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(5);

    internal const string VersionMarkerFilename = "vt2updater_version.txt";
    private const string PrivateStagePrefix = ".vt2-source-exact-stage-";
    private const string ArchiveScratchFilename = ".source-exact-download.partial";
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;

    private static readonly Encoding StrictAscii = Encoding.GetEncoding(
        Encoding.ASCII.CodePage,
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);
    private static readonly Regex LowerSha256Pattern = new(
        "\\A[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseTagPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ReservedDevicePattern = new(
        "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly ISourceExactArchiveSource _source;
    private readonly TimeSpan _operationTimeout;

    internal SourceExactZipStager(
        ISourceExactArchiveSource source,
        TimeSpan? operationTimeout = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero ||
            _operationTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "staging timeout must be within (0, 30 minutes]");
        }
    }

    internal async Task<SourceExactZipStage> StageAsync(
        SourceExactRecoveryArtifact artifact,
        string intendedTargetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var contract = ValidateArtifact(artifact);
        var target = ValidateIntendedTarget(intendedTargetPath);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);

        string? stageDirectory = null;
        try
        {
            stageDirectory = CreatePrivateSiblingStage(target);
            var archivePath = Path.Combine(stageDirectory, ArchiveScratchFilename);

            var actualArchiveSha256 = await DownloadExactArchiveAsync(
                artifact,
                archivePath,
                deadline.Token).ConfigureAwait(false);
            var outputs = await ExtractAndVerifyAsync(
                artifact,
                contract,
                archivePath,
                stageDirectory,
                deadline.Token).ConfigureAwait(false);

            try
            {
                File.Delete(archivePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Failure(
                    SourceExactStageFailure.FileSystem,
                    "verified archive scratch file could not be removed from the stage",
                    ex);
            }

            await VerifyFinalStageAsync(
                contract,
                stageDirectory,
                deadline.Token).ConfigureAwait(false);

            var result = new SourceExactZipStage(
                stageDirectory,
                target,
                artifact,
                actualArchiveSha256,
                outputs);
            stageDirectory = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw Failure(
                SourceExactStageFailure.Timeout,
                "source-exact ZIP staging exceeded its linked deadline",
                ex);
        }
        catch (SourceExactStageException)
        {
            throw;
        }
        catch (SourceExactArchiveSourceException ex)
        {
            var failure = ex.Failure switch
            {
                SourceExactArchiveSourceFailure.ArtifactGone =>
                    SourceExactStageFailure.ArtifactGone,
                SourceExactArchiveSourceFailure.Remote =>
                    SourceExactStageFailure.Remote,
                _ => SourceExactStageFailure.InvalidArtifact
            };
            throw Failure(failure, ex.Message, ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw Failure(
                SourceExactStageFailure.MalformedArchive,
                "source-exact ZIP is malformed or uses an unsupported archive feature",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failure(
                SourceExactStageFailure.FileSystem,
                "source-exact ZIP staging failed while accessing its private stage",
                ex);
        }
        finally
        {
            if (stageDirectory is not null)
                SourceExactZipStage.DeletePrivateStageBestEffort(stageDirectory);
        }
    }

    private async Task<string> DownloadExactArchiveAsync(
        SourceExactRecoveryArtifact artifact,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using var download = await _source.OpenReadAsync(
            artifact,
            cancellationToken).ConfigureAwait(false);

        if (download.DeclaredLength is not null &&
            download.DeclaredLength.Value != artifact.AssetLength)
        {
            throw Failure(
                SourceExactStageFailure.IntegrityMismatch,
                "download Content-Length differs from the selected recovery coordinate");
        }

        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = artifact.AssetLength - total;
                var readSize = checked((int)Math.Min(buffer.Length, remaining + 1));
                int read;
                try
                {
                    read = await download.Content.ReadAsync(
                        buffer.AsMemory(0, readSize),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or
                    ObjectDisposedException or TimeoutException)
                {
                    throw Failure(
                        SourceExactStageFailure.Remote,
                        "source-exact archive stream failed during download",
                        ex);
                }

                if (read == 0)
                    break;
                if (total > artifact.AssetLength - read ||
                    total > MaximumCompressedBytes - read)
                {
                    throw Failure(
                        SourceExactStageFailure.CompressedLimitExceeded,
                        $"source-exact archive exceeds its {MaximumCompressedBytes}-byte bound " +
                        "or its declared exact length");
                }

                sha.AppendData(buffer, 0, read);
                try
                {
                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw Failure(
                        SourceExactStageFailure.FileSystem,
                        "private stage could not accept downloaded archive bytes",
                        ex);
                }
                total += read;
            }

            if (total != artifact.AssetLength)
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"source-exact archive length is {total}, expected {artifact.AssetLength}");
            }
            var actualSha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!FixedTimeHexEquals(artifact.AssetSha256, actualSha256))
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"source-exact archive SHA-256 is {actualSha256}, expected " +
                    artifact.AssetSha256);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return actualSha256;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<IReadOnlyList<SourceExactStagedOutput>> ExtractAndVerifyAsync(
        SourceExactRecoveryArtifact artifact,
        ArtifactContract contract,
        string archivePath,
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Count > MaximumEntries)
        {
            throw Failure(
                SourceExactStageFailure.EntryLimitExceeded,
                $"source-exact ZIP has {archive.Entries.Count} entries, over the " +
                $"{MaximumEntries}-entry bound");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactSeen = new HashSet<string>(StringComparer.Ordinal);
        long aggregate = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchiveEntry(entry);
            var name = entry.FullName;
            if (!seen.Add(name))
            {
                throw Failure(
                    SourceExactStageFailure.UnsafeEntry,
                    $"source-exact ZIP contains a duplicate or case-colliding entry '{name}'");
            }
            exactSeen.Add(name);

            if (!contract.ExpectedEntries.ContainsKey(name))
            {
                throw Failure(
                    SourceExactStageFailure.OutputSetMismatch,
                    $"source-exact ZIP contains undeclared entry '{name}'");
            }
            if (entry.Length < 0 || entry.Length > MaximumOutputBytes)
            {
                throw Failure(
                    SourceExactStageFailure.OutputLimitExceeded,
                    $"source-exact ZIP entry '{name}' exceeds the " +
                    $"{MaximumOutputBytes}-byte output bound");
            }
            if (aggregate > MaximumAggregateOutputBytes - entry.Length)
            {
                throw Failure(
                    SourceExactStageFailure.OutputLimitExceeded,
                    $"source-exact ZIP exceeds the {MaximumAggregateOutputBytes}-byte " +
                    "aggregate output bound");
            }
            aggregate += entry.Length;

        }

        if (exactSeen.Count != contract.ExpectedEntries.Count ||
            contract.ExpectedEntries.Keys.Any(name => !exactSeen.Contains(name)))
        {
            throw Failure(
                SourceExactStageFailure.OutputSetMismatch,
                "source-exact ZIP is missing one or more declared outputs or its exact version marker");
        }

        foreach (var entry in archive.Entries)
        {
            var expected = contract.ExpectedEntries[entry.FullName];
            if (entry.Length != expected.Length)
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"source-exact ZIP entry '{entry.FullName}' length is {entry.Length}, " +
                    $"expected {expected.Length}");
            }
        }

        var staged = new List<SourceExactStagedOutput>(artifact.Proof.Record.Output.Files.Count);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = contract.ExpectedEntries[entry.FullName];
            var outputPath = Path.Combine(stageDirectory, entry.FullName);
            var actual = await ExtractOneAsync(
                entry,
                outputPath,
                expected,
                cancellationToken).ConfigureAwait(false);
            if (!expected.IsVersionMarker)
                staged.Add(new SourceExactStagedOutput(entry.FullName, actual.Length, actual.Sha256));
        }

        var actualRows = artifact.Proof.Record.Output.Files
            .Select(expected =>
            {
                var actual = staged.Single(row =>
                    string.Equals(row.Filename, expected.Filename, StringComparison.Ordinal));
                return new RecoveryOutputFile(
                    actual.Filename,
                    actual.Length,
                    actual.Sha256,
                    expected.GitBlob);
            })
            .ToArray();
        var actualFingerprint = RecoveryRecordContract.ComputeOutputFingerprint(actualRows);
        if (!FixedTimeHexEquals(
                artifact.Proof.Record.Output.FingerprintSha256,
                actualFingerprint))
        {
            throw Failure(
                SourceExactStageFailure.IntegrityMismatch,
                "verified outputs do not reproduce the recovery output fingerprint");
        }

        return staged.AsReadOnly();
    }

    private static async Task<SourceExactStagedOutput> ExtractOneAsync(
        ZipArchiveEntry entry,
        string outputPath,
        ExpectedEntry expected,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = expected.Length - total;
                var readSize = checked((int)Math.Min(buffer.Length, remaining + 1));
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, readSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (total > expected.Length - read)
                {
                    throw Failure(
                        SourceExactStageFailure.IntegrityMismatch,
                        $"source-exact ZIP entry '{entry.FullName}' exceeds its declared length");
                }

                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                total += read;
            }

            if (total != expected.Length)
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"source-exact ZIP entry '{entry.FullName}' extracted {total} bytes, " +
                    $"expected {expected.Length}");
            }
            var actualSha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!FixedTimeHexEquals(expected.Sha256, actualSha256))
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"source-exact ZIP entry '{entry.FullName}' SHA-256 is " +
                    $"{actualSha256}, expected {expected.Sha256}");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return new SourceExactStagedOutput(entry.FullName, total, actualSha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task VerifyFinalStageAsync(
        ArtifactContract contract,
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        var directories = Directory.EnumerateDirectories(
            stageDirectory,
            "*",
            SearchOption.TopDirectoryOnly).ToArray();
        if (directories.Length != 0)
        {
            throw Failure(
                SourceExactStageFailure.UnsafeEntry,
                "private source-exact stage unexpectedly contains a nested directory");
        }

        var files = Directory.EnumerateFiles(
            stageDirectory,
            "*",
            SearchOption.TopDirectoryOnly).ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!names.Add(name) || !insensitive.Add(name) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(
                    SourceExactStageFailure.UnsafeEntry,
                    $"private source-exact stage contains an unsafe file '{name}'");
            }
        }
        if (names.Count != contract.ExpectedEntries.Count ||
            contract.ExpectedEntries.Keys.Any(name => !names.Contains(name)))
        {
            throw Failure(
                SourceExactStageFailure.OutputSetMismatch,
                "private source-exact stage does not contain the exact declared flat output set");
        }

        foreach (var (name, expected) in contract.ExpectedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(stageDirectory, name);
            var info = new FileInfo(path);
            if (info.Length != expected.Length)
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"staged file '{name}' length changed after extraction");
            }

            var actualSha256 = await ComputeFileSha256Async(path, cancellationToken)
                .ConfigureAwait(false);
            if (!FixedTimeHexEquals(expected.Sha256, actualSha256))
            {
                throw Failure(
                    SourceExactStageFailure.IntegrityMismatch,
                    $"staged file '{name}' SHA-256 changed after extraction");
            }
        }
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                sha.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static ArtifactContract ValidateArtifact(SourceExactRecoveryArtifact artifact)
    {
        var proof = artifact.Proof;
        if (proof is null || proof.Record is null)
        {
            throw Failure(
                SourceExactStageFailure.InvalidArtifact,
                "source-exact artifact is missing its validated recovery proof");
        }

        string semanticDigest;
        try
        {
            semanticDigest = RecoveryRecordContract.ComputeSemanticEquivalenceDigest(
                proof.Record);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            NullReferenceException or EncoderFallbackException or OverflowException)
        {
            throw Failure(
                SourceExactStageFailure.ProofDrift,
                "source-exact recovery proof can no longer be reproduced",
                ex);
        }
        var proofDigest = proof.SemanticEquivalenceSha256;
        if (!string.Equals(
                proof.SemanticEquivalenceAlgorithm,
                RecoveryRecordContract.SemanticEquivalenceAlgorithm,
                StringComparison.Ordinal) ||
            !LowerSha256Pattern.IsMatch(proofDigest ?? "") ||
            !FixedTimeHexEquals(proofDigest, semanticDigest))
        {
            throw Failure(
                SourceExactStageFailure.ProofDrift,
                "source-exact recovery proof changed after validation");
        }

        var record = proof.Record;
        var containerTag = artifact.ContainerReleaseTag;
        if (!string.Equals(
                artifact.Repository,
                RecoveryRecordContract.Repository,
                StringComparison.Ordinal) ||
            !string.Equals(artifact.Repository, record.Release.Repository, StringComparison.Ordinal) ||
            !string.Equals(artifact.OriginReleaseTag, record.Release.Tag, StringComparison.Ordinal) ||
            artifact.ContainerReleaseId <= 0 ||
            artifact.AssetId <= 0 ||
            artifact.ContainerPublishedAt == default ||
            artifact.EquivalentRecordCount <= 0 ||
            artifact.SurvivingCoordinateCount <= 0 ||
            artifact.SurvivingCoordinateCount > artifact.EquivalentRecordCount ||
            !ReleaseTagPattern.IsMatch(containerTag ?? "") ||
            !string.Equals(artifact.AssetFilename, record.Asset.Filename, StringComparison.Ordinal) ||
            artifact.AssetLength != record.Asset.Length ||
            !string.Equals(artifact.AssetSha256, record.Asset.Sha256, StringComparison.Ordinal))
        {
            throw Failure(
                SourceExactStageFailure.ProofDrift,
                "selected archive coordinate differs from its validated recovery proof");
        }
        if (artifact.AssetLength is <= 0 or > MaximumCompressedBytes)
        {
            throw Failure(
                SourceExactStageFailure.CompressedLimitExceeded,
                $"selected source-exact archive is outside the 1..{MaximumCompressedBytes} " +
                "byte compressed bound");
        }

        var expectedUrl = new Uri(
            $"https://github.com/{artifact.Repository}/releases/download/" +
            Uri.EscapeDataString(containerTag!) + "/" +
            Uri.EscapeDataString(artifact.AssetFilename),
            UriKind.Absolute);
        if (!Uri.TryCreate(artifact.AssetDownloadUrl, UriKind.Absolute, out var actualUrl) ||
            !string.Equals(
                actualUrl.AbsoluteUri,
                expectedUrl.AbsoluteUri,
                StringComparison.Ordinal))
        {
            throw Failure(
                SourceExactStageFailure.ProofDrift,
                "selected archive browser URL differs from its exact container coordinate");
        }

        byte[] markerBytes;
        try
        {
            markerBytes = StrictAscii.GetBytes(record.Version);
        }
        catch (EncoderFallbackException ex)
        {
            throw Failure(
                SourceExactStageFailure.InvalidArtifact,
                "recovery version cannot be represented by the producer's ASCII marker",
                ex);
        }
        if (markerBytes.Length is <= 0 or > 128)
        {
            throw Failure(
                SourceExactStageFailure.InvalidArtifact,
                "recovery version marker is outside its 1..128 byte contract");
        }

        var expected = new Dictionary<string, ExpectedEntry>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = markerBytes.Length;
        foreach (var row in record.Output.Files)
        {
            if (!expected.TryAdd(row.Filename, new ExpectedEntry(
                    row.Length,
                    row.Sha256,
                    IsVersionMarker: false)) ||
                !insensitive.Add(row.Filename))
            {
                throw Failure(
                    SourceExactStageFailure.ProofDrift,
                    "recovery output proof contains duplicate or case-colliding filenames");
            }
            if (row.Length is <= 0 or > MaximumOutputBytes)
            {
                throw Failure(
                    SourceExactStageFailure.OutputLimitExceeded,
                    $"recovery output '{row.Filename}' exceeds the per-output bound");
            }
            if (aggregate > MaximumAggregateOutputBytes - row.Length)
            {
                throw Failure(
                    SourceExactStageFailure.OutputLimitExceeded,
                    "recovery output proof exceeds the aggregate output bound");
            }
            aggregate += row.Length;
        }
        if (expected.Count == 0 || expected.Count >= MaximumEntries ||
            !insensitive.Add(VersionMarkerFilename))
        {
            throw Failure(
                SourceExactStageFailure.ProofDrift,
                "recovery output proof cannot form one bounded exact ZIP entry set");
        }

        var markerSha256 = Convert.ToHexString(SHA256.HashData(markerBytes)).ToLowerInvariant();
        expected.Add(
            VersionMarkerFilename,
            new ExpectedEntry(markerBytes.Length, markerSha256, IsVersionMarker: true));
        return new ArtifactContract(expected);
    }

    private static string ValidateIntendedTarget(string intendedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(intendedTargetPath))
        {
            throw Failure(
                SourceExactStageFailure.InvalidTarget,
                "intended target path is required");
        }

        string target;
        try
        {
            target = Path.GetFullPath(intendedTargetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            throw Failure(
                SourceExactStageFailure.InvalidTarget,
                "intended target path is not canonical",
                ex);
        }
        var root = Path.GetPathRoot(target);
        if (!string.IsNullOrEmpty(root))
        {
            while (target.Length > root.Length && Path.EndsInDirectorySeparator(target))
                target = target[..^1];
        }
        var parent = Directory.GetParent(target)?.FullName;
        if (string.IsNullOrEmpty(root) ||
            string.IsNullOrEmpty(parent) ||
            string.Equals(target, root, StringComparison.OrdinalIgnoreCase) ||
            !SourceExactTransactionFileSystem.SafeTargetLeaf(Path.GetFileName(target)) ||
            !Directory.Exists(parent) ||
            File.Exists(target))
        {
            throw Failure(
                SourceExactStageFailure.InvalidTarget,
                "intended target must be a directory path with an existing parent");
        }
        try
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 ||
                (Directory.Exists(target) &&
                 (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            {
                throw Failure(
                    SourceExactStageFailure.InvalidTarget,
                    "intended target or its direct parent is a reparse point");
            }
        }
        catch (SourceExactStageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failure(
                SourceExactStageFailure.InvalidTarget,
                "intended target parent metadata could not be validated",
                ex);
        }
        return target;
    }

    private static string CreatePrivateSiblingStage(string target)
    {
        var parent = Directory.GetParent(target)!.FullName;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var stage = Path.Combine(
                parent,
                PrivateStagePrefix + RandomNumberGenerator.GetHexString(24).ToLowerInvariant());
            if (Directory.Exists(stage) || File.Exists(stage))
                continue;
            try
            {
                Directory.CreateDirectory(stage);
                if (!string.Equals(
                        Path.GetPathRoot(stage),
                        Path.GetPathRoot(target),
                        StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                {
                    SourceExactZipStage.DeletePrivateStageBestEffort(stage);
                    throw Failure(
                        SourceExactStageFailure.InvalidTarget,
                        "private stage is not a normal directory on the intended target volume");
                }
                return stage;
            }
            catch (SourceExactStageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Failure(
                    SourceExactStageFailure.FileSystem,
                    "private same-volume stage could not be created",
                    ex);
            }
        }

        throw Failure(
            SourceExactStageFailure.FileSystem,
            "private same-volume stage name could not be reserved");
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (string.IsNullOrEmpty(name) ||
            !string.Equals(name, entry.Name, StringComparison.Ordinal) ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.Contains(':') ||
            name is "." or ".." ||
            !string.Equals(name.TrimEnd(' ', '.'), name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Any(char.IsControl))
        {
            throw Failure(
                SourceExactStageFailure.UnsafeEntry,
                $"source-exact ZIP entry '{name}' is not one canonical flat Windows leaf");
        }

        var firstDot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = firstDot >= 0 ? name[..firstDot] : name;
        if (ReservedDevicePattern.IsMatch(stem))
        {
            throw Failure(
                SourceExactStageFailure.UnsafeEntry,
                $"source-exact ZIP entry '{name}' uses a reserved Windows device name");
        }

        var attributes = unchecked((uint)entry.ExternalAttributes);
        var windowsAttributes = attributes & 0xFFFF;
        var unixType = (attributes >> 16) & UnixFileTypeMask;
        if ((windowsAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            (windowsAttributes & (uint)FileAttributes.Directory) != 0 ||
            (unixType != 0 && unixType != UnixRegularFile))
        {
            throw Failure(
                SourceExactStageFailure.UnsafeEntry,
                $"source-exact ZIP entry '{name}' carries reparse or non-regular metadata");
        }
    }

    private static bool FixedTimeHexEquals(string? expected, string? actual)
    {
        if (!LowerSha256Pattern.IsMatch(expected ?? "") ||
            !LowerSha256Pattern.IsMatch(actual ?? ""))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected!),
            Convert.FromHexString(actual!));
    }

    private static SourceExactStageException Failure(
        SourceExactStageFailure failure,
        string message,
        Exception? inner = null) => new(failure, message, inner);

    private sealed record ExpectedEntry(long Length, string Sha256, bool IsVersionMarker);
    private sealed record ArtifactContract(
        IReadOnlyDictionary<string, ExpectedEntry> ExpectedEntries);
}

internal sealed class SourceExactZipStage : IDisposable
{
    private readonly object _ownershipGate = new();
    private readonly SourceExactStageArtifactBinding _artifactBinding;
    private SourceExactTransactionFileSystem.DirectoryLease? _directoryLease;
    private int _ownershipState;

    internal SourceExactZipStage(
        string stageDirectory,
        string intendedTargetPath,
        SourceExactRecoveryArtifact artifact,
        string archiveSha256,
        IReadOnlyList<SourceExactStagedOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(outputs);
        StageDirectory = SourceExactTransactionFileSystem.Normalize(stageDirectory);
        IntendedTargetPath = SourceExactTransactionFileSystem.Normalize(intendedTargetPath);
        ArchiveSha256 = archiveSha256;
        Outputs = Array.AsReadOnly(outputs
            .OrderBy(row => row.Filename, StringComparer.Ordinal)
            .Select(row => new SourceExactStagedOutput(
                row.Filename,
                row.Length,
                row.Sha256))
            .ToArray());
        Version = artifact.Proof.Record.Version;
        InstalledState = SourceExactInstalledState.Create(artifact, Outputs);
        _artifactBinding = SourceExactStageArtifactBinding.From(artifact);
        var directory = SourceExactTransactionFileSystem.OpenDirectory(StageDirectory);
        try
        {
            VerifiedSnapshot = SourceExactTransactionFileSystem.Snapshot(directory);
            RequireInitialSnapshot();
            _directoryLease = directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    internal string StageDirectory { get; }
    internal string IntendedTargetPath { get; }
    internal string ArchiveSha256 { get; }
    internal IReadOnlyList<SourceExactStagedOutput> Outputs { get; }
    internal string Version { get; }
    internal SourceExactInstalledStateDocument InstalledState { get; }
    internal ExactDirectorySnapshot VerifiedSnapshot { get; }

    internal SourceExactStageTransfer TransferOwnership(
        SourceExactRecoveryArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_ownershipGate)
        {
            if (_ownershipState != 0)
                throw new InvalidOperationException(
                    "source-exact stage was already transferred or disposed");
            if (_artifactBinding != SourceExactStageArtifactBinding.From(artifact))
                throw new InvalidDataException(
                    "source-exact stage artifact binding differs from the transfer request");
            var lease = _directoryLease ??
                throw new InvalidOperationException("source-exact stage lease is missing");
            lease.RequireCurrentPath();
            using (var guard = SourceExactTransactionFileSystem.GuardDirectory(StageDirectory))
                guard.RequireExact(VerifiedSnapshot);
            var transfer = new SourceExactStageTransfer(
                StageDirectory,
                IntendedTargetPath,
                ArchiveSha256,
                Version,
                Outputs,
                InstalledState,
                VerifiedSnapshot,
                lease);
            _ownershipState = 1;
            _directoryLease = null;
            return transfer;
        }
    }

    public void Dispose()
    {
        SourceExactTransactionFileSystem.DirectoryLease? lease;
        lock (_ownershipGate)
        {
            if (_ownershipState != 0) return;
            _ownershipState = 2;
            lease = _directoryLease;
            _directoryLease = null;
        }
        if (lease is null) return;
        try
        {
            SourceExactTransactionFileSystem.DeleteOwnedExactDirectory(
                lease,
                VerifiedSnapshot,
                "phase3-stage");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Dispose is fail-closed: if the held physical lease no longer owns
            // the verified path, preserve both namespaces for diagnosis. Never
            // turn a path replacement into authority to delete the replacement.
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void RequireInitialSnapshot()
    {
        var markerBytes = Encoding.ASCII.GetBytes(Version);
        var marker = VerifiedSnapshot.Files.SingleOrDefault(row =>
            row.Name == SourceExactZipStager.VersionMarkerFilename);
        if (marker is null || marker.Length != markerBytes.Length ||
            marker.Sha256 != Convert.ToHexString(SHA256.HashData(markerBytes)).ToLowerInvariant())
            throw new InvalidDataException(
                "verified stage snapshot does not contain the exact version marker");
        var rows = VerifiedSnapshot.Files
            .Where(row => row.Name != SourceExactZipStager.VersionMarkerFilename)
            .Select(row => new SourceExactStagedOutput(row.Name, row.Length, row.Sha256))
            .ToArray();
        if (!rows.SequenceEqual(Outputs))
            throw new InvalidDataException(
                "verified stage snapshot differs from its exact output proof");
    }

    internal static void DeletePrivateStageBestEffort(string stageDirectory)
    {
        try
        {
            DeletePrivateStage(stageDirectory);
        }
        catch
        {
            // Failure cleanup must preserve the original typed staging error.
            // The random sibling is inert and never aliases the intended target.
        }
    }

    private static void DeletePrivateStage(string stageDirectory)
    {
        if (!Directory.Exists(stageDirectory))
            return;
        var leaf = Path.GetFileName(stageDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (!leaf.StartsWith(".vt2-source-exact-stage-", StringComparison.Ordinal))
            throw new IOException("refusing to remove a directory outside the private stage namespace");
        if (Directory.EnumerateDirectories(stageDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            throw new IOException("refusing to recurse through an unexpected private-stage directory");
        foreach (var file in Directory.EnumerateFiles(
                     stageDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(stageDirectory, recursive: false);
    }

    private sealed record SourceExactStageArtifactBinding(
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
        string SemanticAlgorithm,
        string SemanticSha256,
        string ComputedSemanticSha256,
        int EquivalentRecordCount,
        int SurvivingCoordinateCount)
    {
        internal static SourceExactStageArtifactBinding From(
            SourceExactRecoveryArtifact artifact) => new(
                artifact.Repository,
                artifact.OriginReleaseTag,
                artifact.ContainerReleaseId,
                artifact.ContainerReleaseTag,
                artifact.ContainerPublishedAt,
                artifact.AssetId,
                artifact.AssetFilename,
                artifact.AssetLength,
                artifact.AssetSha256,
                artifact.AssetDownloadUrl,
                artifact.Proof.SemanticEquivalenceAlgorithm,
                artifact.Proof.SemanticEquivalenceSha256,
                RecoveryRecordContract.ComputeSemanticEquivalenceDigest(
                    artifact.Proof.Record),
                artifact.EquivalentRecordCount,
                artifact.SurvivingCoordinateCount);
    }
}

internal sealed class SourceExactStageTransfer : IDisposable
{
    private SourceExactTransactionFileSystem.DirectoryLease? _lease;

    internal SourceExactStageTransfer(
        string stageDirectory,
        string intendedTargetPath,
        string archiveSha256,
        string version,
        IReadOnlyList<SourceExactStagedOutput> outputs,
        SourceExactInstalledStateDocument installedState,
        ExactDirectorySnapshot verifiedSnapshot,
        SourceExactTransactionFileSystem.DirectoryLease lease)
    {
        StageDirectory = stageDirectory;
        IntendedTargetPath = intendedTargetPath;
        ArchiveSha256 = archiveSha256;
        Version = version;
        Outputs = Array.AsReadOnly(outputs.Select(row => row with { }).ToArray());
        InstalledState = installedState with
        {
            Outputs = Array.AsReadOnly(
                installedState.Outputs.Select(row => row with { }).ToArray())
        };
        VerifiedSnapshot = verifiedSnapshot with
        {
            Files = Array.AsReadOnly(
                verifiedSnapshot.Files.Select(row => row with { }).ToArray())
        };
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    internal string StageDirectory { get; }
    internal string IntendedTargetPath { get; }
    internal string ArchiveSha256 { get; }
    internal string Version { get; }
    internal IReadOnlyList<SourceExactStagedOutput> Outputs { get; }
    internal SourceExactInstalledStateDocument InstalledState { get; }
    internal ExactDirectorySnapshot VerifiedSnapshot { get; }
    internal SourceExactTransactionFileSystem.DirectoryLease Lease => _lease ??
        throw new ObjectDisposedException(nameof(SourceExactStageTransfer));

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}

internal sealed record SourceExactStagedOutput(
    string Filename,
    long Length,
    string Sha256);

internal enum SourceExactStageFailure
{
    InvalidArtifact,
    ProofDrift,
    InvalidTarget,
    ArtifactGone,
    Remote,
    Timeout,
    CompressedLimitExceeded,
    EntryLimitExceeded,
    OutputLimitExceeded,
    UnsafeEntry,
    OutputSetMismatch,
    IntegrityMismatch,
    MalformedArchive,
    FileSystem
}

internal sealed class SourceExactStageException : Exception
{
    internal SourceExactStageException(
        SourceExactStageFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    internal SourceExactStageFailure Failure { get; }
}
