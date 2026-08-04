namespace Scrye.Core.Plugins;

/// <summary>A discovered plugin: its manifest plus where it lives on disk.</summary>
public sealed class PluginDescriptor
{
    public PluginManifest Manifest { get; }
    public string FolderPath { get; }

    public PluginDescriptor(PluginManifest manifest, string folderPath)
    {
        Manifest = manifest;
        FolderPath = folderPath;
    }

    public string Id => Manifest.Id;

    /// <summary>Absolute path to the entry script.</summary>
    public string EntryPath => Path.Combine(FolderPath, Manifest.Entry);

    /// <summary>Can this build load the plugin? Convenience wrapper over
    /// <see cref="ScryeApi.IsCompatible"/>; see that for the rule and the wording of
    /// <paramref name="reason"/>.</summary>
    public bool IsApiCompatible(out string reason) => ScryeApi.IsCompatible(Manifest, out reason);

    /// <summary>Declared capabilities, never null. See <see cref="PluginPermissions"/> for what
    /// these do and — importantly — do not guarantee.</summary>
    public IReadOnlyList<string> Permissions => Manifest.Permissions ?? Array.Empty<string>();

    /// <summary>Does this plugin apply to a world? "*" or empty MudIds means all.</summary>
    public bool AppliesTo(string mudId) =>
        Manifest.MudIds.Length == 0 ||
        Manifest.MudIds.Any(m => m == "*" || string.Equals(m, mudId, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Manifest.Id} v{Manifest.Version} ({FolderPath})";
}
