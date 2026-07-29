using Scrye.Core.Plugins;

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

    public void DispatchLine(string line)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchLine(line);
    }

    public void DispatchGmcp(string package, string json)
    {
        for (int i = 0; i < _runtimes.Count; i++) _runtimes[i].DispatchGmcp(package, json);
    }

    public void Dispose()
    {
        foreach (LuaPluginRuntime r in _runtimes) r.Dispose();
        _runtimes.Clear();
    }
}
