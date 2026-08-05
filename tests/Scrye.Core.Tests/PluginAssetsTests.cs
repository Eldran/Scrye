using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Declared plugin data files (<see cref="PluginAssets"/>). Two things matter here. First, that
/// nothing a manifest can say reaches outside the plugin's own folder — this is the only code
/// path from a manifest to the disk. Second, that a broken entry costs the author that entry and
/// nothing else: a plugin whose word list failed to parse still has to start.
/// </summary>
public class PluginAssetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-assets-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _reports = new();

    public PluginAssetsTests()
    {
        Directory.CreateDirectory(_dir);
        Write("areas.json", """{"aegis":{"p":"e e s","mobs":{"Zombie":"zombie"},"noloop":0},"list":[1,2.5,true,null]}""");
        Write("words.txt", "# comment\n\nalpha\n  beta  \r\ngamma\n");
        Write("tpl.md", "hello {name}");
        Write("bad.json", "{ not json ");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    private IReadOnlyDictionary<string, object?> Load(params (string Key, string File)[] entries) =>
        PluginAssets.Load(_dir, entries.ToDictionary(e => e.Key, e => e.File), _reports.Add);

    // ---- parsing --------------------------------------------------------------

    [Fact]
    public void Json_becomes_a_nested_object_graph()
    {
        var data = Load(("areas", "areas.json"));
        var root = Assert.IsType<Dictionary<string, object?>>(data["areas"]);
        var aegis = Assert.IsType<Dictionary<string, object?>>(root["aegis"]);
        Assert.Equal("e e s", aegis["p"]);
        Assert.Equal(0d, aegis["noloop"]);              // one numeric type: double
        var mobs = Assert.IsType<Dictionary<string, object?>>(aegis["mobs"]);
        Assert.Equal("zombie", mobs["Zombie"]);
        Assert.Equal(new object?[] { 1d, 2.5d, true, null }, Assert.IsType<List<object?>>(root["list"]));
    }

    [Fact]
    public void Text_becomes_a_list_of_lines_without_blanks_or_comments()
    {
        var data = Load(("words", "words.txt"));
        Assert.Equal(new object?[] { "alpha", "beta", "gamma" }, Assert.IsType<List<object?>>(data["words"]));
    }

    [Fact]
    public void An_unknown_extension_is_handed_over_as_raw_text()
    {
        var data = Load(("tpl", "tpl.md"));
        Assert.Equal("hello {name}", data["tpl"]);
    }

    [Fact]
    public void No_declared_data_is_not_an_error()
    {
        Assert.Empty(PluginAssets.Load(_dir, null));
        Assert.Empty(PluginAssets.Load(_dir, new Dictionary<string, string>()));
    }

    // ---- containment ----------------------------------------------------------

    [Theory]
    [InlineData("../secret.json")]          // traversal
    [InlineData("sub/areas.json")]          // subfolder
    [InlineData("sub\\areas.json")]         // subfolder, Windows separator
    [InlineData("/etc/passwd")]             // absolute
    [InlineData("C:\\Windows\\win.ini")]    // absolute, Windows
    [InlineData("a..b.json")]               // '..' anywhere, not just as a segment
    [InlineData("CON")]                     // Windows device, bare
    [InlineData("NUL.json")]                // Windows device, with an extension
    [InlineData(".hidden")]                 // leading dot
    [InlineData("trailing.")]               // trailing dot (Windows strips it)
    [InlineData("")]
    public void Unsafe_file_names_are_refused(string name) =>
        Assert.False(PluginAssets.IsSafeFileName(name));

    [Theory]
    [InlineData("areas.json")]
    [InlineData("a-b_c.txt")]
    [InlineData("_private.json")]
    [InlineData("9lives.txt")]              // a digit is fine in a FILE name
    public void Ordinary_file_names_are_accepted(string name) =>
        Assert.True(PluginAssets.IsSafeFileName(name));

    [Fact]
    public void An_over_long_file_name_is_refused() =>
        Assert.False(PluginAssets.IsSafeFileName(new string('x', PluginAssets.MaxNameLength + 1)));

    [Fact]
    public void A_traversing_entry_is_dropped_and_reported_without_reading_anything()
    {
        string outside = Path.Combine(Path.GetDirectoryName(_dir)!, "scrye-assets-outside.json");
        File.WriteAllText(outside, """{"leaked":true}""");
        try
        {
            var data = Load(("bad", "../scrye-assets-outside.json"), ("good", "areas.json"));
            Assert.False(data.ContainsKey("bad"));
            Assert.True(data.ContainsKey("good"));      // one bad entry does not poison the rest
            Assert.Contains(_reports, r => r.Contains("bad") && r.Contains("unsafe"));
        }
        finally { File.Delete(outside); }
    }

    // ---- keys -----------------------------------------------------------------

    [Theory]
    [InlineData("areas")]
    [InlineData("_x")]
    [InlineData("word_list_2")]
    public void Identifier_keys_are_accepted(string key) => Assert.True(PluginAssets.IsSafeKey(key));

    [Theory]
    [InlineData("9lives")]                  // a script cannot write scrye.data.9lives
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("has space")]
    [InlineData("")]
    public void Non_identifier_keys_are_refused(string key) => Assert.False(PluginAssets.IsSafeKey(key));

    // ---- failure is per-entry -------------------------------------------------

    [Fact]
    public void A_missing_file_drops_only_its_own_key()
    {
        var data = Load(("gone", "nope.json"), ("words", "words.txt"));
        Assert.False(data.ContainsKey("gone"));
        Assert.True(data.ContainsKey("words"));
        Assert.Contains(_reports, r => r.Contains("gone") && r.Contains("no file named"));
    }

    [Fact]
    public void Malformed_json_reports_and_omits_the_key_rather_than_throwing()
    {
        var data = Load(("broken", "bad.json"));
        Assert.False(data.ContainsKey("broken"));       // absent, not a null value
        Assert.Contains(_reports, r => r.Contains("broken") && r.Contains("not valid JSON"));
    }

    [Fact]
    public void An_oversized_file_is_refused()
    {
        string name = "huge.txt";
        File.WriteAllBytes(Path.Combine(_dir, name), new byte[PluginAssets.MaxFileBytes + 1]);
        var data = Load(("huge", name));
        Assert.False(data.ContainsKey("huge"));
        Assert.Contains(_reports, r => r.Contains("huge") && r.Contains("limit"));
    }

    [Fact]
    public void More_entries_than_the_cap_stop_at_the_cap()
    {
        var many = new Dictionary<string, string>();
        for (int i = 0; i < PluginAssets.MaxEntries + 5; i++) many["k" + i] = "words.txt";
        var data = PluginAssets.Load(_dir, many, _reports.Add);
        Assert.Equal(PluginAssets.MaxEntries, data.Count);
        Assert.Contains(_reports, r => r.Contains("more than"));
    }
}
