using System.Text;
using LupaFiscal.Core.Corpus;

namespace LupaFiscal.Cli;

/// <summary>
/// Crawl log: every line goes to stdout with a UTC timestamp and, once a tax is selected with
/// <see cref="Switch"/>, also to that tax's data/corpus/&lt;tax&gt;/crawl.log. One instance serves a
/// whole multi-tax run, so the shared HTTP client logs into the file of the tax being crawled.
/// </summary>
internal sealed class CrawlLog(TextWriter stdout) : TextWriter
{
    private StreamWriter? _file;
    private TextWriter _current = new TimestampedWriter(stdout);

    public override Encoding Encoding => Encoding.UTF8;

    public TextWriter Switch(CorpusStore store)
    {
        Directory.CreateDirectory(store.TaxDirectory);
        _file?.Dispose();
        _file = new StreamWriter(Path.Combine(store.TaxDirectory, "crawl.log"), append: true, Encoding.UTF8);
        _current = new TimestampedWriter(stdout, _file);
        return this;
    }

    public override void WriteLine(string? value) => _current.WriteLine(value);

    public override void Write(char value) => _current.Write(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file?.Dispose();
        base.Dispose(disposing);
    }
}
