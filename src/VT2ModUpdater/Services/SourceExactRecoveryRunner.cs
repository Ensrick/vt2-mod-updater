using System.IO;
using System.Text;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Application-level result for one explicit recovery action. Coordinator
/// success is not surfaced as success until the exact installed-state sidecar
/// and version marker have both been read back from the synthetic target.
/// </summary>
internal enum SourceExactRecoveryRunStatus
{
    Succeeded,
    Failed,
    ReadBackFailed,
    Cancelled
}

internal sealed record SourceExactRecoveryRunResult(
    SourceExactRecoveryRunStatus Status,
    SourceExactRecoveryOutcome Outcome,
    string Message,
    SourceExactInstalledReadBack? ReadBack = null);

internal sealed record SourceExactInstalledReadBack(
    SourceExactInstalledStateDocument State,
    string InstalledVersion);

/// <summary>
/// The sole surface consumed by the view model. There is deliberately no
/// ordinary-release or legacy-deployment method on this interface.
/// </summary>
internal interface ISourceExactRecoveryRunner : IDisposable
{
    Task<SourceExactRecoveryRunResult> RecoverAndVerifyAsync(
        SourceExactRecoveryRequest request,
        CancellationToken cancellationToken = default);
}

internal interface ISourceExactInstalledStateReader
{
    SourceExactInstalledReadBack Read(
        SourceExactRecoveryRequest request,
        string targetPath,
        string expectedVersion);
}

/// <summary>
/// Explicit composition boundary used only by the recovery command. Ordinary
/// update selection does not reference this type.
/// </summary>
internal sealed class SourceExactRecoveryRunner : ISourceExactRecoveryRunner
{
    private readonly ISourceExactRecoveryCoordinator _coordinator;
    private readonly ISourceExactInstalledStateReader _reader;
    private int _disposed;

    internal SourceExactRecoveryRunner()
        : this(
            new SourceExactRecoveryCoordinator(),
            new SourceExactInstalledStateReader())
    { }

    internal SourceExactRecoveryRunner(
        ISourceExactRecoveryCoordinator coordinator,
        ISourceExactInstalledStateReader reader)
    {
        _coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<SourceExactRecoveryRunResult> RecoverAndVerifyAsync(
        SourceExactRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(request);

        SourceExactRecoveryOutcome outcome;
        try
        {
            outcome = await _coordinator.RecoverAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new SourceExactRecoveryOutcome(
                SourceExactRecoveryStatus.Cancelled,
                SourceExactRecoveryFailure.Cancelled,
                "source-exact recovery was cancelled");
        }
        catch (Exception ex)
        {
            outcome = new SourceExactRecoveryOutcome(
                SourceExactRecoveryStatus.ContractFailure,
                SourceExactRecoveryFailure.ResolutionContractFailure,
                $"source-exact coordinator escaped its terminal result boundary: {ex.Message}");
        }

        if (outcome.Status == SourceExactRecoveryStatus.Cancelled)
        {
            return new SourceExactRecoveryRunResult(
                SourceExactRecoveryRunStatus.Cancelled,
                outcome,
                "Source-exact recovery was cancelled; no new install was authorized.");
        }
        if (outcome.Status != SourceExactRecoveryStatus.Succeeded)
        {
            return new SourceExactRecoveryRunResult(
                SourceExactRecoveryRunStatus.Failed,
                outcome,
                $"Source-exact recovery failed ({outcome.Status}/{outcome.Failure}): " +
                outcome.Message);
        }
        if (string.IsNullOrWhiteSpace(outcome.TargetPath))
        {
            return ReadBackFailure(
                outcome,
                "the successful coordinator result omitted its synthetic target");
        }
        if (!SourceExactInstalledStateReader.IsCanonicalVersion(
                outcome.ResolvedVersion))
        {
            return ReadBackFailure(
                outcome,
                "the successful coordinator result omitted its canonical resolved version");
        }

        SourceExactInstalledReadBack readBack;
        try
        {
            readBack = _reader.Read(
                request,
                outcome.TargetPath,
                outcome.ResolvedVersion!);
            if (!string.Equals(
                    readBack.State.ModId,
                    request.ModId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    readBack.State.WorkshopId,
                    request.WorkshopId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    readBack.State.SourceCommit,
                    request.SourceCommit,
                    StringComparison.Ordinal))
            {
                return ReadBackFailure(
                    outcome,
                    "installed-state identity differs from the requested exact tuple");
            }
            if (!string.Equals(
                    readBack.InstalledVersion,
                    outcome.ResolvedVersion,
                    StringComparison.Ordinal))
            {
                return ReadBackFailure(
                    outcome,
                    "installed version marker differs from the resolved historical version");
            }
        }
        catch (Exception ex)
        {
            return ReadBackFailure(outcome, ex.Message);
        }

        var abbreviated = request.SourceCommit.Length > 12
            ? request.SourceCommit[..12]
            : request.SourceCommit;
        return new SourceExactRecoveryRunResult(
            SourceExactRecoveryRunStatus.Succeeded,
            outcome,
            $"Recovered exact source {abbreviated} ({readBack.InstalledVersion}); " +
            "installed-state read-back matched.",
            readBack);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _coordinator.Dispose();
    }

    private static SourceExactRecoveryRunResult ReadBackFailure(
        SourceExactRecoveryOutcome outcome,
        string message) => new(
        SourceExactRecoveryRunStatus.ReadBackFailed,
        outcome,
        "Recovery transaction completed, but exact installed-state read-back " +
        $"failed: {message}");
}

/// <summary>
/// Strict, bounded reader for the two files committed by the reviewed exact
/// transaction. It does not infer source authority from legacy sidecars or
/// installed mod bytes.
/// </summary>
internal sealed class SourceExactInstalledStateReader :
    ISourceExactInstalledStateReader
{
    internal const int MaximumVersionBytes = 128;

    public SourceExactInstalledReadBack Read(
        SourceExactRecoveryRequest request,
        string targetPath,
        string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCanonicalVersion(expectedVersion))
        {
            throw new InvalidDataException(
                "resolved historical version is not canonical text");
        }
        if (string.IsNullOrWhiteSpace(targetPath) ||
            !Path.IsPathFullyQualified(targetPath))
        {
            throw new InvalidDataException(
                "source-exact read-back target is not fully qualified");
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.WorkshopContentRoot));
        var expectedTarget = Deployer.GetSyntheticFolder(root, request.WorkshopId);
        var canonicalTarget = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(targetPath));
        if (!string.Equals(
                canonicalTarget,
                expectedTarget,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "source-exact read-back was directed at a foreign target");
        }

        var stateBytes = ReadBoundedRegularFile(
            Path.Combine(canonicalTarget, SourceExactInstalledState.Filename),
            SourceExactInstalledState.MaximumBytes,
            "installed-state sidecar");
        var state = SourceExactInstalledState.Parse(stateBytes);

        var versionBytes = ReadBoundedRegularFile(
            Path.Combine(
                canonicalTarget,
                SourceExactZipStager.VersionMarkerFilename),
            MaximumVersionBytes,
            "version marker");
        if (versionBytes.Length == 0 || versionBytes.Any(value => value > 0x7f))
        {
            throw new InvalidDataException(
                "source-exact version marker is not non-empty ASCII");
        }
        var version = Encoding.ASCII.GetString(versionBytes);
        if (!IsCanonicalVersion(version))
        {
            throw new InvalidDataException(
                "source-exact version marker is not canonical text");
        }
        if (!versionBytes.AsSpan().SequenceEqual(
                Encoding.ASCII.GetBytes(expectedVersion)))
        {
            throw new InvalidDataException(
                "source-exact version marker differs byte-for-byte from the resolved historical version");
        }

        return new SourceExactInstalledReadBack(state, version);
    }

    internal static bool IsCanonicalVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumVersionBytes &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(character => character <= 0x7f && !char.IsControl(character));

    private static byte[] ReadBoundedRegularFile(
        string path,
        int maximumBytes,
        string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"source-exact {description} is unreadable",
                ex);
        }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"source-exact {description} is not a regular file");
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            var declaredLength = stream.Length;
            if (declaredLength <= 0 || declaredLength > maximumBytes)
            {
                throw new InvalidDataException(
                    $"source-exact {description} exceeds its byte bound");
            }

            var bytes = new byte[checked((int)declaredLength)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"source-exact {description} ended before its declared length");
                }
                offset += read;
            }
            if (stream.ReadByte() != -1 || stream.Length != declaredLength)
            {
                throw new InvalidDataException(
                    $"source-exact {description} changed during read-back");
            }
            return bytes;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"source-exact {description} could not be read exactly",
                ex);
        }
    }
}
