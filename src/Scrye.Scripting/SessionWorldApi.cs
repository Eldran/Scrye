using Scrye.Core.Automation;
using Scrye.Core.Session;

namespace Scrye.Scripting;

/// <summary>
/// Binds the <see cref="IWorldApi"/> script surface to a live <see cref="MudSession"/>.
/// Actions (send/echo/variables) go through the session's <see cref="IWorldActions"/>
/// so they run on the session loop; rule registration goes to its automation engine.
/// </summary>
public sealed class SessionWorldApi : IWorldApi
{
    private readonly MudSession _session;
    private readonly IWorldActions _actions;

    public SessionWorldApi(MudSession session)
    {
        _session = session;
        _actions = session;   // MudSession implements IWorldActions
    }

    public void Send(string text) => _actions.Send(text);
    public void Note(string text) => _actions.Echo(text);

    public string? GetVariable(string name) => _actions.GetVariable(name);
    public void SetVariable(string name, string value) => _actions.SetVariable(name, value);

    public void AddTrigger(string name, string pattern, string send) =>
        _session.Automation.AddTrigger(new TriggerDef { Name = name, Pattern = pattern, Send = send });

    public void AddAlias(string name, string pattern, string send) =>
        _session.Automation.AddAlias(new AliasDef { Name = name, Pattern = pattern, Send = send });

    public bool DeleteTrigger(string name) => _session.Automation.RemoveTrigger(name);
    public bool DeleteAlias(string name) => _session.Automation.RemoveAlias(name);
}
