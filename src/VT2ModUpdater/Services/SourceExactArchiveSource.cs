using System.IO;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Disabled transport seam for one already-resolved source-exact archive.
/// It is deliberately not part of the ordinary latest-release update path.
/// </summary>
internal interface ISourceExactArchiveSource
{
    Task<SourceExactArchiveDownload> OpenReadAsync(
        SourceExactRecoveryArtifact artifact,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns one streaming archive response. The stream and its HTTP response (when
/// present) remain alive until the stager has finished hashing the payload.
/// </summary>
internal sealed class SourceExactArchiveDownload : IAsyncDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    internal SourceExactArchiveDownload(
        Stream content,
        long? declaredLength,
        Uri finalUri,
        IDisposable? owner = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (!content.CanRead)
            throw new ArgumentException("archive content stream must be readable", nameof(content));
        if (declaredLength < 0)
            throw new ArgumentOutOfRangeException(nameof(declaredLength));
        FinalUri = finalUri ?? throw new ArgumentNullException(nameof(finalUri));
        DeclaredLength = declaredLength;
        _owner = owner;
    }

    internal Stream Content { get; }
    internal long? DeclaredLength { get; }
    internal Uri FinalUri { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Content.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}

internal enum SourceExactArchiveSourceFailure
{
    ArtifactGone,
    Remote,
    Contract
}

internal sealed class SourceExactArchiveSourceException : Exception
{
    internal SourceExactArchiveSourceException(
        SourceExactArchiveSourceFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    internal SourceExactArchiveSourceFailure Failure { get; }
}
