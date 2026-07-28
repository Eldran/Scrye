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
}

/// <summary>A contiguous run of text sharing one style. Immutable — the value type
/// the renderer consumes. A <see cref="Line"/> is a sequence of these.</summary>
public readonly record struct StyledRun(string Text, Rgb Fore, Rgb Back, RunFlags Flags);
