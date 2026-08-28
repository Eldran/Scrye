namespace Scrye.Core.Text;

[Flags]
public enum RunFlags
{
    None      = 0,
    Bold      = 1 << 0,
    Underline = 1 << 1,
    Italic    = 1 << 2,
    Blink     = 1 << 3,
    Inverse   = 1 << 4,
    Strikeout = 1 << 5,
}

/// <summary>A clickable action attached to a run of text (MXP &lt;SEND&gt;/&lt;A&gt;, or an
/// auto-detected URL). <see cref="IsUrl"/>: open in browser; otherwise <see cref="Action"/>
/// is a command to send to the MUD (<see cref="Prompt"/> = put it in the input box instead).
/// <see cref="RightAction"/> (markup <c>rclick=</c>, API 1.16) is a SECOND command for the
/// right button; null means the right button does nothing on this run. An empty
/// <see cref="Action"/> with a RightAction is legal: the run is right-click-only.</summary>
public sealed record LinkInfo(string Action, bool IsUrl, bool Prompt = false, string? Hint = null,
                              string? RightAction = null);

/// <summary>A contiguous run of text sharing one style. Immutable — the value type
/// the renderer consumes. A <see cref="Line"/> is a sequence of these.
/// <see cref="Link"/> is non-null when the run is part of a clickable MXP link.</summary>
public readonly record struct StyledRun(string Text, Rgb Fore, Rgb Back, RunFlags Flags, LinkInfo? Link = null);
