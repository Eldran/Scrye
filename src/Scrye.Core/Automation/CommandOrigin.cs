namespace Scrye.Core.Automation;

/// <summary>Where a command entered the command pipeline.</summary>
public enum CommandSource
{
    /// <summary>Typed at the desktop, fired by a macro, or expanded from an alias.
    /// Fully trusted — this is the user at the keyboard.</summary>
    Local,

    /// <summary>Arrived over the wire from a paired companion device.</summary>
    Companion,
}

/// <summary>
/// A command's origin plus the capabilities that origin carries. Passed into
/// <c>WorldViewModel.SubmitText</c> so the privilege decision is made once, at the single
/// point where input enters the pipeline (companion design §7.3).
///
/// <para>The per-device permission lookup belongs to the companion server, not to the view
/// model: the server knows which paired device sent the frame, and expresses the result
/// here. The view model only enforces.</para>
/// </summary>
public readonly record struct CommandOrigin(CommandSource Source, bool MayRunScripts)
{
    /// <summary>The user at the desktop. Everything is permitted.</summary>
    public static readonly CommandOrigin Local = new(CommandSource.Local, MayRunScripts: true);

    /// <summary>A paired companion device. <paramref name="mayRunScripts"/> comes from that
    /// device's permission set and is <c>false</c> unless explicitly granted.</summary>
    public static CommandOrigin Companion(bool mayRunScripts = false) =>
        new(CommandSource.Companion, mayRunScripts);
}

/// <summary>The outcome of submitting a command. Rejections are reported rather than
/// silently dropped so the companion server can answer the device with an error frame
/// instead of leaving it wondering whether the socket died.</summary>
public enum CommandSubmitResult
{
    /// <summary>Handled — sent to the MUD, or consumed by a client command.</summary>
    Accepted,

    /// <summary>A <c>/</c> Lua console command from an origin without the scripting
    /// capability. Nothing was executed, echoed, or sent.</summary>
    RejectedScriptingNotPermitted,
}
