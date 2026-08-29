namespace Scrye.Scripting.Plugins;

/// <summary>
/// The language-agnostic contract a single loaded plugin exposes to
/// <see cref="PluginManager"/>. Implemented by <c>KeraLuaPluginRuntime</c> (native
/// Lua 5.4) and <see cref="JsPluginRuntime"/> (Jint). Everything is called on the session
/// loop thread, so a runtime's script engine is never re-entered concurrently.
/// </summary>
public interface IPluginRuntime : IDisposable
{
    string Id { get; }

    /// <summary>Human-readable engine label for the plugins manager ("Lua 5.4 (native)",
    /// "JS (Jint)") — which engine a plugin runs on should never be a mystery. Default
    /// empty so runtime implementations outside this repo still compile.</summary>
    string EngineName => "";

    /// <summary>Read and run the entry script (registers the plugin's hooks). Throws on error.</summary>
    void Load();

    /// <summary>Run an output line through <c>onLine</c> hooks + plugin triggers.
    /// Returns whether to gag the displayed line, and a rewritten string if produced.</summary>
    (bool Gag, string? Rewrite) ProcessLine(string text);

    /// <summary>Run user input through the plugin's aliases. Returns (consumed, rewrite).</summary>
    (bool Consumed, string? Rewrite) ProcessInput(string text);

    void DispatchGmcp(string package, string json);

    /// <summary>A structured MIP chat message: (channel, message); tells use channel "Tell".</summary>
    void DispatchChannel(string channel, string message);

    /// <summary>Chat RELAYED from another open world, because this world's tab is the one in
    /// front: (sourceWorld, channel, message). Fires this world's <c>scrye.onRelay</c> hooks.
    ///
    /// <para>Deliberately NOT <see cref="DispatchChannel"/>. A plugin that treated foreign chat
    /// as its own would notify a second time (the source world already did), write another MUD's
    /// lines into its own log, and fold them into its own history. A separate hook lets a chat
    /// pane show the line — which is the point — while leaving all of that alone. Default no-op
    /// (API 1.10), so a runtime that predates it still compiles.</para></summary>
    void DispatchRelay(string sourceWorld, string channel, string message) { }
    void Tick(double dtSeconds);
    void DispatchConnect();
    void DispatchDisconnect();
    void DispatchPrompt();

    /// <summary>
    /// The idle guard fired: nobody has touched the client for the configured stretch, so
    /// anything this plugin is driving should stop. Default no-op so a runtime written before
    /// the guard existed still compiles.
    /// </summary>
    void DispatchIdle() { }

    /// <summary>A command line went out to the MUD (any origin — typed, macro, sequence,
    /// trigger, another plugin). Fires the plugin's <c>scrye.onCommand</c> hooks. Observe-only:
    /// nothing a hook returns can veto or rewrite the send, which already happened.
    /// Default no-op (API 1.6).</summary>
    void DispatchCommand(string text) { }

    /// <summary>An inter-plugin event (<c>scrye.emit</c>): fire this plugin's matching
    /// <c>scrye.on(name, fn)</c> handlers with (data, name, sourceId). Default no-op (API 1.6).</summary>
    void DispatchPluginEvent(string name, string data, string sourceId) { }

    /// <summary>Invoke a panel-button callback by its action id.</summary>
    void InvokeAction(string actionId);

    /// <summary>Invoke a colorgrid cell-click (or right-click) callback with the clicked cell
    /// (col, row, char). Since API 1.18 the callback may RETURN a context menu — a list of
    /// <see cref="Scrye.Core.Plugins.MenuEntry"/> the host shows at the pointer; null (the
    /// overwhelmingly common case, and this default) means "no menu", which is every pre-1.18
    /// callback's behaviour.</summary>
    IReadOnlyList<Scrye.Core.Plugins.MenuEntry>? InvokeCellAction(string actionId, int col, int row, string ch) => null;

    /// <summary>Invoke an input widget's submit callback with the entered text.</summary>
    void InvokeSubmit(string actionId, string text) { }

    /// <summary>Invoke a choice-shaped callback with the clicked label and its 1-based index:
    /// a bound buttonrow's button, a list/table row click (1.15), or a row's context callback
    /// (<c>onRowMenu</c>, 1.18) — the last of which may RETURN a context menu exactly as
    /// <see cref="InvokeCellAction"/> may. Default no-op so a runtime that predates dynamic
    /// buttonrows still compiles.</summary>
    IReadOnlyList<Scrye.Core.Plugins.MenuEntry>? InvokeChoice(string actionId, string label, int index) => null;
}
