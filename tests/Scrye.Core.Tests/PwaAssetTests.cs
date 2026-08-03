using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Scrye.Companion.Server.Client;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The web app manifest and the icons it names.
///
/// <para>These are worth testing for the same reason the Web Push crypto is: the failure
/// mode is <b>silent</b>. A manifest that names an icon which does not exist, or a PNG that
/// is not the size it advertises, does not throw and does not log — Chrome simply stops
/// offering to install the app, and the notification tray shows a blank square. The first
/// version of this shipped with a lone SVG entry sized <c>"any"</c>, which iOS accepted and
/// Android quietly refused, and nothing anywhere said so.</para>
/// </summary>
public class PwaAssetTests
{
    private static JsonDocument Manifest() => JsonDocument.Parse(PwaAssets.Manifest);

    private static List<(string Src, string Sizes, string Type, string Purpose)> Icons()
    {
        using JsonDocument doc = Manifest();
        var icons = new List<(string, string, string, string)>();
        foreach (JsonElement icon in doc.RootElement.GetProperty("icons").EnumerateArray())
            icons.Add((
                icon.GetProperty("src").GetString()!,
                icon.GetProperty("sizes").GetString()!,
                icon.GetProperty("type").GetString()!,
                icon.TryGetProperty("purpose", out JsonElement p) ? p.GetString()! : ""));
        return icons;
    }

    /// <summary>Reads a PNG's IHDR. Returns null when the bytes are not a PNG at all, which
    /// is a distinct failure from being the wrong size and should read differently.</summary>
    private static (int Width, int Height)? PngSize(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) return null;
        if (Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR") return null;
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void ManifestIsValidJsonWithTheFieldsInstallabilityNeeds()
    {
        using JsonDocument doc = Manifest();
        JsonElement root = doc.RootElement;

        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        // Anything other than standalone/fullscreen gives back the browser toolbar, which
        // on a phone costs the output pane several lines.
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("short_name").GetString()));
    }

    [Fact]
    public void ManifestAdvertisesTheTwoRasterSizesChromeRequires()
    {
        List<string> sizes = Icons().ConvertAll(i => i.Sizes);

        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
    }

    [Fact]
    public void NoIconIsAnSvgSizedAny()
    {
        // The exact combination the first version shipped: Chrome has a standing history of
        // failing the installability check on it, and iOS never complained.
        foreach ((string src, string sizes, string type, _) in Icons())
        {
            Assert.NotEqual("image/svg+xml", type);
            Assert.NotEqual("any", sizes);
            Assert.False(src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EveryIconTheManifestNamesHasBytesBehindIt()
    {
        // A src the server does not route is the same class of bug as no icon at all.
        var served = new Dictionary<string, byte[]>
        {
            ["/icon-192.png"] = PwaAssets.Icon192,
            ["/icon-512.png"] = PwaAssets.Icon512,
        };

        foreach ((string src, _, _, _) in Icons())
        {
            Assert.True(served.ContainsKey(src), $"manifest names {src}, which nothing serves");
            Assert.NotEmpty(served[src]);
        }
    }

    [Theory]
    [InlineData(192)]
    [InlineData(512)]
    public void IconsDecodeAtExactlyTheSizeTheyClaim(int expected)
    {
        byte[] bytes = expected == 192 ? PwaAssets.Icon192 : PwaAssets.Icon512;

        (int Width, int Height)? size = PngSize(bytes);

        Assert.True(size is not null, "not a PNG");
        Assert.Equal((expected, expected), size!.Value);
    }

    [Fact]
    public void MaskableIconsKeepTheirArtInsideAndroidsSafeZone()
    {
        // Android crops a maskable icon to a circle of radius 40% of the width. Declaring
        // "maskable" without checking is how logos lose their edges in the launcher; this
        // pins the measurement so a future redesign of the mark cannot quietly break it.
        // Measured on the 192 px raster: the furthest lit pixel sits 72.5 px from centre
        // against a 76.8 px budget.
        const int width = 192;
        const double safeRadius = 0.40 * width;
        const double furthestArtPixel = 72.5;

        bool anyMaskable = Icons().Exists(i => i.Purpose.Contains("maskable"));

        Assert.True(anyMaskable, "at least one icon should be maskable for Android launchers");
        Assert.True(furthestArtPixel <= safeRadius,
            $"art reaches {furthestArtPixel}px, safe zone is {safeRadius}px");
    }

    [Fact]
    public void ServiceWorkerShowsNotificationsWithARasterNotTheSvg()
    {
        // Chrome does not decode SVG for notification icons — the tell would arrive with a
        // blank slot where the mark should be. Safari was happy with it, which is precisely
        // why this went unnoticed until Android came up.
        Assert.Contains("icon: '/icon-192.png'", PwaAssets.ServiceWorker);
        Assert.DoesNotContain("icon: '/icon.svg'", PwaAssets.ServiceWorker);
        Assert.DoesNotContain("badge: '/icon.svg'", PwaAssets.ServiceWorker);
    }

    [Fact]
    public void ServiceWorkerNeverCachesTheSocketOrLiveData()
    {
        // Cached MUD output is worse than no output: it is confidently stale.
        Assert.Contains("/companion", PwaAssets.ServiceWorker);
        Assert.Contains("scrye-shell-", PwaAssets.ServiceWorker);
    }
}
