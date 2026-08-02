using Scrye.Core.Automation;
using Scrye.Core.Plugins;
using Scrye.Core.Session;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Binds <see cref="IPluginHost"/> to a live <see cref="MudSession"/>. Actions go
/// through the session's <see cref="IWorldActions"/> (so they run on the loop);
/// state reads/watches hit the session's <see cref="MudSession.GameState"/>; local
/// output is routed through the supplied print sink (the App turns it into an output line).
/// </summary>
public sealed class SessionPluginHost : IPluginHost
{
    private readonly MudSession _session;
    private readonly IWorldActions _actions;
    private readonly Action<string, string> _print;             // (pluginId, text)
    private readonly Action<string, PanelSpec> _addPanel;       // (pluginId, panel) → App builds the HUD VM
    private readonly PluginDataStore? _data;                    // persistent scrye.store backing (optional)

    public SessionPluginHost(MudSession session, Action<string, string> print, Action<string, PanelSpec> addPanel,
                             PluginDataStore? data = null)
    {
        _session = session;
        _actions = session;   // MudSession implements IWorldActions
        _print = print;
        _addPanel = addPanel;
        _data = data;
    }

    public void Send(string text) => _actions.Send(text);
    public void Print(string pluginId, string text) => _print(pluginId, text);

    public string? GetVariable(string name) => _actions.GetVariable(name);
    public void SetVariable(string name, string value) => _actions.SetVariable(name, value);

    public string GetState(string path) => _session.GameState.Get(path).Text;

    public void SetState(string path, string value) =>
        _session.GameState.Set(path, Scrye.Core.State.StateValue.Str(value));

    public IDisposable WatchState(string path, Action<string, string> onChange) =>
        _session.GameState.Watch(path, (p, v) => onChange(p, v.Text));

    public void AddPanel(string pluginId, PanelSpec panel) => _addPanel(pluginId, panel);

    public void Capture(string pluginId, string pane, string text) =>
        _session.RoutePane(pane, Scrye.Core.Text.Line.FromText(text));

    public void PlaySound(string sound) => _session.RequestSound(sound);

    public void Notify(string pluginId, string text) =>
        _session.RequestNotify(Scrye.Core.Text.Line.FromText($"[{pluginId}] {text}"));

    // ---- per-plugin log file (scrye.log) --------------------------------------
    // Appends to %APPDATA%/Scrye/logs/plugins/<id>.log. Best-effort: a logging
    // failure must never take down a plugin, so all IO errors are swallowed.
    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "logs", "plugins");

    public void Log(string pluginId, string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDir);
            string safe = string.Join("_", pluginId.Split(System.IO.Path.GetInvalidFileNameChars()));
            System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, safe + ".log"), text + Environment.NewLine);
        }
        catch { /* logging is best-effort; never surface IO errors to the plugin */ }
    }

    // ---- persistent per-plugin storage (scrye.store) --------------------------

    public string? StoreGet(string pluginId, string key) => _data?.Get(pluginId, key);
    public void StoreSet(string pluginId, string key, string value) => _data?.Set(pluginId, key, value);
    public void StoreDelete(string pluginId, string key) => _data?.Delete(pluginId, key);
    public string[] StoreKeys(string pluginId) => _data?.Keys(pluginId) ?? Array.Empty<string>();
}
