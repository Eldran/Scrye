using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Scrye.Core.Updates;

namespace Scrye.App.Services;

/// <summary>
/// Asks whether a newer Scrye has been released: reads <c>catalog/release.json</c> from the
/// repo's main branch (<see cref="ReleaseInfo.DefaultUrl"/>). It only reads - nothing is
/// downloaded or installed; the notice links to the release page. One small file per start
/// (and per press of the version button), so it never meets GitHub's API rate limit, which a
/// call to the releases API would.
/// </summary>
public static class UpdateChecker
{
    private const long MaxBytes = 64 * 1024;

    /// <summary>The newest release. Throws with a sentence fit for the sidebar on failure.</summary>
    public static async Task<ReleaseInfo> LatestAsync(CancellationToken ct = default)
    {
        byte[] bytes;
        try { bytes = await PluginCatalogClient.GetBytesAsync(ReleaseInfo.DefaultUrl, MaxBytes, ct).ConfigureAwait(false); }
        catch (FileNotFoundException) { throw new IOException("no release has been published for the update check yet"); }
        catch (HttpRequestException ex) { throw new IOException("could not reach GitHub: " + ex.Message, ex); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("GitHub did not answer in time"); }
        return ReleaseInfo.Parse(Encoding.UTF8.GetString(bytes));
    }
}
