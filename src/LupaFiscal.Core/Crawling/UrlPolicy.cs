namespace LupaFiscal.Core.Crawling;

/// <summary>
/// Decides which URLs the crawler may request: https only, on an explicit host allowlist.
/// </summary>
public sealed class UrlPolicy
{
    private readonly HashSet<string> _allowedHosts;

    public UrlPolicy(IEnumerable<string> allowedHosts)
    {
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        if (_allowedHosts.Count == 0)
        {
            throw new ArgumentException("At least one allowed host is required.", nameof(allowedHosts));
        }
    }

    public IReadOnlyCollection<string> AllowedHosts => _allowedHosts;

    public bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && _allowedHosts.Contains(uri.IdnHost);

    public void EnsureAllowed(Uri uri)
    {
        if (!IsAllowed(uri))
        {
            throw new CrawlPolicyException($"Refusing URL outside the https host allowlist: {Describe(uri)}");
        }
    }

    /// <summary>Describes a URL for logs without echoing arbitrary remote strings in full.</summary>
    internal static string Describe(Uri? uri)
    {
        if (uri is null) return "(none)";
        var text = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString;
        return text.Length <= 200 ? text : text[..200] + "...";
    }
}

/// <summary>Raised when a request would break the crawl policy (host allowlist, robots.txt, size cap).</summary>
public class CrawlPolicyException(string message) : Exception(message);

public sealed class RobotsDisallowedException(string path)
    : CrawlPolicyException($"Disallowed by robots.txt: {path}");
