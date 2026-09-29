using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The exports folder behind <c>scrye.exports</c>: the one place a plugin may write a file.
/// A name, never a path; harmless extensions only; the file lands whole or not at all.
/// </summary>
public sealed class ExportFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrye-exports-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AFileIsWrittenReadBackAndListed()
    {
        var f = new ExportFolder(_root);
        string path = f.Write("3s-map-all.json", "{ \"rooms\": [] }");

        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "3s-map-all.json"), path);
        Assert.Equal("{ \"rooms\": [] }", f.Read("3s-map-all.json"));
        Assert.Equal(new[] { "3s-map-all.json" }, f.List());
        Assert.Empty(Directory.GetFiles(_root, ".*"));             // no temp file left behind
    }

    [Fact]
    public void WritingAgainReplacesTheFile()
    {
        var f = new ExportFolder(_root);
        f.Write("map.svg", "<svg>old</svg>");
        f.Write("map.svg", "<svg>new</svg>");
        Assert.Equal("<svg>new</svg>", f.Read("map.svg"));
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("..\\evil.json")]
    [InlineData("sub/map.json")]
    [InlineData("C:map.json")]
    [InlineData("/etc/map.json")]
    [InlineData("map.lua")]
    [InlineData("map.exe")]
    [InlineData("map.json.bat")]
    [InlineData(".hidden.json")]
    [InlineData("map")]
    [InlineData("CON.json")]
    [InlineData("")]
    [InlineData("map\n.json")]
    public void ANameThatIsNotASafeFileNameIsRefused(string name)
    {
        var f = new ExportFolder(_root);
        Assert.False(ExportFolder.IsValidName(name, out string why));
        Assert.False(string.IsNullOrEmpty(why));
        Assert.Throws<ArgumentException>(() => f.Write(name, "x"));
        Assert.False(Directory.Exists(_root) && Directory.GetFiles(_root).Length > 0);
    }

    [Fact]
    public void AMissingFileIsAClearError()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => new ExportFolder(_root).Read("nothing.json"));
        Assert.Contains("nothing.json", ex.Message);
    }

    [Fact]
    public void OnlyReadableFilesAreListed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "tool.exe"), "b");
        File.WriteAllText(Path.Combine(_root, ".tmp.json"), "c");

        Assert.Equal(new[] { "notes.txt" }, new ExportFolder(_root).List());
    }
}
