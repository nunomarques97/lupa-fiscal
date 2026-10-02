using System.Net;
using System.Security.Cryptography;
using LupaFiscal.Core.Crawling;

namespace LupaFiscal.Core.Embeddings;

public sealed record ModelDownloadSummary(int Downloaded, int AlreadyValid);

/// <summary>
/// Downloads the files of a pinned model from Hugging Face without any account or credential.
/// Only https URLs on huggingface.co and its CDN hosts (*.hf.co) are requested, also after a
/// redirect. Each file is streamed to a temporary name while it is hashed, and only moved to its
/// final name when its size and SHA-256 match the pins; otherwise it is deleted and the download fails.
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly TextWriter _log;

    public ModelDownloader(HttpMessageHandler handler, TextWriter log, TimeSpan? timeout = null)
    {
        _log = log;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CrawlerOptions.DefaultUserAgent);
    }

    /// <summary>No automatic redirects (each hop is checked), no cookies, no credentials.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };

    public static bool IsAllowed(Uri uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (string.Equals(uri.IdnHost, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads every missing or altered file; files already matching their pins are kept.</summary>
    public async Task<ModelDownloadSummary> EnsureAsync(ModelStore store, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        int downloaded = 0, valid = 0;
        foreach (var file in store.Spec.Files)
        {
            var path = store.PathOf(file);
            if (File.Exists(path))
            {
                if (store.IsValid(file))
                {
                    _log.WriteLine($"{file.LocalName}: present, SHA-256 verified.");
                    valid++;
                    continue;
                }
                _log.WriteLine($"{file.LocalName}: present but does not match its pinned SHA-256; downloading it again.");
                File.Delete(path);
            }

            await DownloadAsync(store.Spec.DownloadUri(file), file, path, cancellationToken);
            downloaded++;
        }
        return new ModelDownloadSummary(downloaded, valid);
    }

    private async Task DownloadAsync(Uri uri, ModelFile file, string path, CancellationToken cancellationToken)
    {
        _log.WriteLine($"{file.LocalName}: downloading {file.Size:N0} bytes from {UrlPolicy.Describe(uri)}");
        var partial = path + ".partial";
        try
        {
            using var response = await GetFollowingRedirectsAsync(uri, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ModelIntegrityException($"{file.LocalName}: download failed with HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength is { } declared && declared != file.Size)
            {
                throw new ModelIntegrityException($"{file.LocalName}: server announced {declared} bytes, expected {file.Size}.");
            }

            string hash;
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > file.Size)
                    {
                        throw new ModelIntegrityException($"{file.LocalName}: response is larger than the pinned {file.Size} bytes.");
                    }
                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }

            if (written != file.Size)
            {
                throw new ModelIntegrityException($"{file.LocalName}: received {written} bytes, expected {file.Size}.");
            }
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelIntegrityException(
                    $"{file.LocalName}: SHA-256 {hash} does not match the pinned {file.Sha256}; the file was discarded.");
            }

            File.Move(partial, path, overwrite: true);
            _log.WriteLine($"{file.LocalName}: downloaded, SHA-256 verified.");
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            if (!IsAllowed(current))
            {
                throw new ModelIntegrityException($"Refusing model URL outside https huggingface.co / *.hf.co: {UrlPolicy.Describe(current)}");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new ModelIntegrityException($"Redirect without Location from {UrlPolicy.Describe(current)}.");
            if (hop >= MaxRedirects) throw new ModelIntegrityException($"Too many redirects for {UrlPolicy.Describe(uri)}.");
            current = new Uri(current, location);
        }
    }

    public void Dispose() => _http.Dispose();
}
