namespace Scrye.Core.Automation;

/// <summary>Where a matched trigger/alias/timer sends its (expanded) text.</summary>
public enum SendTo
{
    /// <summary>Send to the MUD (as if typed).</summary>
    World,
    /// <summary>Echo into the output pane locally.</summary>
    Output,
    /// <summary>No text send — only run the script callback.</summary>
    Script,
    /// <summary>Store the expanded text into the named variable.</summary>
    Variable,
    /// <summary>Place the text in the input box (reserved; treated as Output for now).</summary>
    Command,

    /// <summary>
    /// Run the text through the client's own command pipeline -- plugin aliases first, then
    /// profile aliases, then the MUD if nothing claimed it -- exactly as a typed line is run.
    /// This is how a rule reaches a <em>plugin</em> command: <c>cs pause</c> means something to
    /// the chaos-sea plugin and nothing at all to the MUD, so <see cref="World"/> would send it
    /// to 3Scapes and get "Huh?".
    ///
    /// <para>It runs the pipeline, not the input box: the idle guard is not poked and the
    /// transcript records no "&gt;" line, because a rule firing is not a person at the keyboard.
    /// Nesting is capped (see <c>MudSession.SendToClient</c>) so a rule that re-triggers itself
    /// stops with a message instead of looping.</para>
    /// </summary>
    Client,
}
