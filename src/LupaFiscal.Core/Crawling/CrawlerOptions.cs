namespace LupaFiscal.Core.Crawling;

public sealed class CrawlerOptions
{
    public const string ProductToken = "lupafiscal";

    public const string DefaultUserAgent =
        "LupaFiscal/0.2 (+https://github.com/nunomarques97/lupa-fiscal; open-source research crawler)";

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    public const int MaxAllowedRetries = 10;

    private TimeSpan _requestInterval = MinimumInterval;
    private int _maxRetries = 4;

    public string UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>Hosts the crawler may contact (https only). Gate 0 allowlists a single host.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = ["info.portaldasfinancas.gov.pt"];

    /// <summary>Minimum gap between the end of one request and the start of the next. Never below 1 s.</summary>
    public TimeSpan RequestInterval
    {
        get => _requestInterval;
        init
        {
            if (value < MinimumInterval)
            {
                throw new ArgumentOutOfRangeException(nameof(RequestInterval), value,
                    $"The request interval cannot be lower than {MinimumInterval.TotalSeconds:0} s.");
            }
            _requestInterval = value;
        }
    }

    /// <summary>Retries after a 429, 5xx or network error, on top of the first attempt.</summary>
    public int MaxRetries
    {
        get => _maxRetries;
        init
        {
            if (value is < 0 or > MaxAllowedRetries)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxRetries), value,
                    $"Retries must be between 0 and {MaxAllowedRetries}.");
            }
            _maxRetries = value;
        }
    }

    /// <summary>First back-off delay; doubles on each retry up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A Retry-After longer than this makes the request fail instead of waiting.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxRedirects { get; init; } = 5;

    public long MaxPdfBytes { get; init; } = 25 * 1024 * 1024;

    public long MaxListingBytes { get; init; } = 20 * 1024 * 1024;

    public long MaxRobotsBytes { get; init; } = 512 * 1024;
}
