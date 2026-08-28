using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VT2ModUpdater.Services;

/// <summary>
/// Read-only GitHub transport for the recovery resolver. Page navigation is
/// generated locally from bounded integers; untrusted Link headers are never
/// followed. Manifest redirects are restricted to GitHub's release CDN.
/// </summary>
public sealed class GitHubRecoveryReleaseSource : IRecoveryReleaseSource, IDisposable
{
    internal const int MaximumReleasePageBytes = 8 * 1024 * 1024;
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumRedirects = 3;
    private const int MaximumLinkHeaderBytes = 8 * 1024;
    private const string ApiHost = "api.github.com";
    private const string GitHubHost = "github.com";
    private const string ReleaseAssetHost = "release-assets.githubusercontent.com";
    private const string ObjectsAssetHost = "objects.githubusercontent.com";

    private enum AssetRedirectState
    {
        GitHubReleaseCoordinate,
        ApprovedCdn
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex CanonicalNonnegativeInteger = new(
        "\\A(0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex LinkSegmentPattern = new(
        "\\A<(?<uri>[^<>\\s]+)>;[ ]*rel=\"(?<rel>next|prev|first|last)\"\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseTagPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex NumericRepositoryPathPattern = new(
        "\\A/repositories/(?<id>[1-9][0-9]*)/releases\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex PaginationQueryPattern = new(
        "\\A\\?per_page=(?<size>[1-9][0-9]*)&page=(?<page>[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex UtcTimestampPattern = new(
        "\\A[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T" +
        "([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\\z",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _operationTimeout;

    public GitHubRecoveryReleaseSource()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = true;
        _operationTimeout = DefaultOperationTimeout;
    }

    internal GitHubRecoveryReleaseSource(
        HttpClient httpClient,
        TimeSpan? operationTimeout = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttp = false;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero ||
            _operationTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "operation timeout must be within (0, 10 minutes]");
        }
    }

    public Task<RecoveryReleasePage> GetReleasePageAsync(
        string repository,
        int pageNumber,
        int pageSize,
        int maximumAssets,
        CancellationToken cancellationToken)
    {
        ValidateRepositoryAndPage(repository, pageNumber, pageSize, maximumAssets);
        return ExecuteWithDeadlineAsync(
            "GitHub release-page operation",
            cancellationToken,
            token => GetReleasePageCoreAsync(
                repository,
                pageNumber,
                pageSize,
                maximumAssets,
                token));
    }

    private async Task<RecoveryReleasePage> GetReleasePageCoreAsync(
        string repository,
        int pageNumber,
        int pageSize,
        int maximumAssets,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildReleasePageUri(repository, pageNumber, pageSize);
        using var request = NewRequest(HttpMethod.Get, requestUri, "application/vnd.github+json");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        RequireExactApiResponseUri(response, requestUri);
        if (!response.IsSuccessStatusCode)
            throw Remote($"GitHub release page returned HTTP {(int)response.StatusCode}");

        var entityTag = response.Headers.ETag?.ToString();
        if (!IsCanonicalEntityTag(entityTag))
            throw Contract("GitHub release page is missing a bounded ETag");

        var bytes = await ReadBoundedAsync(
            response.Content,
            MaximumReleasePageBytes,
            "GitHub release page",
            RecoveryReleaseSourceFailure.HistoryBoundExceeded,
            cancellationToken).ConfigureAwait(false);
        var releases = ParseReleasePage(bytes, maximumAssets, cancellationToken);

        // The Link header is validated as numeric same-service metadata, but
        // its URI is never followed. The next request is always generated from
        // the source-qualified repository and bounded page number.
        var hasNextPage = ReadValidatedHasNextPage(
            response,
            repository,
            pageNumber,
            pageSize,
            releases.Count);
        return new RecoveryReleasePage(
            repository,
            pageNumber,
            pageSize,
            entityTag!,
            hasNextPage,
            releases);
    }

    public Task<RecoveryPageRevalidation> RevalidateReleasePageAsync(
        string repository,
        int pageNumber,
        int pageSize,
        string entityTag,
        CancellationToken cancellationToken)
    {
        ValidateRepositoryAndPage(
            repository,
            pageNumber,
            pageSize,
            RecoveryHistoryResolver.MaximumTotalAssets);
        if (!IsCanonicalEntityTag(entityTag))
            throw Contract("release page revalidation ETag is not canonical");

        return ExecuteWithDeadlineAsync(
            "GitHub release-page revalidation",
            cancellationToken,
            token => RevalidateReleasePageCoreAsync(
                repository,
                pageNumber,
                pageSize,
                entityTag,
                token));
    }

    private async Task<RecoveryPageRevalidation> RevalidateReleasePageCoreAsync(
        string repository,
        int pageNumber,
        int pageSize,
        string entityTag,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildReleasePageUri(repository, pageNumber, pageSize);
        using var request = NewRequest(HttpMethod.Get, requestUri, "application/vnd.github+json");
        if (!request.Headers.TryAddWithoutValidation("If-None-Match", entityTag))
            throw Contract("release page revalidation ETag cannot be represented");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        RequireExactApiResponseUri(response, requestUri);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var returnedEntityTag = response.Headers.ETag?.ToString();
            if (!IsCanonicalEntityTag(returnedEntityTag))
                throw Contract("GitHub 304 response is missing an exact canonical ETag");
            return string.Equals(returnedEntityTag, entityTag, StringComparison.Ordinal)
                ? RecoveryPageRevalidation.Unchanged
                : RecoveryPageRevalidation.Changed;
        }
        if (response.IsSuccessStatusCode)
            return RecoveryPageRevalidation.Changed;
        throw Remote($"GitHub release page revalidation returned HTTP " +
            $"{(int)response.StatusCode}");
    }

    public Task<RecoveryManifestFetch> GetManifestAsync(
        string repository,
        long releaseId,
        string releaseTag,
        long assetId,
        string assetName,
        string browserDownloadUrl,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repository);
        if (releaseId <= 0 || assetId <= 0)
            throw Contract("manifest coordinate uses a non-positive release or asset id");
        if (!IsCanonicalText(releaseTag, 128) ||
            !ReleaseTagPattern.IsMatch(releaseTag))
        {
            throw Contract("manifest coordinate release tag is not canonical");
        }
        if (!string.Equals(assetName, "manifest.json", StringComparison.Ordinal))
            throw Contract("manifest coordinate does not name exact 'manifest.json'");
        if (maximumBytes is < 1 or > RecoveryHistoryResolver.MaximumManifestBytes)
            throw Contract("manifest byte bound is outside the resolver contract");

        var currentUri = ValidateBrowserAssetUri(
            browserDownloadUrl,
            repository,
            releaseTag,
            assetName);

        return ExecuteWithDeadlineAsync(
            "GitHub manifest operation",
            cancellationToken,
            token => GetManifestCoreAsync(currentUri, maximumBytes, token));
    }

    private async Task<RecoveryManifestFetch> GetManifestCoreAsync(
        Uri initialUri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        var redirectState = AssetRedirectState.GitHubReleaseCoordinate;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = NewRequest(
                HttpMethod.Get,
                currentUri,
                "application/octet-stream");
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            RequireAllowedAssetResponseUri(response, currentUri);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return RecoveryManifestFetch.Gone;
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects)
                    throw Contract("GitHub manifest redirect depth exceeded its bound");
                (currentUri, redirectState) = ValidateRedirect(
                    currentUri,
                    response.Headers.Location,
                    redirectState);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw Remote($"GitHub manifest asset returned HTTP {(int)response.StatusCode}");

            var bytes = await ReadBoundedAsync(
                response.Content,
                maximumBytes,
                "GitHub manifest asset",
                RecoveryReleaseSourceFailure.ManifestBoundExceeded,
                cancellationToken).ConfigureAwait(false);
            return new RecoveryManifestFetch(
                RecoveryManifestFetchStatus.Found,
                bytes);
        }

        throw Contract("GitHub manifest redirect state was unreachable");
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<T> ExecuteWithDeadlineAsync<T>(
        string operation,
        CancellationToken callerToken,
        Func<CancellationToken, Task<T>> action)
    {
        callerToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(_operationTimeout);
        try
        {
            return await action(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            callerToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (RecoveryReleaseSourceException)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw Remote($"{operation} exceeded its linked deadline", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or
            ObjectDisposedException or TimeoutException)
        {
            throw Remote($"{operation} failed while reading remote content", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            NotSupportedException or ArgumentException or FormatException)
        {
            throw Contract($"{operation} returned malformed transport state", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw Remote("GitHub recovery request timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Remote("GitHub recovery request failed", ex);
        }
    }

    private static IReadOnlyList<RecoveryReleaseSummary> ParseReleasePage(
        ReadOnlyMemory<byte> utf8Json,
        int maximumAssets,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreflightReleasePageBounds(utf8Json.Span, maximumAssets, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 24
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw Contract("GitHub release page root must be a JSON array");
            var count = document.RootElement.GetArrayLength();
            if (count > RecoveryHistoryResolver.ReleasesPerPage)
            {
                throw Bound(
                    RecoveryReleaseSourceFailure.HistoryBoundExceeded,
                    "GitHub release page exceeds its release-count bound");
            }

            var rawRows = new RawReleaseRow[count];
            var aggregateAssetCount = 0;
            var index = 0;
            foreach (var releaseElement in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = $"releases[{index}]";
                var release = CheckedObject.Read(releaseElement, path);
                var assetsElement = RequireArray(release.Require("assets"), $"{path}.assets");
                var assetCount = assetsElement.GetArrayLength();
                if (assetCount > RecoveryHistoryResolver.MaximumAssetsPerRelease)
                {
                    throw Bound(
                        RecoveryReleaseSourceFailure.AssetBoundExceeded,
                        $"{path}.assets exceeds the " +
                        $"{RecoveryHistoryResolver.MaximumAssetsPerRelease}-asset bound");
                }
                if (aggregateAssetCount > maximumAssets - assetCount)
                {
                    throw Bound(
                        RecoveryReleaseSourceFailure.AssetBoundExceeded,
                        "GitHub release page exceeds the remaining aggregate asset budget");
                }
                aggregateAssetCount += assetCount;
                rawRows[index] = new RawReleaseRow(release, assetsElement, assetCount);
                index++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var releases = new RecoveryReleaseSummary[count];
            for (index = 0; index < rawRows.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = $"releases[{index}]";
                var raw = rawRows[index];
                var draft = ReadBoolean(raw.Release.Require("draft"), $"{path}.draft");
                var publishedAt = ReadPublishedAt(
                    raw.Release.Require("published_at"),
                    $"{path}.published_at",
                    draft);

                var assets = new RecoveryReleaseAssetSummary[raw.AssetCount];
                var assetIndex = 0;
                foreach (var assetElement in raw.Assets.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var assetPath = $"{path}.assets[{assetIndex}]";
                    var asset = CheckedObject.Read(assetElement, assetPath);
                    assets[assetIndex] = new RecoveryReleaseAssetSummary(
                        ReadPositiveInt64(asset.Require("id"), $"{assetPath}.id"),
                        ReadCanonicalString(asset.Require("name"), $"{assetPath}.name", 256),
                        ReadNonnegativeInt64(asset.Require("size"), $"{assetPath}.size"),
                        ReadCanonicalString(
                            asset.Require("browser_download_url"),
                            $"{assetPath}.browser_download_url",
                            2048),
                        ReadSha256Digest(
                            asset.Require("digest"),
                            $"{assetPath}.digest"));
                    assetIndex++;
                }

                releases[index] = new RecoveryReleaseSummary(
                    ReadPositiveInt64(raw.Release.Require("id"), $"{path}.id"),
                    ReadCanonicalString(
                        raw.Release.Require("tag_name"), $"{path}.tag_name", 128),
                    publishedAt,
                    draft,
                    ReadBoolean(
                        raw.Release.Require("prerelease"), $"{path}.prerelease"),
                    Array.AsReadOnly(assets));
            }
            return Array.AsReadOnly(releases);
        }
        catch (RecoveryReleaseSourceException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw Contract($"GitHub release page JSON is malformed: {ex.Message}", ex);
        }
        catch (EncoderFallbackException ex)
        {
            throw Contract("GitHub release page contains invalid Unicode", ex);
        }
    }

    private static void PreflightReleasePageBounds(
        ReadOnlySpan<byte> utf8Json,
        int maximumAssets,
        CancellationToken cancellationToken)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 24
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw Contract("GitHub release page root must be a JSON array");

        var releaseCount = 0;
        var aggregateAssetCount = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                if (reader.Read())
                    throw Contract("GitHub release page has trailing JSON content");
                return;
            }

            releaseCount++;
            if (releaseCount > RecoveryHistoryResolver.ReleasesPerPage)
            {
                throw Bound(
                    RecoveryReleaseSourceFailure.HistoryBoundExceeded,
                    "GitHub release page exceeds its release-count bound");
            }
            if (reader.TokenType != JsonTokenType.StartObject)
                throw Contract($"releases[{releaseCount - 1}] must be a JSON object");

            PreflightReleaseObject(
                ref reader,
                releaseCount - 1,
                maximumAssets,
                ref aggregateAssetCount,
                cancellationToken);
        }

        throw Contract("GitHub release page JSON is incomplete");
    }

    private static void PreflightReleaseObject(
        ref Utf8JsonReader reader,
        int releaseIndex,
        int maximumAssets,
        ref int aggregateAssetCount,
        CancellationToken cancellationToken)
    {
        var sawAssets = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw Contract($"releases[{releaseIndex}] has malformed object grammar");

            var isAssets = reader.ValueTextEquals("assets"u8) ||
                string.Equals(reader.GetString(), "assets", StringComparison.OrdinalIgnoreCase);
            if (!reader.Read())
                throw Contract($"releases[{releaseIndex}] has an incomplete property");
            if (!isAssets)
            {
                SkipCurrentJsonValue(ref reader, cancellationToken);
                continue;
            }
            if (sawAssets)
                throw Contract($"releases[{releaseIndex}] repeats assets metadata");
            sawAssets = true;
            if (reader.TokenType != JsonTokenType.StartArray)
                throw Contract($"releases[{releaseIndex}].assets must be a JSON array");

            var releaseAssetCount = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                releaseAssetCount++;
                aggregateAssetCount++;
                if (releaseAssetCount > RecoveryHistoryResolver.MaximumAssetsPerRelease)
                {
                    throw Bound(
                        RecoveryReleaseSourceFailure.AssetBoundExceeded,
                        $"releases[{releaseIndex}].assets exceeds the " +
                        $"{RecoveryHistoryResolver.MaximumAssetsPerRelease}-asset bound");
                }
                if (aggregateAssetCount > maximumAssets)
                {
                    throw Bound(
                        RecoveryReleaseSourceFailure.AssetBoundExceeded,
                        "GitHub release page exceeds the remaining aggregate asset budget");
                }

                // The first excess child is rejected above, before its value is
                // traversed or represented by a JsonDocument node.
                SkipCurrentJsonValue(ref reader, cancellationToken);
            }
        }

        throw Contract($"releases[{releaseIndex}] JSON object is incomplete");
    }

    private static void SkipCurrentJsonValue(
        ref Utf8JsonReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject))
            return;

        var openContainers = 1;
        while (openContainers > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read())
                throw Contract("GitHub release page JSON value is incomplete");
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                openContainers++;
            else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                openContainers--;
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        string label,
        RecoveryReleaseSourceFailure boundFailure,
        CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is > int.MaxValue || declaredLength > maximumBytes)
            throw Bound(boundFailure, $"{label} exceeds the {maximumBytes}-byte bound");
        if (declaredLength is < 0)
            throw Contract($"{label} declares a negative content length");

        var effectiveLimit = declaredLength is not null
            ? Math.Min(maximumBytes, checked((int)declaredLength.Value))
            : maximumBytes;
        var initialCapacity = effectiveLimit > 0
            ? effectiveLimit
            : Math.Min(16 * 1024, maximumBytes);
        using var output = new MemoryStream(initialCapacity);
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var remaining = effectiveLimit - checked((int)output.Length);
                var readLength = Math.Min(buffer.Length, remaining + 1);
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length > effectiveLimit - read)
                {
                    if (declaredLength is not null && declaredLength.Value < maximumBytes)
                        throw Contract($"{label} length exceeds Content-Length");
                    throw Bound(boundFailure, $"{label} exceeds the {maximumBytes}-byte bound");
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (declaredLength is not null && output.Length != declaredLength.Value)
            throw Contract($"{label} length differs from Content-Length");
        return output.ToArray();
    }

    private static HttpRequestMessage NewRequest(
        HttpMethod method,
        Uri uri,
        string accept)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VT2ModUpdater", "0.3"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static Uri BuildReleasePageUri(
        string repository,
        int pageNumber,
        int pageSize) =>
        new(
            $"https://{ApiHost}/repos/{repository}/releases?per_page=" +
            pageSize.ToString(CultureInfo.InvariantCulture) + "&page=" +
            pageNumber.ToString(CultureInfo.InvariantCulture),
            UriKind.Absolute);

    private static bool ReadValidatedHasNextPage(
        HttpResponseMessage response,
        string repository,
        int pageNumber,
        int pageSize,
        int releaseCount)
    {
        var relations = ParseLinkRelations(response, repository, pageSize);
        var hasNext = relations.TryGetValue("next", out var nextPage);
        if (hasNext && (pageNumber == int.MaxValue || nextPage != pageNumber + 1))
            throw Contract("GitHub Link next relation is not the next numeric page");
        if (relations.TryGetValue("prev", out var previousPage) &&
            (pageNumber <= 1 || previousPage != pageNumber - 1))
        {
            throw Contract("GitHub Link prev relation is not the previous numeric page");
        }
        if (relations.TryGetValue("first", out var firstPage) && firstPage != 1)
            throw Contract("GitHub Link first relation does not name page 1");
        if (relations.TryGetValue("last", out var lastPage) &&
            (hasNext ? lastPage < nextPage : lastPage != pageNumber))
        {
            throw Contract("GitHub Link last relation contradicts current pagination");
        }
        if (releaseCount == pageSize && !hasNext)
            throw Contract("a full GitHub release page lacks a validated next relation");
        if (releaseCount < pageSize && hasNext)
            throw Contract("a short GitHub release page falsely advertises a next relation");
        return hasNext;
    }

    private static Dictionary<string, int> ParseLinkRelations(
        HttpResponseMessage response,
        string repository,
        int pageSize)
    {
        var relations = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Link", out var headerValues))
            return relations;

        var headerBuilder = new StringBuilder();
        var headerValueCount = 0;
        var headerByteCount = 0;
        foreach (var value in headerValues)
        {
            headerValueCount++;
            if (headerValueCount > 8 || !IsCanonicalText(value, MaximumLinkHeaderBytes))
                throw Contract("GitHub Link metadata exceeds its transport-value bound");
            var separatorBytes = headerBuilder.Length == 0 ? 0 : 1;
            var addedBytes = StrictUtf8.GetByteCount(value);
            if (headerByteCount >
                MaximumLinkHeaderBytes - separatorBytes - addedBytes)
            {
                throw Contract("GitHub Link metadata exceeds its byte bound");
            }
            if (headerBuilder.Length > 0)
                headerBuilder.Append(',');
            headerBuilder.Append(value);
            headerByteCount += separatorBytes + addedBytes;
        }
        var header = headerBuilder.ToString();
        if (!IsCanonicalText(header, MaximumLinkHeaderBytes))
            throw Contract("GitHub Link metadata is empty, non-canonical, or oversized");

        string? numericRepositoryId = null;
        var commaCount = 0;
        foreach (var character in header)
        {
            if (character == ',' && ++commaCount >= 4)
                throw Contract("GitHub Link metadata exceeds the four-relation bound");
        }
        var segments = header.Split(',');
        if (segments.Length is < 1 or > 4)
            throw Contract("GitHub Link metadata exceeds the four-relation bound");
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            var match = LinkSegmentPattern.Match(segment);
            if (!match.Success)
                throw Contract("GitHub Link metadata has unsupported grammar");
            var relation = match.Groups["rel"].Value;
            if (relations.ContainsKey(relation))
                throw Contract($"GitHub Link metadata repeats rel=\"{relation}\"");

            var page = ValidatePaginationUri(
                match.Groups["uri"].Value,
                repository,
                pageSize,
                ref numericRepositoryId);
            relations.Add(relation, page);
        }
        return relations;
    }

    private static int ValidatePaginationUri(
        string value,
        string repository,
        int pageSize,
        ref string? numericRepositoryId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            uri.Port != 443 ||
            !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Contract("GitHub Link metadata targets an untrusted URI");
        }

        var expectedNamedPath = $"/repos/{repository}/releases";
        if (!string.Equals(uri.AbsolutePath, expectedNamedPath, StringComparison.Ordinal))
        {
            var pathMatch = NumericRepositoryPathPattern.Match(uri.AbsolutePath);
            if (!pathMatch.Success)
                throw Contract("GitHub Link metadata has a foreign repository path");
            var repositoryId = pathMatch.Groups["id"].Value;
            if (repositoryId.Length > 20 ||
                !ulong.TryParse(
                    repositoryId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                throw Contract("GitHub Link metadata has an invalid numeric repository identity");
            }
            if (numericRepositoryId is not null &&
                !string.Equals(
                    numericRepositoryId,
                    repositoryId,
                    StringComparison.Ordinal))
            {
                throw Contract("GitHub Link metadata mixes numeric repository identities");
            }
            numericRepositoryId = repositoryId;
        }

        var query = PaginationQueryPattern.Match(uri.Query);
        if (!query.Success ||
            !int.TryParse(
                query.Groups["size"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var actualPageSize) ||
            actualPageSize != pageSize ||
            !int.TryParse(
                query.Groups["page"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var page) ||
            page <= 0)
        {
            throw Contract("GitHub Link metadata has non-canonical numeric pagination");
        }
        return page;
    }

    private static void ValidateRepositoryAndPage(
        string repository,
        int pageNumber,
        int pageSize,
        int maximumAssets)
    {
        ValidateRepository(repository);
        if (pageNumber is < 1 or > RecoveryHistoryResolver.MaximumPages ||
            pageSize != RecoveryHistoryResolver.ReleasesPerPage)
        {
            throw Contract("release page request is outside the resolver bounds");
        }
        if (maximumAssets is < 0 or > RecoveryHistoryResolver.MaximumTotalAssets)
            throw Contract("release page asset budget is outside the resolver bounds");
    }

    private static void ValidateRepository(string repository)
    {
        if (!string.Equals(
                repository,
                RecoveryRecordContract.Repository,
                StringComparison.Ordinal))
        {
            throw Contract("recovery source refuses a foreign repository");
        }
    }

    private static void RequireExactApiResponseUri(
        HttpResponseMessage response,
        Uri expected)
    {
        var actual = response.RequestMessage?.RequestUri;
        if (actual is null || !string.Equals(
                actual.AbsoluteUri,
                expected.AbsoluteUri,
                StringComparison.Ordinal))
        {
            throw Contract("GitHub release response came from an unexpected URI");
        }
    }

    private static void RequireAllowedAssetResponseUri(
        HttpResponseMessage response,
        Uri expected)
    {
        var actual = response.RequestMessage?.RequestUri;
        if (actual is null || !string.Equals(
                actual.AbsoluteUri,
                expected.AbsoluteUri,
                StringComparison.Ordinal) ||
            !IsAllowedAssetUri(actual))
        {
            throw Contract("GitHub manifest response came from an unexpected URI");
        }
    }

    private static (Uri Uri, AssetRedirectState State) ValidateRedirect(
        Uri current,
        Uri? location,
        AssetRedirectState state)
    {
        if (location is null)
            throw Contract("GitHub manifest redirect is missing Location");
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (!IsCanonicalText(next.AbsoluteUri, 8192) || !IsAllowedAssetUri(next))
            throw Contract("GitHub manifest redirect targets an untrusted URI");
        if (state == AssetRedirectState.GitHubReleaseCoordinate)
        {
            if (!string.Equals(current.Host, GitHubHost, StringComparison.OrdinalIgnoreCase) ||
                !IsApprovedCdnUri(next))
            {
                throw Contract("GitHub manifest redirect did not leave for an approved release CDN");
            }
            return (next, AssetRedirectState.ApprovedCdn);
        }

        if (!IsApprovedCdnUri(current) || !IsApprovedCdnUri(next))
            throw Contract("GitHub manifest CDN redirect attempted to leave or re-enter its CDN boundary");
        return (next, AssetRedirectState.ApprovedCdn);
    }

    private static Uri ValidateBrowserAssetUri(
        string browserDownloadUrl,
        string repository,
        string releaseTag,
        string assetName)
    {
        if (!Uri.TryCreate(browserDownloadUrl, UriKind.Absolute, out var actual))
            throw Contract("manifest browser download URL is not absolute");
        var expected = new Uri(
            $"https://{GitHubHost}/{repository}/releases/download/" +
            Uri.EscapeDataString(releaseTag) + "/" + Uri.EscapeDataString(assetName),
            UriKind.Absolute);
        if (!string.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal))
            throw Contract("manifest browser download URL is foreign or mismatched");
        return actual;
    }

    private static bool IsAllowedAssetUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, ReleaseAssetHost, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, ObjectsAssetHost, StringComparison.OrdinalIgnoreCase));

    private static bool IsApprovedCdnUri(Uri uri) =>
        IsAllowedAssetUri(uri) &&
        (string.Equals(uri.Host, ReleaseAssetHost, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, ObjectsAssetHost, StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static JsonElement RequireArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Contract($"{path} must be a JSON array");
        return element;
    }

    private static bool ReadBoolean(JsonElement element, string path)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Contract($"{path} must be a JSON boolean");
        return element.GetBoolean();
    }

    private static DateTimeOffset? ReadPublishedAt(
        JsonElement element,
        string path,
        bool draft)
    {
        if (draft && element.ValueKind == JsonValueKind.Null)
            return null;
        var value = ReadCanonicalString(element, path, 64);
        if (!UtcTimestampPattern.IsMatch(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw Contract($"{path} must be a UTC ISO-8601 timestamp");
        }
        return parsed;
    }

    private static long ReadPositiveInt64(JsonElement element, string path)
    {
        var value = ReadNonnegativeInt64(element, path);
        if (value <= 0)
            throw Contract($"{path} must be a canonical positive Int64");
        return value;
    }

    private static long ReadNonnegativeInt64(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw Contract($"{path} must be a JSON integer number");
        var raw = element.GetRawText();
        if (!CanonicalNonnegativeInteger.IsMatch(raw) ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw Contract($"{path} must be a canonical nonnegative Int64");
        }
        return value;
    }

    private static string ReadCanonicalString(
        JsonElement element,
        string path,
        int maximumUtf8Bytes)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Contract($"{path} must be a JSON string");
        var value = element.GetString() ?? throw Contract($"{path} must not be null");
        if (!IsCanonicalText(value, maximumUtf8Bytes))
            throw Contract($"{path} is empty, non-canonical, or exceeds its byte bound");
        return value;
    }

    private static string ReadSha256Digest(JsonElement element, string path)
    {
        var value = ReadCanonicalString(element, path, 71);
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != 71)
            throw Contract($"{path} must be an exact lowercase sha256 digest");
        var digest = value[prefix.Length..];
        if (digest.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw Contract($"{path} must be an exact lowercase sha256 digest");
        }
        return digest;
    }

    private static bool IsCanonicalEntityTag(string? value) =>
        IsCanonicalText(value, 256) &&
        EntityTagHeaderValue.TryParse(value, out var parsed) &&
        !string.Equals(parsed.Tag, "*", StringComparison.Ordinal);

    private static bool IsCanonicalText(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumUtf8Bytes ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return false;
        }
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static RecoveryReleaseSourceException Remote(
        string message,
        Exception? inner = null) =>
        new(RecoveryReleaseSourceFailure.Remote, message, inner);

    private static RecoveryReleaseSourceException Contract(
        string message,
        Exception? inner = null) =>
        new(RecoveryReleaseSourceFailure.Contract, message, inner);

    private static RecoveryReleaseSourceException Bound(
        RecoveryReleaseSourceFailure failure,
        string message) =>
        new(failure, message);

    private sealed record RawReleaseRow(
        CheckedObject Release,
        JsonElement Assets,
        int AssetCount);

    private sealed class CheckedObject
    {
        private readonly Dictionary<string, JsonElement> _properties;

        private CheckedObject(Dictionary<string, JsonElement> properties) =>
            _properties = properties;

        public JsonElement Require(string name) =>
            _properties.TryGetValue(name, out var value)
                ? value
                : throw Contract($"GitHub object is missing required property '{name}'");

        public static CheckedObject Read(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw Contract($"{path} must be a JSON object");
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                    throw Contract($"{path} contains duplicate property '{property.Name}'");
            }
            return new CheckedObject(properties);
        }
    }
}
