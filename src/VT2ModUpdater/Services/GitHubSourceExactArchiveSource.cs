using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Numeric-asset GitHub transport used only by the disabled source-exact ZIP
/// stager. Redirects may leave the API only for the two GitHub release CDNs;
/// every hop is generated or validated locally and authorization is never
/// added to a CDN request.
/// </summary>
internal sealed class GitHubSourceExactArchiveSource : ISourceExactArchiveSource, IDisposable
{
    private const int MaximumRedirects = 3;
    private const string ApiHost = "api.github.com";
    private const string ReleaseAssetHost = "release-assets.githubusercontent.com";
    private const string ObjectsAssetHost = "objects.githubusercontent.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    internal GitHubSourceExactArchiveSource()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = true;
    }

    internal GitHubSourceExactArchiveSource(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (httpClient.DefaultRequestHeaders.Authorization is not null ||
            httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            throw new ArgumentException(
                "source-exact archive transport requires a client without default authorization; " +
                "credentials must never flow to release CDN hops",
                nameof(httpClient));
        }
        _ownsHttp = false;
    }

    public async Task<SourceExactArchiveDownload> OpenReadAsync(
        SourceExactRecoveryArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(
                artifact.Repository,
                RecoveryRecordContract.Repository,
                StringComparison.Ordinal))
        {
            throw Contract("source-exact archive transport refuses a foreign repository");
        }
        if (artifact.ContainerReleaseId <= 0 || artifact.AssetId <= 0)
            throw Contract("source-exact archive coordinate uses a non-positive numeric id");

        var currentUri = new Uri(
            $"https://{ApiHost}/repos/{artifact.Repository}/releases/assets/" +
            artifact.AssetId.ToString(CultureInfo.InvariantCulture),
            UriKind.Absolute);
        var onCdn = false;

        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = NewRequest(currentUri, onCdn);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or
                ObjectDisposedException or TimeoutException)
            {
                throw Remote("GitHub source-exact archive request failed", ex);
            }

            if (!HasExactResponseUri(response, currentUri))
            {
                response.Dispose();
                throw Contract("GitHub source-exact archive response came from an unexpected URI");
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                response.Dispose();
                throw new SourceExactArchiveSourceException(
                    SourceExactArchiveSourceFailure.ArtifactGone,
                    "the selected source-exact archive is no longer hosted");
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects)
                {
                    response.Dispose();
                    throw Contract("GitHub source-exact archive redirect depth exceeded its bound");
                }

                var next = ValidateRedirect(currentUri, response.Headers.Location, onCdn);
                response.Dispose();
                currentUri = next;
                onCdn = true;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw Remote($"GitHub source-exact archive returned HTTP {status}");
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or
                ObjectDisposedException or InvalidOperationException)
            {
                response.Dispose();
                throw Remote("GitHub source-exact archive stream could not be opened", ex);
            }

            try
            {
                return new SourceExactArchiveDownload(
                    stream,
                    response.Content.Headers.ContentLength,
                    currentUri,
                    response);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                response.Dispose();
                throw;
            }
        }

        throw Contract("GitHub source-exact archive redirect state was unreachable");
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static HttpRequestMessage NewRequest(Uri uri, bool onCdn)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VT2ModUpdater", "0.3"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (!onCdn)
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static Uri ValidateRedirect(Uri current, Uri? location, bool onCdn)
    {
        if (location is null)
            throw Contract("GitHub source-exact archive redirect is missing Location");
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (!IsApprovedCdnUri(next))
            throw Contract("GitHub source-exact archive redirect targets an untrusted URI");
        if (onCdn && !IsApprovedCdnUri(current))
            throw Contract("GitHub source-exact archive redirect state is inconsistent");
        if (!onCdn && !string.Equals(current.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
            throw Contract("GitHub source-exact archive did not start at its numeric API coordinate");
        return next;
    }

    private static bool HasExactResponseUri(HttpResponseMessage response, Uri expected)
    {
        var actual = response.RequestMessage?.RequestUri;
        return actual is not null && string.Equals(
            actual.AbsoluteUri,
            expected.AbsoluteUri,
            StringComparison.Ordinal);
    }

    private static bool IsApprovedCdnUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsoluteUri.Length <= 8192 &&
        (string.Equals(uri.Host, ReleaseAssetHost, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, ObjectsAssetHost, StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static SourceExactArchiveSourceException Contract(
        string message,
        Exception? inner = null) =>
        new(SourceExactArchiveSourceFailure.Contract, message, inner);

    private static SourceExactArchiveSourceException Remote(
        string message,
        Exception? inner = null) =>
        new(SourceExactArchiveSourceFailure.Remote, message, inner);
}
