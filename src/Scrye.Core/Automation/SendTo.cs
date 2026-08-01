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
}
