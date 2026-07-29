namespace Scrye.Core.Plugins;

/// <summary>
/// A plugin's <c>plugin.json</c>. Deliberately small and readable so plugins are
/// git-friendly and hand-authorable (roadmap #6/#7). A plugin is a folder containing
/// this manifest plus its <see cref="Entry"/> script (and, later, declared UI panels,
/// assets, dependencies).
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Stable unique id (folder-name-ish, e.g. "mip-vitals-hud"). Required.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? Author { get; init; }
    public string? Description { get; init; }

    /// <summary>MUDs this plugin applies to (by world name / mud id). "*" = all. Empty = all.</summary>
    public string[] MudIds { get; init; } = { "*" };

    /// <summary>Entry script, relative to the plugin folder.</summary>
    public string Entry { get; init; } = "main.lua";

    /// <summary>Whether the plugin loads. A disabled plugin stays on disk but is skipped.</summary>
    public bool Enabled { get; init; } = true;
}
