namespace Scrye.Scripting.Plugins;

/// <summary>
/// The language-agnostic contract a single loaded plugin exposes to
/// <see cref="PluginManager"/>. Implemented by <see cref="LuaPluginRuntime"/> (MoonSharp)
/// and <see cref="JsPluginRuntime"/> (Jint). Everything is called on the session loop
/// thread, so a runtime's script engine is never re-entered concurrently.
/// </summary>
public interface IPluginRuntime : IDisposable
{
    string Id { get; }

    /// <summary>Read and run the entry script (registers the plugin's hooks). Throws on error.</summary>
    void Load();

    /// <summary>Run an output line through <c>onLine</c> hooks + plugin triggers.
    /// Returns whether to gag the displayed line, and a rewritten string if produced.</summary>
    (bool Gag, string? Rewrite) ProcessLine(string text);

    /// <summary>Run user input through the plugin's aliases. Returns (consumed, rewrite).</summary>
    (bool Consumed, string? Rewrite) ProcessInput(string text);

    void DispatchGmcp(string package, string json);
    void Tick(double dtSeconds);
    void DispatchConnect();
    void DispatchDisconnect();
    void DispatchPrompt();

    /// <summary>Invoke a panel-button callback by its action id.</summary>
    void InvokeAction(string actionId);
}
