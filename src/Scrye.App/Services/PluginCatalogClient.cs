using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Scrye.Core.Plugins;

namespace Scrye.App.Services;

/// <summary>
/// The network half of the plugin catalogue: reads the index and fetches plugin files over
/// HTTPS. Everything that decides anything - parsing, what fits, checksums, the swap into
/// place - is in Core (<see cref="CatalogIndex"/>, <see cref="CatalogInstaller"/>) and tested
/// there; this only moves bytes, with a timeout and a size cap so a bad server cannot hang the
/// manager or fill the disk.
/// </summary>
public static class PluginCatalogClient
{
    // One client for the app's lifetime: HttpClient is built to be shared, and a new one per
    // request exhausts sockets.
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The index may be large but never this large.</summary>
    private const long MaxIndexBytes = 4L * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Scrye-plugin-catalogue");
        return c;
    }

    /// <summary>Read and parse the index. Throws with a sentence fit for the manager on failure.</summary>
    public static async Task<CatalogIndex> LoadAsync(string url, Action<string>? report, CancellationToken ct = default)
    {
        byte[] bytes;
        try { bytes = await GetAsync(url, MaxIndexBytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException("the plugin catalogue did not answer in time"); }
        catch (HttpRequestException ex)
        { throw new IOException("could not reach the plugin catalogue: " + ex.Message, ex); }
        catch (FileNotFoundException)
        { throw new IOException("there is no plugin catalogue at " + url + " yet"); }
        return CatalogIndex.Parse(System.Text.Encoding.UTF8.GetString(bytes), report);
    }

    /// <summary>A plugin file, for <see cref="CatalogInstaller.InstallAsync"/>.</summary>
    public static Task<byte[]> FetchAsync(string url, CancellationToken ct) =>
        GetAsync(url, CatalogIndex.MaxPluginBytes, ct);

    private static async Task<byte[]> GetAsync(string url, long cap, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new IOException("the plugin catalogue only fetches over https: " + url);
        using HttpResponseMessage resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                                                   .ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException("not found: " + url);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url}");
        if (resp.Content.Headers.ContentLength is long len && len > cap)
            throw new IOException($"{url} is larger than allowed");
        await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > cap) throw new IOException($"{url} is larger than allowed");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
