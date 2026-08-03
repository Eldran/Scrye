using Scrye.Companion.Protocol;
using Scrye.Core.Automation;

namespace Scrye.Companion.Server.Sessions;

/// <summary>
/// Everything the companion server needs from the desktop, and nothing else.
///
/// <para>This interface exists to solve the threading problem in design §4.1. Three
/// contracts meet in the server and none of them is a Kestrel request thread:
/// <c>StateStore</c> is single-threaded on the session mailbox loop, <c>ScrollbackBuffer</c>
/// and <c>Flush()</c> are UI-thread, and socket writes happen on thread-pool threads. So
/// the server never reaches into those types. The app pushes into the hub from the threads
/// that own them, and pulls through this interface — whose implementation is responsible
/// for marshalling.</para>
///
/// <para>It also keeps the dependency arrow pointing one way: the server does not
/// reference Scrye.App, so it can be built and tested against a fake.</para>
/// </summary>
public interface ICompanionSessionSource
{
    /// <summary>Currently open worlds. Called on the UI thread by the implementation's own
    /// marshalling, or from a snapshot the implementation keeps current.</summary>
    IReadOnlyList<SessionStateMessage> GetSessions();

    /// <summary>Submit a command as if typed, honouring <paramref name="origin"/>'s
    /// capabilities. Implementations must marshal to the UI thread and call
    /// <c>WorldViewModel.SubmitText</c> — never bypass the command pipeline, or aliases,
    /// triggers, highlights and logging all stop applying (§4).</summary>
    ValueTask<CommandSubmitResult> SubmitCommandAsync(string sessionId, string command, CommandOrigin origin);

    /// <summary>Fire a HUD panel button's plugin callback. Implementations must marshal
    /// onto the session loop before touching plugin script — the desktop already does this
    /// for local clicks. Returns false when the panel or plugin is unknown.</summary>
    ValueTask<bool> InvokeHudActionAsync(string sessionId, string pluginId, string actionId);

    /// <summary>Fire an <c>input</c> widget's submit callback with the entered text.</summary>
    ValueTask<bool> InvokeHudSubmitAsync(string sessionId, string pluginId, string actionId, string text);

    /// <summary>Fire a <c>colorgrid</c> cell callback. Coordinates are meaningful only to the
    /// plugin, which maps them back to its own data.</summary>
    ValueTask<bool> InvokeHudCellAsync(string sessionId, string pluginId, string actionId,
                                       int col, int row, string ch);

    /// <summary>Everything a client needs to rebuild from scratch: the session state, the
    /// tail of scrollback, the whole state tree and the current HUD panels. Used when a
    /// resume gap is too large to replay (§6).</summary>
    ValueTask<SnapshotMessage?> GetSnapshotAsync(string sessionId, int maxLines);

    /// <summary>Replay the lines after <paramref name="afterSequence"/>, or null when that
    /// point has been trimmed out of scrollback and the caller must snapshot instead.
    /// Implementations should use <c>ScrollbackBuffer.CanReplayFrom</c> to decide, rather
    /// than comparing indices — after a trim, index and sequence differ (§6).</summary>
    ValueTask<OutputBatchMessage?> TryReplayAsync(string sessionId, long afterSequence);
}
