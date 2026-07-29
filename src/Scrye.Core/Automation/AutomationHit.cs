namespace Scrye.Core.Automation;

public enum AutomationHitKind { Trigger, Alias, Timer }

/// <summary>
/// A record of one rule firing (or, in a dry run, that it <em>would</em> fire).
/// Emitted by the engine's <see cref="AutomationEngine.Hit"/> callback so the
/// session can turn it into a <c>SessionEvent</c>, and returned by
/// <see cref="AutomationEngine.Simulate"/> for the trigger debugger's dry-run.
/// </summary>
public readonly record struct AutomationHit(
    AutomationHitKind Kind,
    string Name,
    string? Group,
    string Input,
    string Action)
{
    public override string ToString() =>
        $"{Kind} '{Name}'" + (Input.Length > 0 ? $" on \"{Input}\"" : "") + $" -> {Action}";
}
