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
    private readonly List<LuaPluginRuntime> _runtimes = new();

    /// <summary>Ids of the plugins that loaded successfully.</summary>
    public IReadOnlyList<string> LoadedIds => _runtimes.Select(r => r.Id).ToList();
    public int Count => _runtimes.Count;

    /// <param name="plugins">Descriptors to load (already filtered by MUD/enabled).</param>
    /// <param name="host">Session bridge the plugins act through.</param>
    /// <param name="report">Status sink (loaded / failed), shown in the world output.</param>
    public PluginManager(IReadOnlyList<PluginDescriptor> plugins, IPluginHost host, Action<string> report)
    {
        foreach (PluginDescriptor d in plugins)
        {
            try
            {
                var runtime = new LuaPluginRuntime(d, host);
                runtime.Load();
                _runtimes.Add(runtime);
                report($"loaded plugin '{d.Manifest.Id}' v{d.Manifest.Version}");
            }
            catch (Exception ex)
            {
                report($"plugin '{d.Manifest.Id}' failed to load: {ex.Message}");
            }
        }
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

    public void Dispose()
    {
        foreach (LuaPluginRuntime r in _runtimes) r.Dispose();
        _runtimes.Clear();
    }
}
