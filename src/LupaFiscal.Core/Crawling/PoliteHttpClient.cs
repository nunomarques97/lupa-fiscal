using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace LupaFiscal.Core.Crawling;

public sealed record FetchResponse(HttpStatusCode Status, byte[] Body, string? ContentType, Uri FinalUri)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>Raised when a request still fails after the capped retries (429, 5xx, network errors).</summary>
public sealed class CrawlFetchException(string message) : Exception(message);

/// <summary>
/// The only way the crawler talks to the network. It enforces the https host allowlist (also on
/// redirects, which are followed manually), robots.txt for every host, one request at a time with
/// a minimum gap between requests, capped retries with back-off that honours Retry-After, and a
/// size cap on every response body.
/// </summary>
public sealed class PoliteHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CrawlerOptions _options;
    private readonly ICrawlClock _clock;
    private readonly TextWriter _log;
    private readonly UrlPolicy _policy;
    private readonly Dictionary<string, RobotsRules> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRequestEnd;

    public PoliteHttpClient(HttpMessageHandler handler, CrawlerOptions options, ICrawlClock clock, TextWriter log)
    {
        _options = options;
        _clock = clock;
        _log = log;
        _policy = new UrlPolicy(options.AllowedHosts);
        // The timeout is applied per attempt, so each retry gets its own budget.
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
    }

    /// <summary>Production handler: one connection per server, no automatic redirects, no cookies.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 1,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };

    public UrlPolicy Policy => _policy;

    public int RequestCount { get; private set; }

    /// <summary>Returns the robots.txt rules for the URL's host, fetching them once per client (once per run).</summary>
    public async Task<RobotsRules> GetRobotsAsync(Uri url, CancellationToken cancellationToken)
    {
        _policy.EnsureAllowed(url);
        if (_robots.TryGetValue(url.IdnHost, out var cached)) return cached;

        var robotsUri = new Uri(url, "/robots.txt");
        RobotsRules rules;
        try
        {
            var response = await SendAsync(robotsUri, "text/plain", _options.MaxRobotsBytes, truncateBody: true,
                checkRobots: false, cancellationToken);
            var status = (int)response.Status;
            if (response.IsSuccess)
            {
                rules = RobotsRules.Parse(Encoding.UTF8.GetString(response.Body), CrawlerOptions.ProductToken);
                _log.WriteLine($"robots.txt for {url.IdnHost}: HTTP {status}, {rules}");
            }
            else if (status is >= 400 and < 500 and not 429)
            {
                rules = RobotsRules.AllowAll;
                _log.WriteLine($"robots.txt for {url.IdnHost}: HTTP {status}, unavailable, so no restrictions apply (RFC 9309 2.3.1.3).");
            }
            else
            {
                rules = RobotsRules.DisallowAll;
                _log.WriteLine($"robots.txt for {url.IdnHost}: HTTP {status}, unreachable, so nothing may be crawled (RFC 9309 2.3.1.4).");
            }
        }
        catch (Exception ex) when (ex is CrawlFetchException or CrawlPolicyException)
        {
            rules = RobotsRules.DisallowAll;
            _log.WriteLine($"robots.txt for {url.IdnHost}: {ex.Message}; nothing may be crawled (RFC 9309 2.3.1.4).");
        }

        _robots[url.IdnHost] = rules;
        return rules;
    }

    /// <summary>
    /// GETs a URL under every crawl rule. A non-success final status is returned with an empty
    /// body. Policy violations throw <see cref="CrawlPolicyException"/> before anything is sent;
    /// exhausted retries throw <see cref="CrawlFetchException"/>.
    /// </summary>
    public Task<FetchResponse> GetAsync(Uri url, string accept, long maxBytes, CancellationToken cancellationToken) =>
        SendAsync(url, accept, maxBytes, truncateBody: false, checkRobots: true, cancellationToken);

    private async Task<FetchResponse> SendAsync(Uri url, string accept, long maxBytes, bool truncateBody,
        bool checkRobots, CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            _policy.EnsureAllowed(current);
            if (checkRobots)
            {
                var robots = await GetRobotsAsync(current, cancellationToken);
                if (!robots.IsAllowed(current.PathAndQuery))
                {
                    throw new RobotsDisallowedException(current.AbsolutePath);
                }
            }

            var (result, location) = await SendWithRetriesAsync(current, accept, maxBytes, truncateBody, cancellationToken);
            if (location is null) return result;

            if (redirects >= _options.MaxRedirects)
            {
                throw new CrawlPolicyException($"Too many redirects starting at {UrlPolicy.Describe(url)}");
            }
            var next = new Uri(current, location);
            if (!_policy.IsAllowed(next))
            {
                throw new CrawlPolicyException(
                    $"Refusing redirect from {UrlPolicy.Describe(current)} to {UrlPolicy.Describe(next)} (outside the https host allowlist)");
            }
            _log.WriteLine($"Redirect {UrlPolicy.Describe(current)} -> {UrlPolicy.Describe(next)}");
            current = next;
        }
    }

    private async Task<(FetchResponse Result, Uri? Location)> SendWithRetriesAsync(Uri url, string accept,
        long maxBytes, bool truncateBody, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string failure;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await WaitForSlotAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                try
                {
                    RequestCount++;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    var status = (int)response.StatusCode;
                    _log.WriteLine($"GET {UrlPolicy.Describe(url)} -> {status}");

                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        var location = response.Headers.Location
                            ?? throw new CrawlPolicyException($"Redirect without Location from {UrlPolicy.Describe(url)}");
                        return (new FetchResponse(response.StatusCode, [], null, url), location);
                    }

                    if (status == 429 || status >= 500)
                    {
                        retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                        failure = $"HTTP {status}";
                    }
                    else
                    {
                        var body = status is >= 200 and < 300
                            ? await ReadBodyAsync(response, maxBytes, truncateBody, url, timeout.Token)
                            : [];
                        return (new FetchResponse(response.StatusCode, body, response.Content.Headers.ContentType?.MediaType, url), null);
                    }
                }
                catch (HttpRequestException ex)
                {
                    failure = $"network error ({ex.HttpRequestError})";
                }
                catch (IOException)
                {
                    failure = "network error (I/O)";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failure = $"timeout after {_options.RequestTimeout.TotalSeconds:0} s";
                }
            }
            finally
            {
                _lastRequestEnd = _clock.UtcNow;
                _gate.Release();
            }

            if (attempt >= _options.MaxRetries)
            {
                throw new CrawlFetchException($"{failure} for {UrlPolicy.Describe(url)} after {attempt + 1} attempt(s)");
            }

            var backoff = Backoff(attempt);
            if (retryAfter is { } wait)
            {
                if (wait > _options.MaxRetryAfter)
                {
                    throw new CrawlFetchException(
                        $"{failure} for {UrlPolicy.Describe(url)}: Retry-After of {wait.TotalSeconds:0} s exceeds the {_options.MaxRetryAfter.TotalSeconds:0} s limit");
                }
                // Retry-After is a lower bound; never retry sooner than the server asked.
                if (wait > backoff) backoff = wait;
            }
            _log.WriteLine($"{failure} for {UrlPolicy.Describe(url)}; retry {attempt + 1}/{_options.MaxRetries} in {backoff.TotalSeconds:0.#} s");
            await _clock.DelayAsync(backoff, cancellationToken);
        }
    }

    private async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        if (_lastRequestEnd is not { } last) return;
        var wait = last + _options.RequestInterval - _clock.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await _clock.DelayAsync(wait, cancellationToken);
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var ticks = _options.InitialBackoff.Ticks * Math.Pow(2, attempt);
        return ticks >= _options.MaxBackoff.Ticks ? _options.MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }

    private TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        var wait = header.Delta ?? (header.Date is { } date ? date - _clock.UtcNow : null);
        if (wait is null) return null;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, long maxBytes, bool truncate,
        Uri url, CancellationToken cancellationToken)
    {
        if (!truncate && response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new CrawlPolicyException($"Response of {declared} bytes exceeds the {maxBytes} byte cap: {UrlPolicy.Describe(url)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
            {
                if (!truncate)
                {
                    throw new CrawlPolicyException($"Response exceeds the {maxBytes} byte cap: {UrlPolicy.Describe(url)}");
                }
                buffer.Write(chunk, 0, (int)(maxBytes - buffer.Length));
                break;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
