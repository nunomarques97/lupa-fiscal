using LupaFiscal.Core;
using LupaFiscal.Core.Embeddings;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.FileProviders;

namespace LupaFiscal.Api;

/// <summary>
/// The local search API on http://localhost:4401: GET /api/search, /api/facets and /api/health,
/// plus the built Angular app (web/dist) with an SPA fallback when it exists. Options:
/// --data-dir (else LUPAFISCAL_DATA_DIR, else data/ at the repository root) and --web-root.
/// </summary>
public sealed class Program
{
    public const int Port = 4401;
    public const string IndexFileName = "lupa-fiscal.db";

    public static void Main(string[] args)
    {
        var app = Build(args);
        // Load the model, vectors and facets before accepting requests, so a missing index fails at startup.
        var index = app.Services.GetRequiredService<SearchIndex>();
        app.Logger.LogInformation("Index loaded: {Rulings} rulings, {Chunks} chunks.", index.Rulings, index.Chunks);
        app.Run();
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Loopback only (127.0.0.1 and ::1), whatever the URL settings say.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(Port));
        // Per-request framework logs would echo every question; keep warnings and errors only.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
            // Non-ASCII letters stay readable; <, >, & and quotes are still escaped.
            options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All));

        builder.Services.AddSingleton<IEmbedder>(services =>
            E5Embedder.Load(new ModelStore(Path.Combine(DataDir(services), "models"), ModelSpec.MultilingualE5Small)));
        builder.Services.AddSingleton(services =>
            SearchIndex.Open(Path.Combine(DataDir(services), IndexFileName), services.GetRequiredService<IEmbedder>()));

        var app = builder.Build();

        app.Use((context, next) =>
        {
            // Set when the response starts, so error responses written after a Clear() carry them too.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            return next(context);
        });

        // Unhandled errors become a bare 500 problem: no exception message, stack trace or path.
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context => Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                title: "An unexpected error occurred.").ExecuteAsync(context),
        });

        var api = app.MapGroup("/api");
        api.MapGet("/search", (HttpRequest request, SearchIndex index) =>
        {
            var query = SearchQuery.Parse(request.Query, out var errors);
            return query is null ? Results.ValidationProblem(errors) : Results.Ok(index.Search(query));
        });
        api.MapGet("/facets", (SearchIndex index) => index.Facets);
        api.MapGet("/health", (SearchIndex index) => new HealthResponse("ok", index.Rulings, index.Chunks));
        // Unknown API paths are a JSON 404, never the SPA page.
        app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found."));

        if (WebRoot.Find(app.Configuration["web-root"], DataDirectory.RepositoryRoot()) is { } webRoot)
        {
            var files = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
            app.Logger.LogInformation("Serving the web app from {WebRoot}.", webRoot);
        }

        return app;
    }

    private static string DataDir(IServiceProvider services) =>
        DataDirectory.Resolve(services.GetRequiredService<IConfiguration>()["data-dir"]);
}
