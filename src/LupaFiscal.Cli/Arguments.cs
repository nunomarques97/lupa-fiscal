using System.Globalization;

namespace LupaFiscal.Cli;

/// <summary>Parses "--name value" options and "--flag" switches.</summary>
internal sealed class Arguments
{
    private static readonly HashSet<string> Flags = ["retry-failed", "use-cached-listing", "list-failed", "record", "freeze", "all"];

    private readonly Dictionary<string, string?> _values;

    private Arguments(Dictionary<string, string?> values) => _values = values;

    public static Arguments Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }
            var name = arg[2..];
            if (values.ContainsKey(name))
            {
                throw new ArgumentException($"Option --{name} given twice.");
            }
            if (Flags.Contains(name))
            {
                values[name] = null;
                continue;
            }
            if (!e.MoveNext() || e.Current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option --{name} needs a value.");
            }
            values[name] = e.Current;
        }
        return new Arguments(values);
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public int? GetInt(string name) => Get(name) is { } value
        ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"Option --{name} must be an integer.")
        : null;

    public double? GetDouble(string name) => Get(name) is { } value
        ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw new ArgumentException($"Option --{name} must be a number.")
        : null;

    public void EnsureOnly(params string[] allowed)
    {
        var unknown = _values.Keys.Except(allowed).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}.");
        }
    }
}
