using System.Text;
using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The plugin catalogue: versions ordered as authors mean them, the index built from a
/// release's files and read back defensively, where a catalogued plugin stands against what
/// is installed, the all-or-nothing installer, and newest-wins discovery across the bundled
/// and user folders. No network: downloads are a dictionary.
/// </summary>
public sealed class PluginCatalogIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrye-catalog-" + Guid.NewGuid().ToString("N"));

    public PluginCatalogIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Manifest(string id, string version, string? requires = null) =>
        "{ \"id\": \"" + id + "\", \"name\": \"" + id.ToUpperInvariant() + "\", \"version\": \"" + version + "\""
        + (requires is null ? "" : ", \"requires\": { \"scryeApi\": \"" + requires + "\" }") + " }";

    private static CatalogIndex OneRelease(string version = "1.2.0", string main = "print('hi')") =>
        CatalogIndex.Build(new[]
        {
            ("3s-demo/plugin.json", B(Manifest("3s-demo", version))),
            ("3s-demo/main.lua", B(main)),
            ("3s-demo/tiles/tower.png", new byte[] { 1, 2, 3 }),
            ("3s-demo/demo_test.lua", B("-- harness")),
        }, "v9.9.9");

    // a fake network: the index's URL for each file -> its bytes
    private static CatalogInstaller.Fetch Serve(CatalogIndex idx, CatalogEntry e,
                                               Func<CatalogFile, byte[]>? tamper = null) =>
        (url, ct) =>
        {
            CatalogFile f = e.Files.Single(x => idx.FileUrl(e, x) == url);
            byte[] real = f.Path switch
            {
                "plugin.json" => B(Manifest(e.Id, e.Version)),
                "main.lua" => B("print('hi')"),
                "tiles/tower.png" => new byte[] { 1, 2, 3 },
                _ => throw new FileNotFoundException(url),
            };
            return Task.FromResult(tamper?.Invoke(f) ?? real);
        };

    private string MakePlugin(string root, string folder, string id, string version)
    {
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), Manifest(id, version));
        File.WriteAllText(Path.Combine(dir, "main.lua"), "-- " + version);
        return dir;
    }

    // ---- versions ---------------------------------------------------------------

    [Theory]
    [InlineData("1.10.0", "1.9.3", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("2.0.0-beta", "2.0.0", -1)]
    [InlineData("v1.3.0", "1.2.9", 1)]
    [InlineData("1.0.0+build7", "1.0.0", 0)]
    [InlineData("0.9", "1.0", -1)]
    public void VersionsCompareAsNumbers(string a, string b, int expected)
    {
        Assert.Equal(expected, PluginVersion.Compare(a, b));
        Assert.Equal(-expected, PluginVersion.Compare(b, a));
    }

    // ---- building and reading the index ---------------------------------------

    [Fact]
    public void BuildHashesEveryShippedFileAndLeavesTheHarnessOut()
    {
        CatalogIndex idx = OneRelease();
        CatalogEntry e = Assert.Single(idx.Plugins);

        Assert.Equal("3s-demo", e.Id);
        Assert.Equal("1.2.0", e.Version);
        Assert.Equal("3s-demo", e.Folder);
        Assert.Equal(new[] { "main.lua", "plugin.json", "tiles/tower.png" }, e.Files.Select(f => f.Path).ToArray());
        Assert.Equal(CatalogIndex.Sha256Hex(new byte[] { 1, 2, 3 }), e.Files[2].Sha256);
        Assert.Equal(3, e.Files[2].Size);
        Assert.Equal("https://raw.githubusercontent.com/Eldran/Scrye/v9.9.9/src/Scrye.App/plugins/", idx.Base);
        Assert.Equal("https://raw.githubusercontent.com/Eldran/Scrye/v9.9.9/src/Scrye.App/plugins/3s-demo/tiles/tower.png",
                     idx.FileUrl(e, e.Files[2]));
    }

    [Fact]
    public void AnIndexRoundTripsThroughItsJson()
    {
        CatalogIndex idx = OneRelease();
        CatalogIndex back = CatalogIndex.Parse(idx.ToJson());

        Assert.Equal("v9.9.9", back.Ref);
        CatalogEntry e = Assert.Single(back.Plugins);
        Assert.Equal(idx.Plugins[0].Files.Select(f => f.Sha256), e.Files.Select(f => f.Sha256));
        Assert.Contains("\"sha256\"", idx.ToJson());   // camelCase on disk
    }

    [Fact]
    public void AFolderWithoutAnIdIsLeftOutOfTheBuild()
    {
        var said = new List<string>();
        CatalogIndex idx = CatalogIndex.Build(new[]
        {
            ("broken/plugin.json", B("{ \"name\": \"no id\" }")),
            ("broken/main.lua", B("")),
            ("good/plugin.json", B("\uFEFF" + Manifest("good", "1.0.0"))),   // a BOM does not stop it
        }, "v1", report: said.Add);

        Assert.Equal("good", Assert.Single(idx.Plugins).Id);
        Assert.Contains(said, s => s.StartsWith("broken"));
    }

    [Theory]
    [InlineData("../evil.lua")]
    [InlineData("tiles/../../evil.lua")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("tiles\\evil.lua")]
    [InlineData("")]
    public void AnEntryWhoseFileClimbsOutIsDropped(string path)
    {
        CatalogIndex idx = OneRelease();
        CatalogEntry e = idx.Plugins[0];
        var files = e.Files.ToList();
        files.Add(new CatalogFile { Path = path, Sha256 = new string('a', 64), Size = 1 });
        string json = (idx with { Plugins = new[] { e with { Files = files.ToArray() } } }).ToJson();

        var said = new List<string>();
        Assert.Empty(CatalogIndex.Parse(json, said.Add).Plugins);
        Assert.Single(said);
    }

    [Fact]
    public void ABadEntryDoesNotHideTheGoodOnes()
    {
        CatalogIndex idx = OneRelease();
        CatalogEntry good = idx.Plugins[0];
        CatalogEntry bad = good with { Id = "bad", Files = new[] { good.Files[1] with { Sha256 = "nothex" } } };
        CatalogIndex back = CatalogIndex.Parse((idx with { Plugins = new[] { bad, good } }).ToJson());

        Assert.Equal("3s-demo", Assert.Single(back.Plugins).Id);
    }

    [Fact]
    public void AnUnknownFormatOrAPlainHttpBaseIsRefused()
    {
        CatalogIndex idx = OneRelease();
        Assert.Throws<InvalidDataException>(() => CatalogIndex.Parse((idx with { Format = 2 }).ToJson()));
        Assert.Throws<InvalidDataException>(() => CatalogIndex.Parse((idx with { Base = "http://example.com/" }).ToJson()));
        Assert.Throws<InvalidDataException>(() => CatalogIndex.Parse("<html>404</html>"));
    }

    // ---- status ---------------------------------------------------------------

    [Fact]
    public void StatusComparesTheCatalogueWithWhatWouldLoad()
    {
        CatalogEntry e = OneRelease("1.2.0").Plugins[0];
        PluginDescriptor At(string v) => new(new PluginManifest { Id = "3s-demo", Version = v }, _root);

        Assert.Equal(CatalogStatus.NotInstalled, CatalogIndex.StatusOf(e, null));
        Assert.Equal(CatalogStatus.UpToDate, CatalogIndex.StatusOf(e, At("1.2.0")));
        Assert.Equal(CatalogStatus.UpdateAvailable, CatalogIndex.StatusOf(e, At("1.1.9")));
        Assert.Equal(CatalogStatus.NewerInstalled, CatalogIndex.StatusOf(e, At("1.3.0")));

        CatalogEntry future = e with { Requires = ">=99.0" };
        Assert.Equal(CatalogStatus.Incompatible, CatalogIndex.StatusOf(future, null));
        Assert.Equal(CatalogStatus.Incompatible, CatalogIndex.StatusOf(future, At("1.0.0")));
    }

    // ---- installing -------------------------------------------------------------

    [Fact]
    public async Task InstallWritesEveryFileIntoTheUserFolder()
    {
        CatalogIndex idx = OneRelease();
        CatalogEntry e = idx.Plugins[0];
        string target = CatalogInstaller.TargetFolder(e, null, _root);

        await CatalogInstaller.InstallAsync(idx, e, target, Serve(idx, e));

        Assert.Equal(Path.Combine(_root, "3s-demo"), target);
        Assert.Equal("print('hi')", File.ReadAllText(Path.Combine(target, "main.lua")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "tiles", "tower.png")));
        Assert.False(File.Exists(Path.Combine(target, "demo_test.lua")));
        Assert.Equal("1.2.0", Assert.Single(PluginCatalog.Discover(_root)).Manifest.Version);
        Assert.Empty(Directory.GetDirectories(_root, ".*"));        // no staging left behind
    }

    [Fact]
    public async Task AnUpdateReplacesTheUserCopyWhereverItLives()
    {
        string dir = MakePlugin(_root, "demo-by-hand", "3s-demo", "1.0.0");
        File.WriteAllText(Path.Combine(dir, "stale.lua"), "-- gone after the update");
        PluginDescriptor installed = Assert.Single(PluginCatalog.Discover(_root));
        CatalogIndex idx = OneRelease("1.2.0");
        CatalogEntry e = idx.Plugins[0];

        string target = CatalogInstaller.TargetFolder(e, installed, _root);
        await CatalogInstaller.InstallAsync(idx, e, target, Serve(idx, e));

        Assert.Equal(Path.GetFullPath(dir), target);
        Assert.False(File.Exists(Path.Combine(dir, "stale.lua")));
        Assert.Equal("1.2.0", Assert.Single(PluginCatalog.Discover(_root)).Manifest.Version);
    }

    [Fact]
    public void ABundledPluginIsNeverTheTarget()
    {
        string bundled = Path.Combine(_root, "bundled");
        string user = Path.Combine(_root, "user");
        MakePlugin(bundled, "3s-demo", "3s-demo", "1.0.0");
        PluginDescriptor installed = Assert.Single(PluginCatalog.Discover(bundled));

        string target = CatalogInstaller.TargetFolder(OneRelease().Plugins[0], installed, user);

        Assert.Equal(Path.Combine(Path.GetFullPath(user), "3s-demo"), target);
    }

    [Fact]
    public async Task AWrongChecksumInstallsNothingAndKeepsTheOldCopy()
    {
        string dir = MakePlugin(_root, "3s-demo", "3s-demo", "1.0.0");
        CatalogIndex idx = OneRelease("1.2.0");
        CatalogEntry e = idx.Plugins[0];
        CatalogInstaller.Fetch bad = Serve(idx, e, f => f.Path == "main.lua" ? B("print('HI')") : null!);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CatalogInstaller.InstallAsync(idx, e, dir, bad));

        Assert.Contains("checksum", ex.Message);
        Assert.Equal("-- 1.0.0", File.ReadAllText(Path.Combine(dir, "main.lua")));
        Assert.Empty(Directory.GetDirectories(_root, ".*"));
    }

    [Fact]
    public async Task AShortDownloadInstallsNothing()
    {
        CatalogIndex idx = OneRelease();
        CatalogEntry e = idx.Plugins[0];
        string target = Path.Combine(_root, "3s-demo");
        CatalogInstaller.Fetch cut = Serve(idx, e, f => f.Path == "tiles/tower.png" ? new byte[] { 1, 2 } : null!);

        await Assert.ThrowsAsync<InvalidDataException>(() => CatalogInstaller.InstallAsync(idx, e, target, cut));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void LeftoversAreSweptAndAStrandedCopyIsPutBack()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".3s-demo.new-1234abcd"));
        MakePlugin(_root, ".3s-other.old-1234abcd", "3s-other", "1.0.0");   // the swap died half-way

        Assert.Empty(PluginCatalog.Discover(_root));                         // hidden: never loaded
        CatalogInstaller.SweepLeftovers(_root);

        Assert.Empty(Directory.GetDirectories(_root, ".*"));
        Assert.Equal("3s-other", Assert.Single(PluginCatalog.Discover(_root)).Id);
    }

    // ---- newest wins -------------------------------------------------------------

    [Fact]
    public void AUserUpdateOfABundledPluginLoadsUntilTheBundledCopyOvertakesIt()
    {
        string extra = Path.Combine(_root, "extra"), bundled = Path.Combine(_root, "bundled"), user = Path.Combine(_root, "user");
        MakePlugin(bundled, "3s-demo", "3s-demo", "1.0.0");
        MakePlugin(user, "3s-demo", "3s-demo", "1.2.0");

        Assert.Equal("1.2.0", Assert.Single(PluginCatalog.DiscoverNewest(null, bundled, user)).Manifest.Version);

        MakePlugin(bundled, "3s-demo", "3s-demo", "1.3.0");                  // the next Scrye release
        Assert.Equal("1.3.0", Assert.Single(PluginCatalog.DiscoverNewest(null, bundled, user)).Manifest.Version);

        MakePlugin(extra, "3s-demo", "3s-demo", "0.1.0");                    // being worked on: wins outright
        PluginDescriptor d = Assert.Single(PluginCatalog.DiscoverNewest(extra, bundled, user));
        Assert.Equal("0.1.0", d.Manifest.Version);
    }

    [Fact]
    public void ATieGoesToTheEarlierRoot()
    {
        string bundled = Path.Combine(_root, "bundled"), user = Path.Combine(_root, "user");
        MakePlugin(bundled, "3s-demo", "3s-demo", "1.0.0");
        MakePlugin(user, "3s-demo", "3s-demo", "1.0.0");

        PluginDescriptor d = Assert.Single(PluginCatalog.DiscoverNewest(null, bundled, user));
        Assert.StartsWith(bundled, d.FolderPath);
    }
}
