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
    private readonly Action<string, string> _print;   // (pluginId, text)

    public SessionPluginHost(MudSession session, Action<string, string> print)
    {
        _session = session;
        _actions = session;   // MudSession implements IWorldActions
        _print = print;
    }

    public void Send(string text) => _actions.Send(text);
    public void Print(string pluginId, string text) => _print(pluginId, text);

    public string? GetVariable(string name) => _actions.GetVariable(name);
    public void SetVariable(string name, string value) => _actions.SetVariable(name, value);

    public string GetState(string path) => _session.GameState.Get(path).Text;

    public IDisposable WatchState(string path, Action<string, string> onChange) =>
        _session.GameState.Watch(path, (p, v) => onChange(p, v.Text));
}
