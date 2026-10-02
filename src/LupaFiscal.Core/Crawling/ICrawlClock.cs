namespace LupaFiscal.Core.Crawling;

/// <summary>Time source and delay used for request spacing and back-off; replaced by a fake in tests.</summary>
public interface ICrawlClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemCrawlClock : ICrawlClock
{
    public static SystemCrawlClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
}
