namespace Scrye.Core.Plugins;

/// <summary>
/// The capabilities the host (a live session) offers a plugin. Implemented by a
/// session bridge; consumed by the Lua runtime, which wraps these as the <c>scrye.*</c>
/// script API. Deliberately MoonSharp-free (lives in Core) so the API surface stays
/// language-agnostic — a future JavaScript host binds the same interface.
///
/// Values are exchanged as strings (Lua-friendly; numbers convert with <c>tonumber</c>).
/// Everything runs on the session loop thread, matching trigger/alias script execution.
/// </summary>
public interface IPluginHost
{
    /// <summary>Send a line to the MUD.</summary>
    void Send(string text);

    /// <summary>Echo a line to local output, tagged with the plugin id.</summary>
    void Print(string pluginId, string text);

    string? GetVariable(string name);
    void SetVariable(string name, string value);

    /// <summary>Current value of a state path (e.g. "character.health.current"), or "" if unset.</summary>
    string GetState(string path);

    /// <summary>Watch a state path/subtree; callback gets (changedPath, valueText).
    /// Returns an <see cref="IDisposable"/> the runtime disposes when the plugin unloads.</summary>
    IDisposable WatchState(string path, Action<string, string> onChange);

    /// <summary>Contribute a declarative HUD panel. The host renders it and keeps its
    /// bound widgets in sync with state (Foundation D).</summary>
    void AddPanel(string pluginId, PanelSpec panel);
}
