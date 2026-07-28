namespace Scrye.Scripting;

/// <summary>
/// The curated <c>world.*</c> surface exposed to scripts — a slim, modern facade
/// over a MudSession, NOT a 1:1 port of MUSHclient's 447 functions. Grows
/// deliberately as milestones land. The script host binds an implementation of
/// this to the Lua global <c>world</c>.
/// </summary>
public interface IWorldApi
{
    void Send(string text);
    void Note(string text);
    string? GetVariable(string name);
    void SetVariable(string name, string value);
    // int AddTrigger(...), bool DeleteTrigger(...), etc. — later milestones.
}
