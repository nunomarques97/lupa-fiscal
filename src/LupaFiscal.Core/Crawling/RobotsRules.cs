using System.Text;

namespace LupaFiscal.Core.Crawling;

/// <summary>
/// Minimal RFC 9309 robots.txt evaluation for one product token: the groups naming the token
/// (or "*" when none does) apply, the longest matching rule wins, Allow wins ties,
/// and "*" and "$" wildcards are supported.
/// </summary>
public sealed class RobotsRules
{
    private readonly IReadOnlyList<Rule> _rules;
    private readonly bool _disallowAll;

    private RobotsRules(IReadOnlyList<Rule> rules, bool disallowAll)
    {
        _rules = rules;
        _disallowAll = disallowAll;
    }

    /// <summary>No robots.txt available (4xx): everything may be crawled (RFC 9309 2.3.1.3).</summary>
    public static RobotsRules AllowAll { get; } = new([], disallowAll: false);

    /// <summary>robots.txt unreachable (5xx, network error): nothing may be crawled (RFC 9309 2.3.1.4).</summary>
    public static RobotsRules DisallowAll { get; } = new([], disallowAll: true);

    public bool BlocksEverything => _disallowAll;

    public IReadOnlyList<(bool Allow, string Pattern)> Rules => _rules.Select(r => (r.Allow, r.Pattern)).ToList();

    public static RobotsRules Parse(string content, string productToken)
    {
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;
        // A leading UTF-8 byte-order mark would otherwise hide the first User-agent line.
        foreach (var raw in content.TrimStart('\uFEFF').Split('\n'))
        {
            var hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent)
                {
                    current = new Group();
                    groups.Add(current);
                }
                current!.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else
            {
                lastWasAgent = false;
                if (current is not null && (key == "allow" || key == "disallow"))
                {
                    current.Rules.Add(new Rule(key == "allow", value));
                }
            }
        }

        var token = productToken.ToLowerInvariant();
        var own = groups.Where(g => g.Agents.Contains(token)).ToList();
        var chosen = own.Count > 0 ? own : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsRules(chosen.SelectMany(g => g.Rules).ToList(), disallowAll: false);
    }

    /// <summary>Checks a URL path (with query), as sent on the wire.</summary>
    public bool IsAllowed(string pathAndQuery)
    {
        if (_disallowAll) return false;
        var path = Normalize(pathAndQuery);
        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (rule.Pattern.Length == 0 || !Matches(Normalize(rule.Pattern), path)) continue;
            if (best is null
                || rule.Pattern.Length > best.Pattern.Length
                || (rule.Pattern.Length == best.Pattern.Length && rule.Allow))
            {
                best = rule;
            }
        }
        return best?.Allow ?? true;
    }

    // Percent-decoding both sides lets "/a b" and "/a%20b" compare equal.
    private static string Normalize(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var body = anchored ? pattern[..^1] : pattern;
        // Iterative wildcard match: '*' matches any sequence; without '$' the pattern is a prefix.
        return MatchFrom(body, 0, path, 0, anchored);
    }

    private static bool MatchFrom(string pattern, int p, string path, int s, bool anchored)
    {
        int starP = -1, starS = -1;
        while (true)
        {
            if (p == pattern.Length)
            {
                if (!anchored || s == path.Length) return true;
            }
            else if (pattern[p] == '*')
            {
                starP = p++;
                starS = s;
                continue;
            }
            else if (s < path.Length && pattern[p] == path[s])
            {
                p++;
                s++;
                continue;
            }

            if (starP < 0 || starS >= path.Length) return false;
            p = starP + 1;
            s = ++starS;
        }
    }

    private sealed record Rule(bool Allow, string Pattern);

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<Rule> Rules { get; } = [];
    }

    public override string ToString()
    {
        if (_disallowAll) return "disallow all";
        if (_rules.Count == 0) return "no rules (allow all)";
        var builder = new StringBuilder();
        foreach (var rule in _rules)
        {
            builder.Append(rule.Allow ? "Allow: " : "Disallow: ").Append(rule.Pattern).Append("; ");
        }
        return builder.ToString().TrimEnd(' ', ';');
    }
}
