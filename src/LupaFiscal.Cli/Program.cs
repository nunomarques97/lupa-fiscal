using LupaFiscal.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C stops the crawl cleanly and saves progress; a second one ends the process.
    if (cancellation.IsCancellationRequested) return;
    e.Cancel = true;
    cancellation.Cancel();
};
Console.OutputEncoding = System.Text.Encoding.UTF8;
return await CliApp.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
