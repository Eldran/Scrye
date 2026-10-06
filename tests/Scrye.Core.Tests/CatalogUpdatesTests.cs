using System.Text;
using Scrye.Core.Plugins;
using Scrye.Core.Profiles;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The startup notice's question (Joakim, 6 Oct 2026): which plugins in use have a newer
/// version in the catalogue. Only installed ones, only ones some profile enabled, never one
/// loaded from the extra folder - and every layer of every profile counts as "in use".
/// </summary>
public sealed class CatalogUpdatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrye-updates-" + Guid.NewGuid().ToString("N"));

    public CatalogUpdatesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Manifest(string id, string version) =>
        "{ \"id\": \"" + id + "\", \"name\": \"" + id.ToUpperInvariant() + "\", \"version\": \"" + version + "\" }";

    private static CatalogIndex Release(params (string Id, string Version)[] plugins) =>
        CatalogIndex.Build(plugins.SelectMany(p => new[]
        {
            (p.Id + "/plugin.json", B(Manifest(p.Id, p.Version))),
            (p.Id + "/main.lua", B("-- " + p.Version)),
        }), "v9.9.9");

    private string Plugin(string root, string id, string version)
    {
        string dir = Path.Combine(_root, root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), Manifest(id, version));
        File.WriteAllText(Path.Combine(dir, "main.lua"), "-- " + version);
        return Path.Combine(_root, root);
    }

    private static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Only_an_installed_plugin_with_a_newer_catalogue_version_is_named()
    {
        string user = Plugin("user", "a", "1.1.0");
        Plugin("user", "b", "2.0.0");                 // up to date
        Plugin("user", "d", "1.0.0");                 // not in the catalogue
        var index = Release(("a", "1.2.0"), ("b", "2.0.0"), ("c", "1.0.0"));   // c: not installed

        var found = CatalogUpdates.Find(index, PluginCatalog.DiscoverNewest(null, user), enabled: null);

        Assert.Single(found);
        Assert.Equal("a", found[0].Entry.Id);
        Assert.Equal("1.1.0", found[0].InstalledVersion);
    }

    [Fact]
    public void A_plugin_nobody_enabled_is_not_mentioned()
    {
        string user = Plugin("user", "a", "1.0.0");
        Plugin("user", "b", "1.0.0");
        var index = Release(("a", "1.1.0"), ("b", "1.1.0"));

        var found = CatalogUpdates.Find(index, PluginCatalog.DiscoverNewest(null, user), Set("B"));

        Assert.Equal(new[] { "b" }, found.Select(p => p.Entry.Id));
    }

    [Fact]
    public void A_plugin_loaded_from_the_extra_folder_is_left_alone()
    {
        string extra = Plugin("extra", "a", "1.0.0");
        string user = Plugin("user", "b", "1.0.0");
        var index = Release(("a", "1.1.0"), ("b", "1.1.0"));

        var found = CatalogUpdates.Find(index, PluginCatalog.DiscoverNewest(extra, user), null, extra);

        Assert.Equal(new[] { "b" }, found.Select(p => p.Entry.Id));
    }

    [Fact]
    public void The_newest_copy_on_disk_is_what_is_compared()
    {
        string bundled = Plugin("bundled", "a", "1.0.0");
        string user = Plugin("user", "a", "1.2.0");     // a catalogue update already installed
        var index = Release(("a", "1.2.0"));

        Assert.Empty(CatalogUpdates.Find(index, PluginCatalog.DiscoverNewest(null, bundled, user), null));
    }

    [Fact]
    public void The_notice_names_a_few_and_counts_the_rest()
    {
        string user = Plugin("user", "a", "1.0.0");
        foreach (string id in new[] { "b", "c", "d" }) Plugin("user", id, "1.0.0");
        var index = Release(("a", "1.1.0"), ("b", "1.1.0"), ("c", "1.1.0"), ("d", "2.0.0"));
        var found = CatalogUpdates.Find(index, PluginCatalog.DiscoverNewest(null, user), null);

        Assert.Equal("4 plugin updates: A 1.1.0, B 1.1.0, C 1.1.0 and 1 more", CatalogUpdates.Describe(found));
        Assert.Equal("1 plugin update: A 1.1.0", CatalogUpdates.Describe(found.Take(1).ToList()));
        Assert.Equal("", CatalogUpdates.Describe(Array.Empty<CatalogUpdates.Pending>()));
    }

    [Fact]
    public void Every_profile_layer_counts_as_in_use()
    {
        var store = new ProfileStore(Path.Combine(_root, "profiles"));
        store.SaveGlobal(new ProfileLayer { Plugins = { "g" } });
        store.SaveWorld("flat", new ProfileLayer { Plugins = { "w" } });
        store.SaveMud("3s", new ProfileLayer { Kind = LayerKind.Mud, Name = "3s", Plugins = { "m" } });
        store.SaveCharacter("3s", null, "Solo", new ProfileLayer { Kind = LayerKind.Character, Name = "Solo", Plugins = { "c1" } });
        store.SaveAccount("3s", "acct", new ProfileLayer { Kind = LayerKind.Account, Name = "acct", Plugins = { "a" } });
        store.SaveCharacter("3s", "acct", "Goran", new ProfileLayer { Kind = LayerKind.Character, Name = "Goran", Plugins = { "3S-Viking-Status-GMCP" } });

        ISet<string> all = store.AllEnabledPlugins();

        foreach (string id in new[] { "g", "w", "m", "c1", "a", "3s-viking-status-gmcp" })
            Assert.True(all.Contains(id), id);
        Assert.Equal(6, all.Count);
    }
}
