using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public sealed class GitHubSourceExactArchiveSourceTests
{
    [Fact]
    public async Task UsesNumericAssetCoordinateAndStreamsApprovedCdnResponse()
    {
        var bytes = Encoding.ASCII.GetBytes("archive bytes");
        var calls = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/object?token=fixture");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubSourceExactArchiveSource(http);

        await using var download = await source.OpenReadAsync(Artifact(), default);
        using var output = new MemoryStream();
        await download.Content.CopyToAsync(output);

        Assert.Equal(bytes, output.ToArray());
        Assert.Equal(bytes.Length, download.DeclaredLength);
        Assert.Equal("release-assets.githubusercontent.com", download.FinalUri.Host);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(
            "https://api.github.com/repos/Ensrick/vermintide-2-tweaker/releases/assets/200",
            handler.Calls[0].RequestUri!.AbsoluteUri);
        Assert.Equal("application/octet-stream", handler.Calls[0].Accept.Single().MediaType);
        Assert.Contains("X-GitHub-Api-Version", handler.Calls[0].Headers);
        Assert.DoesNotContain("X-GitHub-Api-Version", handler.Calls[1].Headers);
        Assert.Null(handler.Calls[0].Authorization);
        Assert.Null(handler.Calls[1].Authorization);
    }

    [Fact]
    public async Task NumericApiMayReturnArchiveWithoutRedirect()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            }));
        using var http = new HttpClient(handler);
        using var source = new GitHubSourceExactArchiveSource(http);

        await using var download = await source.OpenReadAsync(Artifact(), default);

        Assert.Equal(bytes, await ReadAllAsync(download.Content));
        Assert.Single(handler.Calls);
        Assert.Equal("api.github.com", download.FinalUri.Host);
    }

    [Theory]
    [InlineData("https://github.com/Ensrick/vermintide-2-tweaker/releases/download/mods/x.zip")]
    [InlineData("https://example.com/asset")]
    [InlineData("http://release-assets.githubusercontent.com/asset")]
    public async Task InitialRedirectToAnythingButApprovedCdnIsRejected(string location)
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(location);
            return Task.FromResult(redirect);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubSourceExactArchiveSource(http);

        var exception = await Assert.ThrowsAsync<SourceExactArchiveSourceException>(() =>
            source.OpenReadAsync(Artifact(), default));

        Assert.Equal(SourceExactArchiveSourceFailure.Contract, exception.Failure);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task CdnCannotReturnToGitHubOrTriggerThirdRequest()
    {
        var calls = 0;
        var handler = new ScriptedHandler((_, _) =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = calls == 1
                ? new Uri("https://objects.githubusercontent.com/object?token=x")
                : new Uri(
                    "https://github.com/Ensrick/vermintide-2-tweaker/releases/download/" +
                    "mods-container-2026-08-28/mx.zip");
            return Task.FromResult(redirect);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubSourceExactArchiveSource(http);

        var exception = await Assert.ThrowsAsync<SourceExactArchiveSourceException>(() =>
            source.OpenReadAsync(Artifact(), default));

        Assert.Equal(SourceExactArchiveSourceFailure.Contract, exception.Failure);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task DeletedNumericAssetHasTypedGoneFailure(HttpStatusCode status)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        using var source = new GitHubSourceExactArchiveSource(http);

        var exception = await Assert.ThrowsAsync<SourceExactArchiveSourceException>(() =>
            source.OpenReadAsync(Artifact(), default));

        Assert.Equal(SourceExactArchiveSourceFailure.ArtifactGone, exception.Failure);
    }

    [Fact]
    public async Task CallerCancellationDuringSendIsPreserved()
    {
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var source = new GitHubSourceExactArchiveSource(http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.OpenReadAsync(Artifact(), cancellation.Token));
    }

    [Fact]
    public async Task UnexpectedResponseUriIsRejected()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1 }),
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://objects.githubusercontent.com/foreign")
            };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var source = new GitHubSourceExactArchiveSource(http);

        var exception = await Assert.ThrowsAsync<SourceExactArchiveSourceException>(() =>
            source.OpenReadAsync(Artifact(), default));

        Assert.Equal(SourceExactArchiveSourceFailure.Contract, exception.Failure);
    }

    [Fact]
    public void DefaultAuthorizationIsRejectedBeforeAnyRequestCanReachCdn()
    {
        using var http = new HttpClient(new ScriptedHandler((_, _) =>
            throw new InvalidOperationException("must not send")));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        var exception = Assert.Throws<ArgumentException>(() =>
            new GitHubSourceExactArchiveSource(http));

        Assert.Contains("authorization", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceExactRecoveryArtifact Artifact() => new(
        RecoveryRecordContract.Repository,
        "mods-fixture-2026-08-26",
        100,
        "mods-container-2026-08-28",
        DateTimeOffset.Parse("2026-08-28T12:00:00Z"),
        200,
        "mx.zip",
        546,
        new string('a', 64),
        "https://github.com/Ensrick/vermintide-2-tweaker/releases/download/" +
            "mods-container-2026-08-28/mx.zip",
        null!,
        1,
        1);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _send;

        internal ScriptedHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
            _send = send;

        internal List<CapturedRequest> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls.Add(new CapturedRequest(
                request.RequestUri,
                request.Headers.Accept.ToArray(),
                request.Headers.Authorization,
                request.Headers.Select(header => header.Key).ToArray()));
            var response = await _send(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed record CapturedRequest(
        Uri? RequestUri,
        MediaTypeWithQualityHeaderValue[] Accept,
        AuthenticationHeaderValue? Authorization,
        string[] Headers);
}
