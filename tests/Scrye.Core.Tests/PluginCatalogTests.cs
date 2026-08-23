using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

public class PluginCatalogTests : IDisposable
{
    private readonly string _root;

    public PluginCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scrye_plugintest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void Make(string folder, string manifestJson)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
    }

    [Fact]
    public void DiscoverParsesManifestsAndAppliesDefaults()
    {
        Make("hello", "{\"id\":\"hello\",\"name\":\"Hello\"}");
        var found = PluginCatalog.Discover(_root);
        var d = Assert.Single(found);
        Assert.Equal("hello", d.Manifest.Id);
        Assert.Equal("0.0.0", d.Manifest.Version);        // default
        Assert.Equal(new[] { "*" }, d.Manifest.MudIds);   // default
        Assert.Equal("main.lua", d.Manifest.Entry);       // default
        Assert.True(d.Manifest.Enabled);                  // default
    }

    [Fact]
    public void IgnoresBrokenJsonMissingIdAndManifestlessFolders()
    {
        Make("broken", "{ not json ]");
        Make("noid", "{\"name\":\"no id here\"}");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));   // no plugin.json
        Make("ok", "{\"id\":\"ok\"}");

        var found = PluginCatalog.Discover(_root);
        Assert.Single(found);
        Assert.Equal("ok", found[0].Manifest.Id);
    }

    [Fact]
    public void ForMudFiltersByMudIdAndEnabled()
    {
        Make("all", "{\"id\":\"all\",\"mudIds\":[\"*\"]}");
        Make("threes", "{\"id\":\"threes\",\"mudIds\":[\"3Scapes\"]}");
        Make("aard", "{\"id\":\"aard\",\"mudIds\":[\"Aardwolf\"]}");
        Make("off", "{\"id\":\"off\",\"mudIds\":[\"3Scapes\"],\"enabled\":false}");

        var forThrees = PluginCatalog.ForMud("3Scapes", _root).Select(d => d.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "all", "threes" }, forThrees);   // 'aard' N/A, 'off' disabled
    }

    [Fact]
    public void AppliesToIsCaseInsensitive()
    {
        Make("threes", "{\"id\":\"threes\",\"mudIds\":[\"3Scapes\"]}");
        var d = PluginCatalog.Discover(_root)[0];
        Assert.True(d.AppliesTo("3scapes"));
        Assert.True(d.AppliesTo("3SCAPES"));
        Assert.False(d.AppliesTo("Aardwolf"));
    }

    [Fact]
    public void FirstRootWinsOnIdCollision()
    {
        string root2 = Path.Combine(Path.GetTempPath(), "scrye_plugintest2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root2, "dup"));
        File.WriteAllText(Path.Combine(root2, "dup", "plugin.json"), "{\"id\":\"dup\",\"name\":\"from root2\"}");
        Make("dup", "{\"id\":\"dup\",\"name\":\"from root1\"}");
        try
        {
            var found = PluginCatalog.Discover(_root, root2);
            var d = Assert.Single(found);
            Assert.Equal("from root1", d.Manifest.Name);   // _root passed first
        }
        finally { Directory.Delete(root2, true); }
    }

    // The shape Scrye actually calls Discover with once an extra plugin folder is configured:
    // extra, bundled, user. The extra folder is passed FIRST precisely so a plugin worked on in
    // place overrides the bundled copy of the same id -- if the bundled one won, pointing the
    // client at the folder would appear to do nothing.
    [Fact]
    public void ExtraRootFirstOverridesTheBundledCopyOfTheSameId()
    {
        string bundled = Path.Combine(Path.GetTempPath(), "scrye_bundled_" + Guid.NewGuid().ToString("N"));
        string user = Path.Combine(Path.GetTempPath(), "scrye_user_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(bundled, "3s-map"));
        File.WriteAllText(Path.Combine(bundled, "3s-map", "plugin.json"),
            "{\"id\":\"3s-map\",\"name\":\"shipped\",\"version\":\"1.0.0\"}");
        Directory.CreateDirectory(Path.Combine(user, "other"));
        File.WriteAllText(Path.Combine(user, "other", "plugin.json"), "{\"id\":\"other\"}");
        Make("3s-map", "{\"id\":\"3s-map\",\"name\":\"in progress\",\"version\":\"1.1.0\"}");
        try
        {
            var found = PluginCatalog.Discover(_root, bundled, user);
            PluginDescriptor map = found.Single(d => d.Manifest.Id == "3s-map");
            Assert.Equal("in progress", map.Manifest.Name);
            Assert.Equal(_root, Directory.GetParent(map.FolderPath)!.FullName);   // loaded from where it lives
            Assert.Equal(2, found.Count);                                     // the other root still contributes
        }
        finally { Directory.Delete(bundled, true); Directory.Delete(user, true); }
    }

    // Empty is the default for the setting, and a path can be typed wrong or point at a folder
    // that has since moved. Neither may cost the user the plugins they do have.
    [Fact]
    public void AnEmptyOrMissingExtraRootIsSkippedNotFatal()
    {
        Make("keeper", "{\"id\":\"keeper\"}");
        string gone = Path.Combine(Path.GetTempPath(), "scrye_absent_" + Guid.NewGuid().ToString("N"));

        Assert.Equal("keeper", Assert.Single(PluginCatalog.Discover("", _root)).Manifest.Id);
        Assert.Equal("keeper", Assert.Single(PluginCatalog.Discover(gone, _root)).Manifest.Id);
    }

    [Fact]
    public void NormaliseRootTreatsBlankAsUnset()
    {
        Assert.Null(PluginCatalog.NormaliseRoot(null));
        Assert.Null(PluginCatalog.NormaliseRoot(""));
        Assert.Null(PluginCatalog.NormaliseRoot("   "));
        Assert.Null(PluginCatalog.NormaliseRoot("\"\""));       // a pasted empty quoted path
    }

    // Explorer's "Copy as path" wraps the path in quotes, and pasting is exactly how this box
    // gets filled. Quoted or not, it has to mean the same folder.
    [Fact]
    public void NormaliseRootStripsQuotesAndSpace()
    {
        Make("keeper", "{\"id\":\"keeper\"}");
        Assert.Equal(_root, PluginCatalog.NormaliseRoot("  " + _root + "  "));
        Assert.Equal(_root, PluginCatalog.NormaliseRoot("\"" + _root + "\""));
        Assert.Equal("keeper", Assert.Single(
            PluginCatalog.Discover(PluginCatalog.NormaliseRoot("\"" + _root + "\"")!)).Manifest.Id);
    }

    // Pointing at the plugin itself is the obvious mistake: the folder you have open while
    // editing is the plugin's, not the folder above it. Scanning that would look a level too
    // deep and find nothing at all -- a silence indistinguishable from never having set it.
    [Fact]
    public void NormaliseRootAcceptsThePluginFolderItselfAndMeansItsParent()
    {
        Make("keeper", "{\"id\":\"keeper\"}");
        string pluginFolder = Path.Combine(_root, "keeper");

        Assert.Equal(_root, PluginCatalog.NormaliseRoot(pluginFolder));
        Assert.Equal(_root, PluginCatalog.NormaliseRoot(pluginFolder + Path.DirectorySeparatorChar));
        Assert.Equal("keeper", Assert.Single(
            PluginCatalog.Discover(PluginCatalog.NormaliseRoot(pluginFolder)!)).Manifest.Id);
    }

    // A real plugin root is left exactly as typed -- the parent rewrite must not fire just
    // because the folder exists, or every correctly-typed path would scan one level too high.
    [Fact]
    public void NormaliseRootLeavesARealRootAlone()
    {
        Make("keeper", "{\"id\":\"keeper\"}");
        Assert.Equal(_root, PluginCatalog.NormaliseRoot(_root));

        string missing = Path.Combine(Path.GetTempPath(), "scrye_absent_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(missing, PluginCatalog.NormaliseRoot(missing));   // reported, not rewritten
    }
}
