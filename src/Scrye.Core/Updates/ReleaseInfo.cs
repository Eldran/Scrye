using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scrye.Core.Plugins;

namespace Scrye.Core.Updates;

/// <summary>
/// The newest Scrye release, as published in <c>catalog/release.json</c> on main: its version,
/// its tag and the first lines of its release note. Scrye reads it to say "v1.9.2 is out" -
/// it does not download or install anything; the user follows the link to the release page.
///
/// <para>The link is never taken from the file. It is built from the tag, which must look like
/// a version, and always points at this repo's releases page - so a bad or hostile file can at
/// worst name a wrong version, never send the user's browser (or shell) somewhere else.</para>
/// </summary>
public sealed record ReleaseInfo
{
    /// <summary>Where Scrye looks for the newest release.</summary>
    public const string DefaultUrl = "https://raw.githubusercontent.com/Eldran/Scrye/main/catalog/release.json";

    /// <summary>The release page a tag's link is built on.</summary>
    public const string ReleasePageBase = "https://github.com/Eldran/Scrye/releases/tag/";

    public const int CurrentFormat = 1;

    /// <summary>Longest release note kept; the rest is on the release page.</summary>
    public const int MaxNotes = 1200;

    public int Format { get; init; } = CurrentFormat;
    /// <summary>The version, without a leading 'v' (<c>1.9.2</c>).</summary>
    public string Version { get; init; } = "";
    /// <summary>The git tag it was released as (<c>v1.9.2</c>).</summary>
    public string Tag { get; init; } = "";
    public string? Notes { get; init; }
    /// <summary>When the tag was made, ISO date.</summary>
    public string? Published { get; init; }

    /// <summary>The release page for this tag.</summary>
    [JsonIgnore]
    public string PageUrl => ReleasePageBase + Uri.EscapeDataString(Tag);

    private static readonly Regex TagShape = new(@"^v?\d+(\.\d+){0,3}(-[0-9A-Za-z.]+)?$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions) + "\n";

    /// <summary>Read <c>catalog/release.json</c>. Throws <see cref="InvalidDataException"/> with a sentence
    /// fit for the user when it is not one, or names a tag that does not look like a version.</summary>
    public static ReleaseInfo Parse(string json)
    {
        ReleaseInfo? r;
        try { r = JsonSerializer.Deserialize<ReleaseInfo>(json, ReadOptions); }
        catch (JsonException ex) { throw new InvalidDataException("the release file is not valid JSON: " + ex.Message); }
        if (r is null) throw new InvalidDataException("the release file is empty");
        if (r.Format != CurrentFormat)
            throw new InvalidDataException($"the release file is format {r.Format}; this Scrye reads format {CurrentFormat}");
        if (r.Tag is null || !TagShape.IsMatch(r.Tag))
            throw new InvalidDataException("the release file names no usable tag");
        string version = string.IsNullOrWhiteSpace(r.Version) ? VersionOfTag(r.Tag) : r.Version.Trim();
        string? notes = r.Notes?.Trim();
        if (notes is { Length: > MaxNotes }) notes = notes[..(MaxNotes - 1)].TrimEnd() + "…";
        return r with { Version = version, Notes = string.IsNullOrEmpty(notes) ? null : notes };
    }

    /// <summary>A release from its tag and the tag's annotation (what <c>--release</c> writes).</summary>
    public static ReleaseInfo FromTag(string tag, string? annotation, string? published = null)
    {
        if (!TagShape.IsMatch(tag)) throw new ArgumentException($"'{tag}' does not look like a version tag (v1.9.2)");
        string? notes = annotation?.Replace("\r\n", "\n").Trim();
        // a signed tag carries its signature after the message
        int sig = notes?.IndexOf("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal) ?? -1;
        if (sig >= 0) notes = notes![..sig].TrimEnd();
        if (notes is { Length: > MaxNotes }) notes = notes[..(MaxNotes - 1)].TrimEnd() + "…";
        return new ReleaseInfo
        {
            Version = VersionOfTag(tag), Tag = tag,
            Notes = string.IsNullOrEmpty(notes) ? null : notes, Published = published,
        };
    }

    public static string VersionOfTag(string tag) =>
        tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    /// <summary>Is this release newer than the running <paramref name="current"/> version?</summary>
    public bool IsNewerThan(string current) => PluginVersion.IsNewer(Version, current);

    /// <summary>
    /// This build's version: the assembly's informational version with any <c>+commit</c> suffix
    /// the SDK appends cut off, else its file version. <c>Version</c> in Directory.Build.props sets
    /// both, so it is the one number to bump before tagging a release.
    /// </summary>
    public static string CurrentVersion(Assembly? assembly = null)
    {
        Assembly a = assembly ?? typeof(ReleaseInfo).Assembly;
        string? info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return a.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
