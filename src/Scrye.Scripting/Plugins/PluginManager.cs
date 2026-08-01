using Scrye.Core.Plugins;
using Scrye.Core.Text;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Loads a set of plugins for a session and fans session events out to all of them.
/// Discovery is the caller's job (<see cref="PluginCatalog"/>); this owns the loaded
/// <see cref="IPluginRuntime"/>s (Lua or JS, chosen per-manifest) and their lifecycle. A plugin that throws on load is
/// reported and skipped — one bad plugin never blocks the others.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly List<PluginDescriptor> _descriptors;   // available plugins (mutated on the loop by rescan/remove)
    private readonly IPluginHost _host;
    private readonly Action<string> _report;
    private readonly Action<string>? _dropPanels;           // (pluginId) → host removes its HUD panels
    private readonly Func<IReadOnlyList<PluginDescriptor>>? _rediscover;   // re-scan disk (for add/remove)
    private readonly string? _userRoot;                     // plugins under here are removable (deletable)
    private readonly Action<string, bool>? _persistEnable;  // (pluginId, enabled) → save the choice to the profile
    private readonly List<IPluginRuntime> _runtimes = new();
    // Authoritative opt-in set: the plugins this world should load. Seeded from the
    // character's profile; Enable/Disable mutate it and persist via _persistEnable.
    private readonly HashSet<string> _enabled;

    // Immutable snapshots republished on every set-change (always on the loop / pre-loop) so the
    // UI can read plugin state without touching the mutable _runtimes / _descriptors.
    private volatile string[] _loadedIds = Array.Empty<string>();
    private volatile PluginInfo[] _info = Array.Empty<PluginInfo>();

    public IReadOnlyList<string> LoadedIds => _loadedIds;
    public int Count => _runtimes.Count;

    /// <param name="plugins">Descriptors to load (already filtered by MUD/enabled).</param>
    /// <param name="host">Session bridge the plugins act through.</param>
    /// <param name="report">Status sink (loaded / failed), shown in the world output.</param>
    /// <param name="dropPanels">Called (pluginId) when a plugin is unloaded, so the host can
    /// remove that plugin's HUD panels. Invoked on the same thread as the unload.</param>
    /// <param name="rediscover">Re-scans the plugin roots (for <see cref="Rescan"/>).</param>
    /// <param name="userRoot">The writable user plugins folder; plugins under it are removable.</param>
    /// <param name="plugins">All plugins AVAILABLE for this world (the manager's catalogue).</param>
    /// <param name="enabledIds">Ids the character has opted into — only these load at startup.</param>
    /// <param name="persistEnable">Called (id, enabled) when the user toggles a plugin, so the
    /// choice can be saved to the connected character's profile. Null = session-only (quick-connect).</param>
    public PluginManager(IReadOnlyList<PluginDescriptor> plugins, IEnumerable<string> enabledIds,
                         IPluginHost host, Action<string> report,
                         Action<string>? dropPanels = null,
                         Func<IReadOnlyList<PluginDescriptor>>? rediscover = null, string? userRoot = null,
                         Action<string, bool>? persistEnable = null)
    {
        _descriptors = plugins.ToList();
        _enabled = new HashSet<string>(enabledIds, StringComparer.Ordinal);
        _host = host;
        _report = report;
        _dropPanels = dropPanels;
        _rediscover = rediscover;
        _userRoot = string.IsNullOrEmpty(userRoot) ? null : Path.GetFullPath(userRoot);
        _persistEnable = persistEnable;
        // Load only the opted-in plugins (that are actually present in the catalogue).
        foreach (PluginDescriptor d in _descriptors)
            if (_enabled.Contains(d.Manifest.Id)) LoadOne(d);
        Republish();
    }

    private void LoadOne(PluginDescriptor d)
    {
        try
        {
            IPluginRuntime runtime = d.Manifest.Lang.Equals("js", StringComparison.OrdinalIgnoreCase)
                ? new JsPluginRuntime(d, _host)
                : new LuaPluginRuntime(d, _host);
            runtime.Load();
            _runtimes.Add(runtime);
            _report($"loaded plugin '{d.Manifest.Id}' v{d.Manifest.Version}");
        }
        catch (Exception ex)
        {
            _report($"plugin '{d.Manifest.Id}' failed to load: {ex.Message}");
        }
        Republish();
    }

    private void UnloadRuntime(string id)
    {
        IPluginRuntime? rt = _runtimes.FirstOrDefault(r => r.Id == id);
        if (rt is null) return;
        _runtimes.Remove(rt);
        rt.Dispose();                 // disposes its watches/timers/rules/hooks (on the loop)
        _dropPanels?.Invoke(id);      // host removes the plugin's HUD panels
        Republish();
    }

    private bool IsRemovable(PluginDescriptor d) =>
        _userRoot is not null &&
        Path.GetFullPath(d.FolderPath).StartsWith(_userRoot, StringComparison.OrdinalIgnoreCase);

    private void Republish()
    {
        _loadedIds = _runtimes.Select(r => r.Id).ToArray();
        var loaded = new HashSet<string>(_loadedIds, StringComparer.Ordinal);
        _info = _descriptors
            .Select(d => new PluginInfo(d.Manifest.Id, d.Manifest.Name, d.Manifest.Version,
                                        loaded.Contains(d.Manifest.Id), IsRemovable(d)))
            .ToArray();
    }

    // ---- lifecycle (call on the loop thread) ---------------------------------

    /// <summary>Dispose + re-run a plugin's entry script (edit-then-reload). Loop-thread only.
    /// Only meaningful for an enabled plugin; does nothing for one that isn't loaded.</summary>
    public void Reload(string id)
    {
        PluginDescriptor? d = _descriptors.FirstOrDefault(x => x.Manifest.Id == id);
        if (d is null || _runtimes.All(r => r.Id != id)) return;
        UnloadRuntime(id);
        LoadOne(d);
    }

    /// <summary>Turn a plugin OFF for this character: unload it and drop it from the opt-in set
    /// (persisted to the profile). Loop-thread only.</summary>
    public void Disable(string id)
    {
        bool changed = _enabled.Remove(id);
        if (_runtimes.Any(r => r.Id == id)) { UnloadRuntime(id); _report($"disabled plugin '{id}'"); }
        if (changed) _persistEnable?.Invoke(id, false);
    }

    /// <summary>Turn a plugin ON for this character: add it to the opt-in set (persisted) and
    /// load it if present. Loop-thread only.</summary>
    public void Enable(string id)
    {
        bool changed = _enabled.Add(id);
        if (_runtimes.All(r => r.Id != id))
        {
            PluginDescriptor? d = _descriptors.FirstOrDefault(x => x.Manifest.Id == id);
            if (d is not null) LoadOne(d);
        }
        if (changed) _persistEnable?.Invoke(id, true);
    }

    /// <summary>Re-scan the plugin roots: load newly-added plugins, unload ones deleted from disk.
    /// Honours the per-session disabled set. Loop-thread only.</summary>
    public void Rescan()
    {
        if (_rediscover is null) return;
        // first install any *.scryeplugin packages dropped into the user folder
        if (_userRoot is not null) PluginPackage.InstallAllIn(_userRoot, _report);
        List<PluginDescriptor> found = _rediscover().ToList();
        var foundIds = new HashSet<string>(found.Select(d => d.Manifest.Id), StringComparer.Ordinal);

        foreach (IPluginRuntime rt in _runtimes.Where(r => !foundIds.Contains(r.Id)).ToList())
            UnloadRuntime(rt.Id);   // vanished from disk

        _descriptors.Clear();
        _descriptors.AddRange(found);

        // load any opted-in plugin that appeared on disk and isn't running yet
        foreach (PluginDescriptor d in found)
            if (_enabled.Contains(d.Manifest.Id) && _runtimes.All(r => r.Id != d.Manifest.Id))
                LoadOne(d);

        Republish();
    }

    /// <summary>Unload a plugin and delete its folder from disk (user plugins only; bundled
    /// plugins are just unloaded). Loop-thread only.</summary>
    public void Remove(string id)
    {
        PluginDescriptor? d = _descriptors.FirstOrDefault(x => x.Manifest.Id == id);
        UnloadRuntime(id);
        if (_enabled.Remove(id)) _persistEnable?.Invoke(id, false);
        if (d is not null)
        {
            _descriptors.Remove(d);
            if (IsRemovable(d))
            {
                try { Directory.Delete(d.FolderPath, recursive: true); _report($"removed plugin '{id}'"); }
                catch (Exception ex) { _report($"could not delete plugin '{id}': {ex.Message}"); }
            }
            else _report($"plugin '{id}' unloaded (bundled — not deleted from disk)");
        }
        Republish();
    }

    /// <summary>Snapshot of discovered plugins + their loaded/removable state, for the manager UI
    /// (a volatile immutable array — UI-thread safe).</summary>
    public IReadOnlyList<PluginInfo> ListPlugins() => _info;

    /// <summary>Run a server output line through every plugin (onLine hooks + triggers) and
    /// fold their gag/rewrite decisions: returns the line to display (possibly rewritten),
    /// or null to gag it. Also dispatches the prompt hook for prompt lines. Set this as the
    /// session's <c>LineDisplayFilter</c>.</summary>
    public Line? ProcessLine(Line line)
    {
        // Prompt hook: GA/EOR-flagged prompts, or a bare ">" line — 3Scapes sends its
        // prompt as plain text without GA, so IsPrompt alone never fires. Same heuristic
        // the session core uses for prompt-gated sequences.
        if (line.IsPrompt || line.PlainText.Trim() == ">") DispatchPrompt();

        string text = line.PlainText;
        bool gag = false;
        string? rewrite = null;
        for (int i = 0; i < _runtimes.Count; i++)
        {
            (bool g, string? rw) = _runtimes[i].ProcessLine(text);
            if (g) gag = true;
            if (rw is not null) rewrite = rw;
        }
        if (gag) return null;
        return rewrite is not null ? Line.FromText(rewrite) : line;
    }

    /// <summary>Run user input through every plugin's aliases: returns the command to
    /// process (possibly rewritten), or null if a plugin consumed it. Set this as the
    /// session's <c>InputFilter</c>.</summary>
    public string? ProcessInput(string text)
    {
        string current = text;
        for (int i = 0; i < _runtimes.Count; i++)
        {
            (bool consumed, string? rewrite) = _runtimes[i].ProcessInput(current);
            if (consumed) return null;
            if (rewrite is not null) current = rewrite;
        }
        return current;
    }

    public void DispatchChannel(string channel, string message)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchChannel(channel, message);
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchGmcp(package, json);
    }

    /// <summary>Advance every plugin's timers (fed from the session's per-second tick).</summary>
    public void Tick(double dtSeconds)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].Tick(dtSeconds);
    }

    public void DispatchConnect()    { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchConnect(); }
    public void DispatchDisconnect() { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchDisconnect(); }
    public void DispatchPrompt()     { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchPrompt(); }

    /// <summary>Fire a panel-button callback owned by <paramref name="pluginId"/>. Runs on the loop thread.</summary>
    /// <summary>Fire a colorgrid cell-click callback with the clicked cell. Loop-thread only.</summary>
    public void InvokeCellAction(string pluginId, string actionId, int col, int row, string ch)
    {
        IPluginRuntime? rt = _runtimes.FirstOrDefault(r => r.Id == pluginId);
        rt?.InvokeCellAction(actionId, col, row, ch);
    }

    public void InvokeAction(string pluginId, string actionId)
    {
        for (int i = 0; i < _runtimes.Count; i++)
            if (_runtimes[i].Id == pluginId) { _runtimes[i].InvokeAction(actionId); return; }
    }

    public void Dispose()
    {
        foreach (IPluginRuntime r in _runtimes) r.Dispose();
        _runtimes.Clear();
        Republish();
    }
}

/// <summary>A discovered plugin's identity + loaded/removable state, for the plugins-manager UI.</summary>
public readonly record struct PluginInfo(string Id, string Name, string Version, bool Loaded, bool Removable);
