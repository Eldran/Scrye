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

    // Simplified rule registration from script (full option set comes later):
    void AddTrigger(string name, string pattern, string send);
    void AddAlias(string name, string pattern, string send);
    bool DeleteTrigger(string name);
    bool DeleteAlias(string name);
}
