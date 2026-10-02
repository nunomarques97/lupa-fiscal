using System.Net;
using LupaFiscal.Core.Crawling;

namespace LupaFiscal.Tests.Support;

/// <summary>Clock whose delays advance time instantly; records every delay.</summary>
internal sealed class FakeClock : ICrawlClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan by) => UtcNow += by;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal sealed record RecordedRequest(Uri Url, DateTimeOffset Start, string? UserAgent, string? Accept);

/// <summary>
/// HttpMessageHandler serving scripted responses per absolute URL. Unscripted URLs return 404.
/// Each request advances the fake clock by a simulated transfer time.
/// </summary>
internal sealed class FakeHandler(FakeClock clock) : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _scripted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<HttpResponseMessage>> _always = new(StringComparer.Ordinal);

    public List<RecordedRequest> Requests { get; } = [];

    public TimeSpan TransferTime { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Called before each response; lets a test cancel the run mid-crawl.</summary>
    public Action<Uri>? OnRequest { get; set; }

    public FakeHandler Always(string url, Func<HttpResponseMessage> response)
    {
        _always[url] = response;
        return this;
    }

    public FakeHandler Then(string url, Func<HttpResponseMessage> response)
    {
        if (!_scripted.TryGetValue(url, out var queue)) _scripted[url] = queue = new Queue<Func<HttpResponseMessage>>();
        queue.Enqueue(response);
        return this;
    }

    public int CountFor(string url) => Requests.Count(r => r.Url.AbsoluteUri == url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        Requests.Add(new RecordedRequest(url, clock.UtcNow,
            request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null,
            request.Headers.Accept.ToString()));
        OnRequest?.Invoke(url);
        clock.Advance(TransferTime);

        var key = url.AbsoluteUri;
        HttpResponseMessage response;
        if (_scripted.TryGetValue(key, out var queue) && queue.Count > 0) response = queue.Dequeue()();
        else if (_always.TryGetValue(key, out var always)) response = always();
        else response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        if (retryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return response;
    }

    public static HttpResponseMessage Text(string body, string mediaType = "text/plain") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Bytes(byte[] body, string mediaType = "application/pdf")
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }, Content = new StringContent("") };
}
