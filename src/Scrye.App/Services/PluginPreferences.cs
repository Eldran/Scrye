namespace Scrye.App.Services;

/// <summary>
/// Global plugin settings that a world needs at construction, before any profile of its own is
/// resolved. Same shape and the same reasoning as <see cref="InputPreferences"/>: a static set
/// from the two places the global layer is read, rather than threaded through every caller.
/// </summary>
public static class PluginPreferences
{
    /// <summary>An extra folder to discover plugins in, or null. Searched before the bundled
    /// and user folders, so a plugin here wins on an id collision — a folder you deliberately
    /// pointed the client at should beat what shipped, or pointing at it does nothing.</summary>
    public static string? ExtraRoot { get; set; }
}
