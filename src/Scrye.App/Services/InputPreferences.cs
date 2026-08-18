namespace Scrye.App.Services;

/// <summary>
/// How the command box behaves, as chosen in Global Settings.
///
/// <para>A static, and set from exactly the two places the other global appearance settings are
/// applied (startup and a Settings save) — the same shape as
/// <see cref="ThemeService.ApplyAnsiPalette"/>. It is read on every Enter in every world tab,
/// and world tabs are created long after the setting is loaded, so handing it to each one
/// would mean remembering to hand it to the next one too.</para>
/// </summary>
public static class InputPreferences
{
    /// <summary>Leave the command in the box after Enter, selected, so Enter alone repeats it
    /// and typing replaces it. Off unless asked for: clearing is what someone who has not
    /// heard of this expects, and silently keeping the last command is a good way to send it
    /// twice by accident.</summary>
    public static bool KeepAfterSend { get; set; }
}
