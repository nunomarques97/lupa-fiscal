using System.Globalization;
using System.Text;

namespace LupaFiscal.Cli;

/// <summary>Writes each log line, prefixed with a UTC timestamp, to every target writer.</summary>
internal sealed class TimestampedWriter(params TextWriter[] targets) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        var line = $"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)} {value}";
        foreach (var target in targets)
        {
            target.WriteLine(line);
            target.Flush();
        }
    }

    public override void Write(char value)
    {
        foreach (var target in targets) target.Write(value);
    }
}
