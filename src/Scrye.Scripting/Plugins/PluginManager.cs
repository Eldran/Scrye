using System.Diagnostics;
using Scrye.Core.Plugins;
using Scrye.Core.Text;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Loads a set of plugins for a session and fans session events out to all of them.
/// Discovery is the caller's job (<see cref="PluginCatalog"/>); this owns the loaded
/// <see cref="IPluginRuntime"/>s (native Lua 5.4 or JS, chosen per-manifest) and their lifecycle. A plugin that throws on load is
/// reported and skipped — one bad plugin never blocks the others.
///
/// <para>Two gates sit in front of a load: the plugin's declared <c>requires.scryeApi</c> range
/// must admit <see cref="ScryeApi.Current"/>, and a plugin quarantined by
/// <see cref="PluginDiagnostics"/> for repeated failures stays unloaded until the user explicitly
/// reloads or re-enables it. Both refuse loudly rather than silently — a plugin that isn't
/// running should never be a mystery.</para>
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
    private readonly PluginDiagnostics _diagnostics;
    // Authoritative opt-in set: the plugins this world should load. Seeded from the
    // character's profile; Enable/Disable mutate it and persist via _persistEnable.
    private readonly HashSet<string> _enabled;

    // Immutable snapshots republished on every set-change (always on the loop / pre-loop) so the
    // UI can read plugin state without touching the mutable _runtimes / _descriptors.
    private volatile string[] _loadedIds = Array.Empty<string>();
    private volatile PluginInfo[] _info = Array.Empty<PluginInfo>();

    public IReadOnlyList<string> LoadedIds => _loadedIds;
    public int Count => _runtimes.Count;

    /// <summary>Per-plugin cost and failure accounting. Mutated on the loop; read via
    /// <see cref="PluginDiagnostics.Snapshot"/> from the UI.</summary>
    public PluginDiagnostics Diagnostics => _diagnostics;

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
        _diagnostics = new PluginDiagnostics(report);
        // Load only the opted-in plugins (that are actually present in the catalogue).
        foreach (PluginDescriptor d in _descriptors)
            if (_enabled.Contains(d.Manifest.Id)) LoadOne(d);
        Republish();
    }

    private void LoadOne(PluginDescriptor d)
    {
        // API gate first: refusing here turns "my plugin does nothing and prints a weird Lua
        // error" into one sentence naming the actual problem.
        if (!d.IsApiCompatible(out string why))
        {
            _report($"plugin '{d.Manifest.Id}' was not loaded because {why}");
            Republish();
            return;
        }
        // A plugin quarantined this session stays down until the user asks for it back.
        if (_diagnostics.IsQuarantined(d.Manifest.Id))
        {
            _report($"plugin '{d.Manifest.Id}' is quarantined after repeated errors — press Reload to try again");
            Republish();
            return;
        }

        try
        {
            IPluginRuntime runtime = PluginRuntimeFactory.Create(d, _host, _diagnostics);
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
        _diagnostics.Publish();
        _info = _descriptors
            .Select(d => new PluginInfo(d.Manifest.Id, d.Manifest.Name, d.Manifest.Version,
                                        loaded.Contains(d.Manifest.Id), IsRemovable(d),
                                        d.Permissions, d.Manifest.Requires?.ScryeApi,
                                        d.IsApiCompatible(out string why) ? null : why,
                                        _runtimes.FirstOrDefault(r => r.Id == d.Manifest.Id)?.EngineName,
                                        d.Manifest.Panes))
            .ToArray();
    }

    // ---- lifecycle (call on the loop thread) ---------------------------------

    /// <summary>Dispose + re-run a plugin's entry script (edit-then-reload). Loop-thread only.
    /// Only meaningful for an enabled plugin; does nothing for one that isn't loaded.</summary>
    public void Reload(string id)
    {
        PluginDescriptor? d = _descriptors.FirstOrDefault(x => x.Manifest.Id == id);
        if (d is null) return;
        // A quarantined plugin has no runtime, so the old "not loaded → do nothing" guard would
        // make Reload the one button that cannot rescue it. Explicit reload is the user saying
        // "I fixed the script", so wipe its history and give it a clean run.
        bool quarantined = _diagnostics.IsQuarantined(id);
        if (!quarantined && _runtimes.All(r => r.Id != id)) return;
        UnloadRuntime(id);
        _diagnostics.Reset(id);
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
        _diagnostics.Reset(id);   // enabling is an explicit request; clear any quarantine
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
            // This is the hot path — every plugin, every line, on the session loop. Timing is a
            // Stopwatch timestamp pair (no allocation, no syscall on the platforms we ship), and
            // the accounting itself only does real work on the rare slow/failed call.
            long t0 = Stopwatch.GetTimestamp();
            (bool g, string? rw) = _runtimes[i].ProcessLine(text);
            _diagnostics.RecordCall(_runtimes[i].Id, Stopwatch.GetTimestamp() - t0);
            if (g) gag = true;
            if (rw is not null) rewrite = rw;
        }
        DrainQuarantine();
        if (gag) return null;
        return rewrite is not null ? Line.FromText(rewrite) : line;
    }

    /// <summary>
    /// Unload anything the diagnostics quarantined during a dispatch. Called <b>after</b> a
    /// dispatch loop, never inside one: unloading mutates <see cref="_runtimes"/>, which the loop
    /// is indexing, and a plugin failing its tenth time mid-line would otherwise shift every
    /// later plugin's index and skip one.
    /// </summary>
    private void DrainQuarantine()
    {
        IReadOnlyList<string> ids = _diagnostics.TakeQuarantined();
        for (int i = 0; i < ids.Count; i++) UnloadRuntime(ids[i]);
    }

    /// <summary>Run user input through every plugin's aliases: returns the command to
    /// process (possibly rewritten), or null if a plugin consumed it. Set this as the
    /// session's <c>InputFilter</c>.</summary>
    public string? ProcessInput(string text)
    {
        string current = text;
        bool consumedByPlugin = false;
        for (int i = 0; i < _runtimes.Count; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            (bool consumed, string? rewrite) = _runtimes[i].ProcessInput(current);
            _diagnostics.RecordCall(_runtimes[i].Id, Stopwatch.GetTimestamp() - t0);
            if (consumed) { consumedByPlugin = true; break; }
            if (rewrite is not null) current = rewrite;
        }
        DrainQuarantine();
        return consumedByPlugin ? null : current;
    }

    public void DispatchChannel(string channel, string message)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchChannel(channel, message);
        DrainQuarantine();
    }

    /// <summary>Chat relayed in from another open world (API 1.10). Fans out to every plugin's
    /// <c>scrye.onRelay</c>; nothing here reaches <c>onChannel</c>, on purpose.</summary>
    public void DispatchRelay(string sourceWorld, string channel, string message)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchRelay(sourceWorld, channel, message);
        DrainQuarantine();
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchGmcp(package, json);
        DrainQuarantine();
    }

    /// <summary>Advance every plugin's timers (fed from the session's per-second tick).</summary>
    public void Tick(double dtSeconds)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].Tick(dtSeconds);
        DrainQuarantine();
    }

    public void DispatchConnect()    { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchConnect(); DrainQuarantine(); }
    public void DispatchDisconnect() { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchDisconnect(); DrainQuarantine(); }
    public void DispatchPrompt()     { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchPrompt(); DrainQuarantine(); }
    public void DispatchIdle()       { for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchIdle(); DrainQuarantine(); }

    /// <summary>A command went to the MUD (any origin): fire every plugin's <c>scrye.onCommand</c>
    /// hooks. Observe-only by construction — the send has already happened. Wire the session's
    /// <c>CommandSent</c> event here (API 1.6).</summary>
    public void DispatchCommand(string text)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchCommand(text);
        DrainQuarantine();
    }

    // Inter-plugin event dispatch nesting. Emits can nest (a handler may emit), and an
    // A-emits→B-emits→A cycle would otherwise recurse forever; past this depth the emit is
    // dropped with a report. Modest on purpose — a legitimate chain deeper than 8 is a design
    // smell, and each level is a full fan-out.
    private const int MaxEventDepth = 8;
    private int _eventDepth;

    /// <summary>
    /// Broadcast an inter-plugin event (<c>scrye.emit</c>) to EVERY loaded plugin's matching
    /// <c>scrye.on</c> handlers — including the emitter's own, so a plugin can use its own
    /// events without special-casing. Wire the session host's event sink here (API 1.6).
    ///
    /// <para>Deliberately does NOT drain quarantine: an emit can fire from inside a
    /// ProcessLine/Tick dispatch loop that is still indexing <see cref="_runtimes"/>, and
    /// unloading mid-loop is exactly the bug <see cref="DrainQuarantine"/>'s call sites are
    /// arranged to avoid. Anything quarantined here is drained by the next line or tick,
    /// which is at most 250 ms away.</para>
    /// </summary>
    public void DispatchPluginEvent(string sourceId, string name, string data)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (_eventDepth >= MaxEventDepth)
        {
            _report($"plugin event '{name}' from '{sourceId}' dropped: emit chains nested deeper than {MaxEventDepth} (cycle?)");
            return;
        }
        _eventDepth++;
        try
        {
            for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchPluginEvent(name, data, sourceId);
        }
        finally { _eventDepth--; }
    }

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

    /// <summary>Fire an input widget's submit callback with the entered text. Loop-thread only.</summary>
    public void InvokeSubmit(string pluginId, string actionId, string text)
    {
        IPluginRuntime? rt = _runtimes.FirstOrDefault(r => r.Id == pluginId);
        rt?.InvokeSubmit(actionId, text);
    }

    /// <summary>Fire a bound buttonrow's callback with the clicked label and its 1-based index.
    /// Loop-thread only.</summary>
    public void InvokeChoice(string pluginId, string actionId, string label, int index)
    {
        IPluginRuntime? rt = _runtimes.FirstOrDefault(r => r.Id == pluginId);
        rt?.InvokeChoice(actionId, label, index);
    }

    public void Dispose()
    {
        foreach (IPluginRuntime r in _runtimes) r.Dispose();
        _runtimes.Clear();
        Republish();
    }
}

/// <summary>
/// A discovered plugin's identity, state and declarations, for the plugins-manager UI.
/// <paramref name="Permissions"/> is what the manifest says it intends to do (declarative only —
/// see <see cref="PluginPermissions"/>), and <paramref name="IncompatibleReason"/> is non-null
/// when the plugin cannot load on this build at all, so the manager can explain itself instead of
/// just showing a plugin that never turns on. <paramref name="Engine"/> is the loaded runtime's
/// engine label (null when not loaded) — surfaced during the KeraLua soak so which engine a
/// plugin runs on is never a mystery.
/// </summary>
public readonly record struct PluginInfo(
    string Id,
    string Name,
    string Version,
    bool Loaded,
    bool Removable,
    IReadOnlyList<string> Permissions,
    string? RequiresApi,
    string? IncompatibleReason,
    string? Engine = null,
    IReadOnlyList<string>? Panes = null);
