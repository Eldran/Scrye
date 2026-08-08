namespace Scrye.Core.Plugins;

/// <summary>
/// The capability vocabulary a plugin declares in its manifest's <c>permissions</c> array.
///
/// <para><b>What this is and is not.</b> These declarations are currently <i>informational</i> —
/// nothing in the host refuses a <c>scrye.send</c> from a plugin that didn't ask for
/// <c>commands.send</c>. They exist so a user installing a plugin can see what it intends to do
/// before enabling it, and so the enforcement point later has a vocabulary to enforce against.
/// Presenting them as a security boundary today would be a lie.</para>
///
/// <para><b>The actual sandbox.</b> Script plugins run in the host's Lua sandbox / Jint with no
/// <c>io</c>, no <c>os.execute</c> and no CLR access — that part is real and
/// enforced by the engine, not by this list. What is <i>not</i> bounded is what matters most on a
/// MUD: <c>scrye.send</c> can issue any command your character can type. A plugin cannot read
/// your files — the only thing it reads from disk is the data files it ships in its own folder,
/// which the host resolves for it (see <see cref="PluginManifest.Data"/>) — but it can drop your
/// inventory. That asymmetry is why <c>commands.send</c> is the
/// permission worth reading carefully.</para>
///
/// <para>Unknown permission strings are preserved and shown verbatim rather than dropped — a
/// plugin written for a newer Scrye should not have its intentions silently hidden from the user
/// by an older client.</para>
/// </summary>
public static class PluginPermissions
{
    public const string OutputRead = "output.read";
    public const string OutputModify = "output.modify";
    public const string CommandsSend = "commands.send";
    public const string VariablesRead = "variables.read";
    public const string VariablesWrite = "variables.write";
    public const string StateRead = "state.read";
    public const string StateWrite = "state.write";
    public const string TriggersManage = "triggers.manage";
    public const string AliasesManage = "aliases.manage";
    public const string TimersManage = "timers.manage";
    public const string StoragePrivate = "storage.private";
    public const string NotificationsShow = "notifications.show";
    public const string SoundPlay = "sound.play";
    public const string UiPanels = "ui.panels";
    public const string CaptureWrite = "capture.write";
    public const string LogWrite = "log.write";

    /// <summary>Every known permission, in the order they should be listed to a user —
    /// roughly most-consequential first, so a glance at a truncated list is still informative.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        CommandsSend, OutputModify, OutputRead, TriggersManage, AliasesManage,
        VariablesWrite, VariablesRead, StateWrite, StateRead, TimersManage,
        StoragePrivate, NotificationsShow, SoundPlay, CaptureWrite, LogWrite, UiPanels,
    };

    /// <summary>A short user-facing phrase for a permission, or null when the name is unknown
    /// (in which case callers should display the raw string).</summary>
    public static string? Describe(string permission) => permission switch
    {
        CommandsSend => "Send commands to the MUD as you",
        OutputModify => "Hide or rewrite lines before you see them",
        OutputRead => "Read everything the MUD sends",
        TriggersManage => "Add its own triggers",
        AliasesManage => "Add its own aliases (can intercept what you type)",
        VariablesWrite => "Change world variables shared with your triggers",
        VariablesRead => "Read world variables",
        StateWrite => "Publish values into the state tree",
        StateRead => "Read character and game state",
        TimersManage => "Run code on a timer",
        StoragePrivate => "Save data of its own between sessions",
        NotificationsShow => "Show notifications (and push to your phone)",
        SoundPlay => "Play sounds",
        CaptureWrite => "Route lines into capture panes",
        LogWrite => "Write to its own log file",
        UiPanels => "Add HUD panels",
        _ => null,
    };

    /// <summary>
    /// The permissions a user should think hardest about before enabling a plugin from someone
    /// they don't know. Surfaced with emphasis in the plugins manager.
    /// </summary>
    public static bool IsSensitive(string permission) =>
        permission is CommandsSend or OutputModify or AliasesManage or VariablesWrite;

    /// <summary>True when the name is one this build knows about.</summary>
    public static bool IsKnown(string permission) =>
        All.Contains(permission, StringComparer.OrdinalIgnoreCase);
}
