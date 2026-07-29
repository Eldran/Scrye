namespace Scrye.Core.Events;

/// <summary>
/// What kind of thing happened on a session. The event stream is the single
/// instrumented spine everything downstream reads: the trigger timeline/debugger,
/// session record &amp; replay, the dry-run simulator, and test harnesses all
/// consume <see cref="SessionEvent"/>s rather than reaching into the session.
/// </summary>
public enum SessionEventKind
{
    /// <summary>A connection attempt started.</summary>
    Connecting,
    /// <summary>The socket connected (post-handshake transport is up).</summary>
    Connected,
    /// <summary>The connection closed or failed.</summary>
    Disconnected,

    /// <summary>A completed output line arrived from the MUD (plain text in <see cref="SessionEvent.Text"/>).</summary>
    LineReceived,
    /// <summary>A prompt line (flushed by a telnet GA/EOR rather than a newline).</summary>
    Prompt,

    /// <summary>The user submitted input (before aliases run).</summary>
    InputSubmitted,
    /// <summary>Text was sent to the MUD (post-alias, actual wire text).</summary>
    Sent,

    /// <summary>A trigger matched a line. <see cref="SessionEvent.Label"/> = trigger name, <see cref="SessionEvent.Text"/> = matched line, <see cref="SessionEvent.Detail"/> = action taken.</summary>
    TriggerMatched,
    /// <summary>An alias matched user input.</summary>
    AliasMatched,
    /// <summary>A timer fired. <see cref="SessionEvent.Label"/> = timer name, <see cref="SessionEvent.Detail"/> = action taken.</summary>
    TimerFired,

    /// <summary>A variable changed. <see cref="SessionEvent.Label"/> = name, <see cref="SessionEvent.Text"/> = new value, <see cref="SessionEvent.Detail"/> = old value.</summary>
    VariableChanged,

    /// <summary>An out-of-band GMCP message. <see cref="SessionEvent.Label"/> = package, <see cref="SessionEvent.Text"/> = json.</summary>
    Gmcp,
    /// <summary>An in-band MIP message was processed. <see cref="SessionEvent.Label"/> = id+tag, <see cref="SessionEvent.Text"/> = data.</summary>
    Mip,

    /// <summary>A script snippet ran (console or dispatch).</summary>
    ScriptRun,
    /// <summary>A script raised an error. <see cref="SessionEvent.Text"/> = message.</summary>
    ScriptError,

    /// <summary>A system/local notice was echoed to the output (not from the MUD).</summary>
    Notice,
}

/// <summary>
/// One thing that happened on a session, stamped with a monotonic sequence number
/// and a timestamp. Immutable and flat so it serializes to a single JSON line and
/// diffs cleanly in git. Payload lives in three optional string slots whose meaning
/// depends on <see cref="Kind"/> (documented on each <see cref="SessionEventKind"/>).
/// </summary>
public sealed record SessionEvent
{
    /// <summary>Monotonic per-session sequence (1-based). Total order even when timestamps tie.</summary>
    public long Seq { get; init; }

    /// <summary>When it happened (UTC).</summary>
    public DateTimeOffset TimeUtc { get; init; }

    public SessionEventKind Kind { get; init; }

    /// <summary>Primary payload: the line, the sent text, the json, a variable's new value.</summary>
    public string Text { get; init; } = "";

    /// <summary>Secondary label: a rule name, a GMCP package, a variable name. Null when N/A.</summary>
    public string? Label { get; init; }

    /// <summary>Tertiary detail: what a rule did, a variable's old value, an error location. Null when N/A.</summary>
    public string? Detail { get; init; }

    /// <summary>Compact one-line rendering for logs and the timeline.</summary>
    public override string ToString()
    {
        string head = $"#{Seq} {TimeUtc:HH:mm:ss.fff} {Kind}";
        string body = Label is null ? Text : $"[{Label}] {Text}";
        if (!string.IsNullOrEmpty(Detail)) body += $"  ({Detail})";
        return body.Length == 0 ? head : $"{head}  {body}";
    }
}
