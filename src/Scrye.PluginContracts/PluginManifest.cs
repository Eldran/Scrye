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

    /// <summary>Language/format of the entry: <c>"lua"</c> (default, native Lua 5.4),
    /// <c>"js"</c> (Jint), or <c>"wasm"</c> (a compiled WebAssembly module speaking
    /// scrye-wasm-abi — see docs/scrye-wasm-abi.md; permissions are enforced for wasm).
    /// The host picks the matching runtime.</summary>
    public string Lang { get; init; } = "lua";

    /// <summary>
    /// Data files this plugin ships, as script key → file name (<c>"areas": "areas.json"</c>).
    /// The host reads them from the plugin's folder at load and publishes them as
    /// <c>scrye.data.&lt;key&gt;</c>; <c>Scrye.Core.Plugins.PluginAssets</c> has the parsing rules
    /// and the (deliberately strict) name check — named rather than cref'd because the engine is
    /// deliberately not referenced from the contracts package. Declaring a file is the only way a plugin reaches the
    /// filesystem — it names files here, never a path at run time.
    ///
    /// <para>This is for a plugin's own source data: a word list, a route table, a map, a
    /// palette. Read-only. Saving state between sessions is still <c>scrye.store</c>.</para>
    /// </summary>
    public Dictionary<string, string>? Data { get; init; }

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

    /// <summary>
    /// Capture panes this plugin writes to, e.g. <c>["Chats"]</c>. The host creates them when
    /// the plugin loads, so the pane is there from the start instead of appearing the first
    /// time a line happens to be routed into it.
    ///
    /// <para>This exists because lazy creation reads as a bug on a fresh install: enable the
    /// chat plugin on a new machine, see nothing, and the pane only turns up when somebody
    /// eventually speaks. Declaring it makes "this plugin has a Chats pane" true at load
    /// rather than on first traffic.</para>
    ///
    /// <para>Optional and additive: an older host ignores the field, and a pane the user has
    /// explicitly closed is not resurrected — see the layout's closed-pane list.</para>
    /// </summary>
    public string[] Panes { get; init; } = Array.Empty<string>();
}
