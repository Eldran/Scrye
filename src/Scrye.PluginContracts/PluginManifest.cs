namespace Scrye.Core.Plugins;

/// <summary>Compatibility requirements a plugin declares. Absent means "no constraint".</summary>
public sealed record PluginRequires
{
    /// <summary>Plugin API range this plugin needs, e.g. <c>"&gt;=1.1 &lt;2.0"</c>. See
    /// <see cref="ApiRange"/> for the grammar and <see cref="Plugins.ScryeApi"/> for what the
    /// version means. Null/empty = loads on any API version.</summary>
    public string? ScryeApi { get; init; }
}

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

    /// <summary>Scripting language of the entry script: <c>"lua"</c> (default, MoonSharp) or
    /// <c>"js"</c> (Jint). The host picks the matching runtime.</summary>
    public string Lang { get; init; } = "lua";

    /// <summary>Whether the plugin loads. A disabled plugin stays on disk but is skipped.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// What this plugin needs from the host. The important one is
    /// <see cref="PluginRequires.ScryeApi"/>: declare it and an incompatible client refuses the
    /// plugin with a clear message at load, instead of letting the script die on a missing
    /// function forty lines in. Omitting it keeps the original behaviour (always try to load),
    /// which is why every plugin written before this field existed still works.
    /// </summary>
    public PluginRequires? Requires { get; init; }

    /// <summary>
    /// Capabilities this plugin intends to use, from the <see cref="PluginPermissions"/>
    /// vocabulary. Shown to the user in the plugins manager before they enable it.
    ///
    /// <para>These are <b>declarations, not enforcement</b> — see <see cref="PluginPermissions"/>
    /// for exactly what is and isn't bounded. Declaring nothing is legal and simply means the
    /// manager has nothing to show; it does not restrict the plugin.</para>
    /// </summary>
    public string[] Permissions { get; init; } = Array.Empty<string>();
}
