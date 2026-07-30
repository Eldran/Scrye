using Scrye.Core.Plugins;
using Scrye.Core.Text;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Loads a set of plugins for a session and fans session events out to all of them.
/// Discovery is the caller's job (<see cref="PluginCatalog"/>); this owns the loaded
/// <see cref="LuaPluginRuntime"/>s and their lifecycle. A plugin that throws on load is
/// reported and skipped — one bad plugin never blocks the others.
/// </summary>
public sealed class PluginManager : IDisposable
{
    private readonly List<PluginDescriptor> _descriptors;   // all discovered (fixed after ctor)
    private readonly IPluginHost _host;
    private readonly Action<string> _report;
    private readonly Action<string>? _dropPanels;           // (pluginId) → host removes its HUD panels
    private readonly List<LuaPluginRuntime> _runtimes = new();

    // Ids of currently-loaded plugins, republished as an immutable snapshot whenever the set
    // changes (always on the loop thread) so the UI can read it without touching _runtimes.
    private volatile string[] _loadedIds = Array.Empty<string>();

    public IReadOnlyList<string> LoadedIds => _loadedIds;
    public int Count => _runtimes.Count;

    /// <param name="plugins">Descriptors to load (already filtered by MUD/enabled).</param>
    /// <param name="host">Session bridge the plugins act through.</param>
    /// <param name="report">Status sink (loaded / failed), shown in the world output.</param>
    /// <param name="dropPanels">Called (pluginId) when a plugin is unloaded, so the host can
    /// remove that plugin's HUD panels. Invoked on the same thread as the unload.</param>
    public PluginManager(IReadOnlyList<PluginDescriptor> plugins, IPluginHost host, Action<string> report,
                         Action<string>? dropPanels = null)
    {
        _descriptors = plugins.ToList();
        _host = host;
        _report = report;
        _dropPanels = dropPanels;
        foreach (PluginDescriptor d in _descriptors) LoadOne(d);
    }

    private void LoadOne(PluginDescriptor d)
    {
        try
        {
            var runtime = new LuaPluginRuntime(d, _host);
            runtime.Load();
            _runtimes.Add(runtime);
            _report($"loaded plugin '{d.Manifest.Id}' v{d.Manifest.Version}");
        }
        catch (Exception ex)
        {
            _report($"plugin '{d.Manifest.Id}' failed to load: {ex.Message}");
        }
        RepublishLoaded();
    }

    private void UnloadRuntime(string id)
    {
        LuaPluginRuntime? rt = _runtimes.FirstOrDefault(r => r.Id == id);
        if (rt is null) return;
        _runtimes.Remove(rt);
        rt.Dispose();                 // disposes its watches/timers/rules/hooks (on the loop)
        _dropPanels?.Invoke(id);      // host removes the plugin's HUD panels
        RepublishLoaded();
    }

    private void RepublishLoaded() => _loadedIds = _runtimes.Select(r => r.Id).ToArray();

    // ---- lifecycle (call on the loop thread) ---------------------------------

    /// <summary>Dispose + re-run a plugin's entry script (edit-then-reload). Loop-thread only.</summary>
    public void Reload(string id)
    {
        PluginDescriptor? d = _descriptors.FirstOrDefault(x => x.Manifest.Id == id);
        if (d is null) return;
        UnloadRuntime(id);
        LoadOne(d);
    }

    /// <summary>Unload a plugin without reloading. Loop-thread only.</summary>
    public void Disable(string id)
    {
        if (_runtimes.Any(r => r.Id == id)) { UnloadRuntime(id); _report($"disabled plugin '{id}'"); }
    }

    /// <summary>Load a plugin that isn't currently loaded. Loop-thread only.</summary>
    public void Enable(string id)
    {
        if (_runtimes.Any(r => r.Id == id)) return;
        PluginDescriptor? d = _descriptors.FirstOrDefault(x => x.Manifest.Id == id);
        if (d is not null) LoadOne(d);
    }

    /// <summary>Snapshot of discovered plugins + their loaded state, for the manager UI.
    /// Reads only fixed descriptors + the volatile loaded-ids snapshot (UI-thread safe).</summary>
    public IReadOnlyList<PluginInfo> ListPlugins()
    {
        var loaded = new HashSet<string>(_loadedIds, StringComparer.Ordinal);
        return _descriptors
            .Select(d => new PluginInfo(d.Manifest.Id, d.Manifest.Name, d.Manifest.Version, loaded.Contains(d.Manifest.Id)))
            .ToList();
    }

    /// <summary>Run a server output line through every plugin (onLine hooks + triggers) and
    /// fold their gag/rewrite decisions: returns the line to display (possibly rewritten),
    /// or null to gag it. Also dispatches the prompt hook for prompt lines. Set this as the
    /// session's <c>LineDisplayFilter</c>.</summary>
    public Line? ProcessLine(Line line)
    {
        if (line.IsPrompt) DispatchPrompt();

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
    public void InvokeAction(string pluginId, string actionId)
    {
        for (int i = 0; i < _runtimes.Count; i++)
            if (_runtimes[i].Id == pluginId) { _runtimes[i].InvokeAction(actionId); return; }
    }

    public void Dispose()
    {
        foreach (LuaPluginRuntime r in _runtimes) r.Dispose();
        _runtimes.Clear();
        RepublishLoaded();
    }
}

/// <summary>A discovered plugin's identity + loaded state, for the plugins-manager UI.</summary>
public readonly record struct PluginInfo(string Id, string Name, string Version, bool Loaded);
