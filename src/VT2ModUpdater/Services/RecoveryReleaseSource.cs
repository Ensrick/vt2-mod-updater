namespace VT2ModUpdater.Services;

public interface IRecoveryReleaseSource
{
    Task<RecoveryReleasePage> GetReleasePageAsync(
        string repository,
        int pageNumber,
        int pageSize,
        int maximumAssets,
        CancellationToken cancellationToken);

    Task<RecoveryPageRevalidation> RevalidateReleasePageAsync(
        string repository,
        int pageNumber,
        int pageSize,
        string entityTag,
        CancellationToken cancellationToken);

    Task<RecoveryManifestFetch> GetManifestAsync(
        string repository,
        long releaseId,
        string releaseTag,
        long assetId,
        string assetName,
        string browserDownloadUrl,
        int maximumBytes,
        CancellationToken cancellationToken);
}

public sealed record RecoveryReleasePage(
    string Repository,
    int PageNumber,
    int PageSize,
    string EntityTag,
    bool HasNextPage,
    IReadOnlyList<RecoveryReleaseSummary> Releases);

public sealed record RecoveryReleaseSummary(
    long Id,
    string TagName,
    DateTimeOffset? PublishedAt,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<RecoveryReleaseAssetSummary> Assets);

public sealed record RecoveryReleaseAssetSummary(
    long Id,
    string Name,
    long Size,
    string BrowserDownloadUrl = "",
    string DigestSha256 = "");

public enum RecoveryPageRevalidation
{
    Unchanged,
    Changed
}

public enum RecoveryManifestFetchStatus
{
    Found,
    Gone
}

public sealed record RecoveryManifestFetch(
    RecoveryManifestFetchStatus Status,
    ReadOnlyMemory<byte> Bytes)
{
    public static RecoveryManifestFetch Gone { get; } =
        new(RecoveryManifestFetchStatus.Gone, ReadOnlyMemory<byte>.Empty);
}

public enum RecoveryReleaseSourceFailure
{
    Remote,
    Contract,
    HistoryBoundExceeded,
    ManifestBoundExceeded,
    AssetBoundExceeded
}

public sealed class RecoveryReleaseSourceException : Exception
{
    public RecoveryReleaseSourceException(
        RecoveryReleaseSourceFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public RecoveryReleaseSourceFailure Failure { get; }
}
