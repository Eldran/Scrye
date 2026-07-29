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
}
