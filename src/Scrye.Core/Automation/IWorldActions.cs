using Scrye.Core.Text;

namespace Scrye.Core.Automation;

/// <summary>
/// The actions the automation engine can perform when a rule fires. Implemented
/// by <c>MudSession</c>; kept in Core (UI-free) so the engine never references
/// the session directly or the scripting layer.
/// </summary>
public interface IWorldActions
{
    /// <summary>Send a line to the MUD.</summary>
    void Send(string text);

    /// <summary>Echo a line into the local output.</summary>
    void Echo(string text);

    string? GetVariable(string name);
    void SetVariable(string name, string value);

    /// <summary>Invoke a named script function with the match's wildcards.</summary>
    void CallScript(string function, IReadOnlyList<string> wildcards);

    /// <summary>Route the line currently being processed to a named capture pane.
    /// Default no-op so headless/test implementations need not care.</summary>
    void Capture(string pane) { }

    /// <summary>Hide the line currently being processed from the main output.
    /// Default no-op so headless/test implementations need not care.</summary>
    void GagLine() { }

    /// <summary>Raise a notification for the line currently being processed
    /// (toast/flash — presentation decided by the UI). Default no-op.</summary>
    void Notify() { }

    /// <summary>Play a sound ("beep", absolute path, or sounds-folder file name). Default no-op.</summary>
    void PlaySound(string sound) { }

    /// <summary>Recolour a plain-text range of the line currently being processed (highlight
    /// triggers). <paramref name="start"/>/<paramref name="length"/> index the line's plain
    /// text; a null colour leaves that channel unchanged. Default no-op so headless/test
    /// implementations need not care.</summary>
    void Highlight(Rgb? fore, Rgb? back, int start, int length) { }
}
