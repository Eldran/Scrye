using System.Text;

namespace Scrye.Core.Plugins;

/// <summary>
/// The one folder a plugin may write files into and read them back from:
/// <c>%APPDATA%/Scrye/exports</c> (the <c>scrye.exports</c> backing, API 1.21).
///
/// <para>Plugins run sandboxed with no filesystem at all, which is right - and which left a
/// mapper with no way to hand its map to another player, or to save a picture of it. This
/// opens exactly one door: a flat folder, a file NAME (never a path), a short list of
/// harmless text extensions, a size cap. A plugin cannot reach anything else on the disk,
/// cannot write something that runs (no .exe, .bat, .lua), and cannot hide a file in a
/// subfolder. Reading is the same folder: a file another player sent you goes here, and
/// the plugin that understands it can import it.</para>
///
/// <para>Shared by every plugin: an export is for the user, who names and moves the files, so
/// a per-plugin maze of subfolders would only make them harder to find. Two plugins that
/// pick the same name overwrite each other; the plugins that ship prefix their files with
/// their id.</para>
/// </summary>
public sealed class ExportFolder
{
    /// <summary>What may be written or read: text formats a user opens in a viewer.</summary>
    public static readonly IReadOnlyList<string> Extensions = new[] { ".json", ".svg", ".txt", ".csv", ".md", ".html" };

    /// <summary>Largest file written or read.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    public const int MaxNameLength = 100;

    public string Root { get; }

    public ExportFolder(string root) => Root = Path.GetFullPath(root);

    /// <summary>The standard folder: <c>%APPDATA%/Scrye/exports</c>.</summary>
    public static ExportFolder Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "exports"));

    /// <summary>
    /// Is this a file name the folder accepts? Letters, digits, space, <c>. _ - ( )</c>;
    /// no separators, no leading dot, a known extension, no Windows device name. The
    /// sentence in <paramref name="error"/> is fit to show the user.
    /// </summary>
    public static bool IsValidName(string? name, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(name)) { error = "no file name"; return false; }
        if (name.Length > MaxNameLength) { error = $"'{name}' is longer than {MaxNameLength} characters"; return false; }
        if (name != name.Trim() || name.StartsWith('.') || name.EndsWith('.'))
        { error = $"'{name}' cannot start or end with a dot or a space"; return false; }
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '(' or ')'))
            { error = $"'{name}' has a character a file name here cannot have ('{c}') - a name only, no folders"; return false; }
        if (name.Contains("..", StringComparison.Ordinal)) { error = $"'{name}' cannot contain '..'"; return false; }
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (!Extensions.Contains(ext))
        { error = $"'{name}' must end in one of {string.Join(" ", Extensions)}"; return false; }
        string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
        { error = $"'{name}' is a name Windows reserves"; return false; }
        return true;
    }

    private string PathOf(string name)
    {
        if (!IsValidName(name, out string error)) throw new ArgumentException(error);
        string full = Path.GetFullPath(Path.Combine(Root, name));
        // belt and braces: the name rules already make this impossible
        if (!string.Equals(Path.GetDirectoryName(full), Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{name}' would land outside the exports folder");
        return full;
    }

    /// <summary>Write (or replace) a file; returns its full path, for the plugin to tell the
    /// user. Written to a temporary name first and moved into place, so a reader never sees
    /// half a file and a failed write leaves the old one.</summary>
    public string Write(string name, string text)
    {
        string path = PathOf(name);
        byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? "");
        if (bytes.LongLength > MaxBytes) throw new ArgumentException($"'{name}' would be larger than {MaxBytes / (1024 * 1024)} MB");
        Directory.CreateDirectory(Root);
        string tmp = Path.Combine(Root, "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch (IOException) { }
        }
        return path;
    }

    /// <summary>A file's text. Throws <see cref="FileNotFoundException"/> when there is none.</summary>
    public string Read(string name)
    {
        string path = PathOf(name);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"there is no '{name}' in {Root}");
        if (info.Length > MaxBytes) throw new ArgumentException($"'{name}' is larger than {MaxBytes / (1024 * 1024)} MB");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>The files a plugin could read, newest first.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<string>();
        return new DirectoryInfo(Root).GetFiles()
            .Where(f => IsValidName(f.Name, out _))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.Name)
            .ToList();
    }
}
