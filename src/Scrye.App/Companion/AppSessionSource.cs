using Avalonia.Threading;
using Scrye.App.ViewModels;
using Scrye.Companion.Protocol;
using Scrye.Companion.Server.Sessions;
using Scrye.Core.Automation;
using Scrye.Core.Plugins;
using Scrye.Core.State;

namespace Scrye.App.Companion;

/// <summary>
/// The desktop side of <see cref="ICompanionSessionSource"/> — the only place the companion
/// server is allowed to touch app state.
///
/// <para><b>Every member marshals to the UI thread.</b> These are called from Kestrel
/// threads, while <c>ScrollbackBuffer</c>, <c>HudViewModel.Panels</c> and the view models are
/// UI-thread-owned and <c>StateStore</c> belongs to the session loop (companion design §4.1).
/// The marshalling lives here, once, so the server never has to think about it.</para>
/// </summary>
public sealed class AppSessionSource : ICompanionSessionSource
{
    private readonly MainWindowViewModel _main;

    public AppSessionSource(MainWindowViewModel main) => _main = main;

    public IReadOnlyList<SessionStateMessage> GetSessions() =>
        Dispatcher.UIThread.CheckAccess()
            ? Snapshot()
            : Dispatcher.UIThread.InvokeAsync<IReadOnlyList<SessionStateMessage>>(Snapshot).GetAwaiter().GetResult();

    private IReadOnlyList<SessionStateMessage> Snapshot()
    {
        var list = new List<SessionStateMessage>(_main.Worlds.Count);
        foreach (WorldViewModel w in _main.Worlds)
            list.Add(Describe(w));
        return list;
    }

    internal static SessionStateMessage Describe(WorldViewModel w) =>
        new(w.SessionId,
            w.ConnState == Core.Model.ConnectionState.Connected,
            w.Ref?.Character ?? w.Title,
            w.Ref?.Mud ?? w.Title);

    public async ValueTask<CommandSubmitResult> SubmitCommandAsync(
        string sessionId, string command, CommandOrigin origin) =>
        await Dispatcher.UIThread.InvokeAsync<CommandSubmitResult>(() =>
        {
            WorldViewModel? w = Find(sessionId);
            // Route through SubmitText, never MudSession.Submit — bypassing it would skip
            // aliases, triggers, highlights and logging, which is the whole point of §4.
            return w is null ? CommandSubmitResult.Accepted : w.SubmitText(command, origin);
        });

    public async ValueTask<bool> InvokeHudActionAsync(string sessionId, string pluginId, string actionId) =>
        await Dispatcher.UIThread.InvokeAsync<bool>(() =>
        {
            WorldViewModel? w = Find(sessionId);
            if (w is null) return false;
            // Same entry point a desktop click uses, which already posts onto the session
            // loop before touching plugin Lua — plugin script is loop-thread-only.
            return w.InvokeHudAction(pluginId, actionId);
        });

    public async ValueTask<bool> InvokeHudSubmitAsync(string sessionId, string pluginId, string actionId, string text) =>
        await Dispatcher.UIThread.InvokeAsync<bool>(() =>
            Find(sessionId)?.InvokeHudSubmit(pluginId, actionId, text) ?? false);

    public async ValueTask<bool> InvokeHudCellAsync(string sessionId, string pluginId, string actionId,
                                                    int col, int row, string ch) =>
        await Dispatcher.UIThread.InvokeAsync<bool>(() =>
            Find(sessionId)?.InvokeHudCell(pluginId, actionId, col, row, ch) ?? false);

    public async ValueTask<OutputBatchMessage?> TryReplayAsync(string sessionId, long afterSequence) =>
        await Dispatcher.UIThread.InvokeAsync<OutputBatchMessage?>(() =>
        {
            WorldViewModel? w = Find(sessionId);
            if (w is null) return null;

            // Returning null is the SAFE answer: the hub then sends a snapshot. Serving a
            // partial replay would silently skip lines the client never saw (§6).
            if (!w.Scrollback.CanReplayFrom(afterSequence)) return null;

            IReadOnlyList<Core.Text.Line> lines = w.Scrollback.LinesAfter(afterSequence);
            var builder = new OutputBatchBuilder();
            builder.AddRange(lines, afterSequence + 1);
            return builder.Build(sessionId);
        });

    public async ValueTask<SnapshotMessage?> GetSnapshotAsync(string sessionId, int maxLines) =>
        await Dispatcher.UIThread.InvokeAsync<SnapshotMessage?>(() =>
        {
            WorldViewModel? w = Find(sessionId);
            if (w is null) return null;

            var output = new OutputBatchBuilder();
            int take = Math.Min(maxLines, w.Scrollback.Count);
            int start = w.Scrollback.Count - take;
            for (int i = start; i < w.Scrollback.Count; i++)
                output.Add(w.Scrollback[i], w.Scrollback.SequenceAt(i));

            var state = new List<StateUpdateMessage>();
            foreach (KeyValuePair<string, StateValue> kv in w.GameState.Snapshot())
                state.Add(new StateUpdateMessage(sessionId, kv.Key, kv.Value.Kind, kv.Value.Text));

            var panels = new List<HudPanelMessage>();
            foreach (KeyValuePair<string, PanelSpec> kv in w.Hud.PanelSpecs)
                panels.Add(new HudPanelMessage(sessionId, kv.Key, kv.Value));

            // Pane tails, newest-first order preserved. Capped well below the main output
            // budget: chat is skimmed, not scrolled back through, on a phone.
            var panes = new List<PaneOutputMessage>();
            foreach (CapturePaneViewModel pane in w.CapturePanes)
            {
                if (pane.Buffer.Count == 0) continue;
                var pb = new OutputBatchBuilder();
                int pTake = Math.Min(200, pane.Buffer.Count);
                int pStart = pane.Buffer.Count - pTake;
                for (int i = pStart; i < pane.Buffer.Count; i++)
                    pb.Add(pane.Buffer[i], pane.Buffer.SequenceAt(i));
                OutputBatchMessage built = pb.Build(sessionId);
                panes.Add(new PaneOutputMessage(sessionId, pane.Name, built.Styles, built.Lines));
            }

            return new SnapshotMessage(sessionId, Describe(w), output.Build(sessionId), state, panels, panes);
        });

    private WorldViewModel? Find(string sessionId)
    {
        foreach (WorldViewModel w in _main.Worlds)
            if (string.Equals(w.SessionId, sessionId, StringComparison.Ordinal))
                return w;
        return null;
    }
}
