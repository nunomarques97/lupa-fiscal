using System.Net;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public class PoliteHttpClientTests
{
    private static readonly string PdfA = TestListing.PdfUrl("PIV_1.pdf");
    private static readonly string PdfB = TestListing.PdfUrl("PIV_2.pdf");

    private readonly FakeClock _clock = new();
    private readonly FakeHandler _handler;

    public PoliteHttpClientTests()
    {
        _handler = new FakeHandler(_clock);
        _handler.Always(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.NotFound));
    }

    private PoliteHttpClient Client(CrawlerOptions? options = null) =>
        new(_handler, options ?? new CrawlerOptions(), _clock, TextWriter.Null);

    private static Task<FetchResponse> Get(PoliteHttpClient client, string url, long maxBytes = 1_000_000) =>
        client.GetAsync(new Uri(url), "application/pdf", maxBytes, CancellationToken.None);

    [Fact]
    public async Task SendsTheDescriptiveUserAgentOnEveryRequest()
    {
        _handler.Always(PdfA, () => FakeHandler.Bytes("%PDF-1.5"u8.ToArray()));
        using var client = Client();

        await Get(client, PdfA);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r =>
        {
            Assert.Equal(CrawlerOptions.DefaultUserAgent, r.UserAgent);
            Assert.Contains("LupaFiscal", r.UserAgent, StringComparison.Ordinal);
            Assert.Contains("https://github.com/", r.UserAgent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FetchesRobotsFirstAndOnlyOnce()
    {
        _handler.Always(PdfA, () => FakeHandler.Bytes([1]));
        _handler.Always(PdfB, () => FakeHandler.Bytes([2]));
        using var client = Client();

        await Get(client, PdfA);
        await Get(client, PdfB);

        Assert.Equal(TestListing.RobotsUrl, _handler.Requests[0].Url.AbsoluteUri);
        Assert.Equal(1, _handler.CountFor(TestListing.RobotsUrl));
    }

    [Fact]
    public async Task KeepsAtLeastTheMinimumIntervalBetweenRequests()
    {
        _handler.Always(PdfA, () => FakeHandler.Bytes([1]));
        _handler.Always(PdfB, () => FakeHandler.Bytes([2]));
        _handler.TransferTime = TimeSpan.FromMilliseconds(300);
        using var client = Client();

        for (var i = 0; i < 3; i++)
        {
            await Get(client, PdfA);
            await Get(client, PdfB);
        }

        Assert.Equal(7, _handler.Requests.Count);
        for (var i = 1; i < _handler.Requests.Count; i++)
        {
            var previousEnd = _handler.Requests[i - 1].Start + _handler.TransferTime;
            Assert.True(_handler.Requests[i].Start - previousEnd >= TimeSpan.FromSeconds(1),
                $"request {i} started {(_handler.Requests[i].Start - previousEnd).TotalMilliseconds} ms after the previous one ended");
        }
    }

    [Fact]
    public async Task HonoursALongerConfiguredInterval()
    {
        _handler.Always(PdfA, () => FakeHandler.Bytes([1]));
        using var client = Client(new CrawlerOptions { RequestInterval = TimeSpan.FromSeconds(3) });

        await Get(client, PdfA);
        await Get(client, PdfA);

        for (var i = 1; i < _handler.Requests.Count; i++)
        {
            Assert.True(_handler.Requests[i].Start - _handler.Requests[i - 1].Start >= TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(999)]
    public void RejectsAnIntervalBelowOneSecond(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerOptions { RequestInterval = TimeSpan.FromMilliseconds(milliseconds) });
    }

    [Fact]
    public void RejectsAnUnboundedRetryCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerOptions { MaxRetries = CrawlerOptions.MaxAllowedRetries + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrawlerOptions { MaxRetries = -1 });
    }

    [Fact]
    public async Task RetriesAfter429HonouringRetryAfter()
    {
        _handler.Then(PdfA, () => FakeHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)));
        _handler.Then(PdfA, () => FakeHandler.Bytes("%PDF-"u8.ToArray()));
        using var client = Client();

        var response = await Get(client, PdfA);

        Assert.True(response.IsSuccess);
        Assert.Equal(2, _handler.CountFor(PdfA));
        Assert.Contains(TimeSpan.FromSeconds(30), _clock.Delays);
        var attempts = _handler.Requests.Where(r => r.Url.AbsoluteUri == PdfA).ToList();
        Assert.True(attempts[1].Start - attempts[0].Start >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RetriesAfter503WithExponentialBackoff()
    {
        _handler.Then(PdfA, () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        _handler.Then(PdfA, () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        _handler.Then(PdfA, () => FakeHandler.Bytes([1]));
        using var client = Client(new CrawlerOptions { InitialBackoff = TimeSpan.FromSeconds(5) });

        var response = await Get(client, PdfA);

        Assert.True(response.IsSuccess);
        Assert.Equal(3, _handler.CountFor(PdfA));
        Assert.Contains(TimeSpan.FromSeconds(5), _clock.Delays);
        Assert.Contains(TimeSpan.FromSeconds(10), _clock.Delays);
    }

    [Fact]
    public async Task CapsTheNumberOfRetries()
    {
        _handler.Always(PdfA, () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        using var client = Client(new CrawlerOptions { MaxRetries = 3 });

        var error = await Assert.ThrowsAsync<CrawlFetchException>(() => Get(client, PdfA));

        Assert.Equal(4, _handler.CountFor(PdfA));
        Assert.Contains("503", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivesUpWhenRetryAfterExceedsTheLimit()
    {
        _handler.Always(PdfA, () => FakeHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2)));
        using var client = Client();

        await Assert.ThrowsAsync<CrawlFetchException>(() => Get(client, PdfA));

        Assert.Equal(1, _handler.CountFor(PdfA));
        Assert.DoesNotContain(TimeSpan.FromHours(2), _clock.Delays);
    }

    [Fact]
    public async Task ReturnsANotFoundWithoutRetrying()
    {
        using var client = Client();

        var response = await Get(client, PdfA);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal(1, _handler.CountFor(PdfA));
    }

    [Fact]
    public async Task NeverRequestsAPathDisallowedByRobots()
    {
        _handler.Always(TestListing.RobotsUrl, () => FakeHandler.Text("User-agent: *\nDisallow: /pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_1.pdf"));
        _handler.Always(PdfB, () => FakeHandler.Bytes([2]));
        using var client = Client();

        await Assert.ThrowsAsync<RobotsDisallowedException>(() => Get(client, PdfA));
        var allowed = await Get(client, PdfB);

        Assert.Equal(0, _handler.CountFor(PdfA));
        Assert.True(allowed.IsSuccess);
    }

    [Fact]
    public async Task NeverRequestsAPathDisallowedByARobotsFileWithAByteOrderMark()
    {
        byte[] body = [0xEF, 0xBB, 0xBF, .. "User-agent: *\nDisallow: /"u8];
        _handler.Always(TestListing.RobotsUrl, () => FakeHandler.Bytes(body, "text/plain"));
        using var client = Client();

        await Assert.ThrowsAsync<RobotsDisallowedException>(() => Get(client, PdfA));

        Assert.Equal(0, _handler.CountFor(PdfA));
    }

    [Fact]
    public async Task RobotsNotFoundAllowsEverything()
    {
        using var client = Client();

        var rules = await client.GetRobotsAsync(new Uri(TestListing.Origin), CancellationToken.None);

        Assert.Same(RobotsRules.AllowAll, rules);
    }

    [Fact]
    public async Task UnreachableRobotsDisallowsEverything()
    {
        _handler.Always(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        using var client = Client(new CrawlerOptions { MaxRetries = 1 });

        await Assert.ThrowsAsync<RobotsDisallowedException>(() => Get(client, PdfA));

        Assert.Equal(0, _handler.CountFor(PdfA));
        Assert.Equal(2, _handler.CountFor(TestListing.RobotsUrl));
    }

    [Theory]
    [InlineData("https://evil.example/pt/PIV_1.pdf")]
    [InlineData("http://info.portaldasfinancas.gov.pt/pt/PIV_1.pdf")]
    [InlineData("https://info.portaldasfinancas.gov.pt:8443/pt/PIV_1.pdf")]
    [InlineData("https://user@info.portaldasfinancas.gov.pt/pt/PIV_1.pdf")]
    [InlineData("https://info.portaldasfinancas.gov.pt.evil.example/pt/PIV_1.pdf")]
    public async Task RefusesUrlsOutsideTheHttpsAllowlistWithoutSending(string url)
    {
        using var client = Client();

        await Assert.ThrowsAsync<CrawlPolicyException>(() => Get(client, url));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task RefusesRedirectsToAnotherHost()
    {
        _handler.Always(PdfA, () => FakeHandler.Redirect("https://evil.example/payload.pdf"));
        using var client = Client();

        await Assert.ThrowsAsync<CrawlPolicyException>(() => Get(client, PdfA));

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Host == "evil.example");
    }

    [Fact]
    public async Task RefusesRedirectsToPlainHttp()
    {
        _handler.Always(PdfA, () => FakeHandler.Redirect("http://info.portaldasfinancas.gov.pt/pt/PIV_1.pdf"));
        using var client = Client();

        await Assert.ThrowsAsync<CrawlPolicyException>(() => Get(client, PdfA));

        Assert.DoesNotContain(_handler.Requests, r => r.Url.Scheme == "http");
    }

    [Fact]
    public async Task FollowsRedirectsWithinTheAllowedHostAndChecksRobots()
    {
        _handler.Always(TestListing.RobotsUrl, () => FakeHandler.Text("User-agent: *\nDisallow: /blocked/"));
        _handler.Always(PdfA, () => FakeHandler.Redirect("/pt/moved/PIV_1.pdf"));
        _handler.Always(TestListing.Origin + "/pt/moved/PIV_1.pdf", () => FakeHandler.Bytes([7]));
        _handler.Always(PdfB, () => FakeHandler.Redirect("/blocked/PIV_2.pdf"));
        using var client = Client();

        var moved = await Get(client, PdfA);
        await Assert.ThrowsAsync<RobotsDisallowedException>(() => Get(client, PdfB));

        Assert.Equal([7], moved.Body);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.AbsolutePath.StartsWith("/blocked/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopsAfterTooManyRedirects()
    {
        _handler.Always(PdfA, () => FakeHandler.Redirect(PdfA));
        using var client = Client(new CrawlerOptions { MaxRedirects = 3 });

        await Assert.ThrowsAsync<CrawlPolicyException>(() => Get(client, PdfA));

        Assert.Equal(4, _handler.CountFor(PdfA));
    }

    [Fact]
    public async Task RejectsADeclaredBodyOverTheSizeCap()
    {
        _handler.Always(PdfA, () => FakeHandler.Bytes(new byte[2048]));
        using var client = Client();

        var error = await Assert.ThrowsAsync<CrawlPolicyException>(() => Get(client, PdfA, maxBytes: 1024));

        Assert.Contains("cap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAStreamedBodyOverTheSizeCap()
    {
        _handler.Always(PdfA, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(200_000) });
        using var client = Client();

        await Assert.ThrowsAsync<CrawlPolicyException>(() => Get(client, PdfA, maxBytes: 100_000));
    }

    /// <summary>A body without Content-Length, like a chunked response.</summary>
    private sealed class UnsizedContent(int size) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(new byte[size]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
