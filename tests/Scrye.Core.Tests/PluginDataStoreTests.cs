using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

public sealed class PluginDataStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-store-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SetGetRoundTrips()
    {
        var store = new PluginDataStore(_dir, "3Scapes");
        store.Set("mapper", "rooms.count", "42");
        Assert.Equal("42", store.Get("mapper", "rooms.count"));
        Assert.Null(store.Get("mapper", "unset-key"));
    }

    [Fact]
    public void DataSurvivesANewStoreInstance()
    {
        var a = new PluginDataStore(_dir, "3Scapes");
        a.Set("mapper", "home", "temple square");

        var b = new PluginDataStore(_dir, "3Scapes");   // fresh instance, same disk
        Assert.Equal("temple square", b.Get("mapper", "home"));
    }

    [Fact]
    public void WorldsAreIsolated()
    {
        var a = new PluginDataStore(_dir, "WorldA");
        var b = new PluginDataStore(_dir, "WorldB");
        a.Set("p", "k", "from-a");
        Assert.Null(b.Get("p", "k"));
    }

    [Fact]
    public void PluginsAreIsolated()
    {
        var store = new PluginDataStore(_dir, "3Scapes");
        store.Set("alpha", "k", "1");
        store.Set("beta", "k", "2");
        Assert.Equal("1", store.Get("alpha", "k"));
        Assert.Equal("2", store.Get("beta", "k"));
    }

    [Fact]
    public void DeleteRemovesAndReportsExistence()
    {
        var store = new PluginDataStore(_dir, "w");
        store.Set("p", "k", "v");
        Assert.True(store.Delete("p", "k"));
        Assert.Null(store.Get("p", "k"));
        Assert.False(store.Delete("p", "k"));   // already gone

        var reread = new PluginDataStore(_dir, "w");   // deletion persisted
        Assert.Null(reread.Get("p", "k"));
    }

    [Fact]
    public void KeysListsAllStoredKeys()
    {
        var store = new PluginDataStore(_dir, "w");
        Assert.Empty(store.Keys("p"));
        store.Set("p", "a", "1");
        store.Set("p", "b", "2");
        string[] keys = store.Keys("p");
        Assert.Equal(2, keys.Length);
        Assert.Contains("a", keys);
        Assert.Contains("b", keys);
    }

    [Fact]
    public void HostileNamesAreSanitizedIntoValidPaths()
    {
        // world and plugin names with path separators / invalid chars must not escape the root
        var store = new PluginDataStore(_dir, "my:world/with*chars");
        store.Set("we?ird|id", "k", "v");
        Assert.Equal("v", store.Get("we?ird|id", "k"));

        var reread = new PluginDataStore(_dir, "my:world/with*chars");
        Assert.Equal("v", reread.Get("we?ird|id", "k"));
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void CorruptFileStartsEmptyInsteadOfThrowing()
    {
        var store = new PluginDataStore(_dir, "w");
        store.Set("p", "k", "v");   // creates the file

        string file = Directory.GetFiles(Path.Combine(_dir, "w"))[0];
        File.WriteAllText(file, "{ not valid json !!");

        string? warning = null;
        var reread = new PluginDataStore(_dir, "w", msg => warning = msg);
        Assert.Null(reread.Get("p", "k"));         // starts empty, no throw
        Assert.NotNull(warning);                    // and reports what happened

        reread.Set("p", "k2", "v2");                // store keeps working
        Assert.Equal("v2", reread.Get("p", "k2"));
    }

    [Fact]
    public void OverwritingWithSameValueDoesNotChangeResult()
    {
        var store = new PluginDataStore(_dir, "w");
        store.Set("p", "k", "v");
        store.Set("p", "k", "v");   // no-op path
        store.Set("p", "k", "v2");
        Assert.Equal("v2", store.Get("p", "k"));
    }
}
