using System.Collections.Concurrent;
using Scrye.Companion.Protocol;
using Scrye.Core.Automation;
using Scrye.Companion.Server.Sessions;

namespace Scrye.Companion.Server.Hub;

/// <summary>
/// The fan-out point between the desktop and every connected companion device.
///
/// <para><b>Direction of travel matters.</b> The desktop <i>pushes</i> into the hub from the
/// threads that own the data — <c>PublishOutput</c> from the UI thread's flush,
/// <c>PublishState</c> from the session loop — and each subscriber's socket loop <i>pulls</i>
/// from its own queue. Nothing here reads <c>ScrollbackBuffer</c> or <c>StateStore</c>, so
/// the three threading contracts in §4.1 never meet.</para>
///
/// <para>Multi-subscriber from the first commit, per §11.3: a phone, a tablet and a browser
/// at once is the normal case even for one user.</para>
/// </summary>
public sealed class CompanionHub
{
    private readonly ConcurrentDictionary<string, CompanionSubscriber> _subscribers = new(StringComparer.Ordinal);
    private readonly ICompanionSessionSource _source;

    public CompanionHub(ICompanionSessionSource source) => _source = source;

    public int SubscriberCount => _subscribers.Count;

    public IReadOnlyCollection<CompanionSubscriber> Subscribers => _subscribers.Values.ToArray();

    public CompanionSubscriber Add(string id, bool mayRunScripts)
    {
        var sub = new CompanionSubscriber(id, mayRunScripts);
        _subscribers[id] = sub;
        return sub;
    }

    public void Remove(string id)
    {
        if (_subscribers.TryRemove(id, out CompanionSubscriber? sub)) sub.Complete();
    }

    // ---- desktop → devices ---------------------------------------------------

    /// <summary>Publish one flush's worth of output. Called from the UI thread, inside the
    /// same drain that fills <c>ScrollbackBuffer</c> — that 33 ms tick already is the batch
    /// window, so there is no second batcher here (§3.1).</summary>
    public void PublishOutput(OutputBatchMessage batch)
    {
        if (batch.Lines.Count == 0) return;
        Broadcast(batch.SessionId, batch);
    }

    /// <summary>Publish a state-tree change. Called from the session loop via
    /// <c>StateStore.Changed</c>.</summary>
    public void PublishState(StateUpdateMessage update) => Broadcast(update.SessionId, update);

    /// <summary>Publish lines a trigger routed into a capture pane. A separate stream from
    /// <see cref="PublishOutput"/> because a captured line may also be gagged from the main
    /// output, and would otherwise never reach a client at all.</summary>
    public void PublishPaneOutput(PaneOutputMessage pane)
    {
        if (pane.Lines.Count == 0) return;
        Broadcast(pane.SessionId, pane);
    }

    public void PublishHudPanel(HudPanelMessage panel) => Broadcast(panel.SessionId, panel);

    public void PublishHudPanelRemoved(HudPanelRemovedMessage removed) => Broadcast(removed.SessionId, removed);

    /// <summary>Session connected/disconnected/renamed. Goes to every device regardless of
    /// which world it is watching, so session pickers stay current.</summary>
    public void PublishSessionState(SessionStateMessage state)
    {
        foreach (CompanionSubscriber sub in _subscribers.Values) sub.TryPublish(state);
    }

    private void Broadcast(string sessionId, object message)
    {
        foreach (CompanionSubscriber sub in _subscribers.Values)
            if (sub.Watches(sessionId))
                sub.TryPublish(message);
    }

    // ---- devices → desktop ---------------------------------------------------

    /// <summary>Handle one decoded client frame. Returns the message to send back, or null
    /// when there is nothing to say. Every rejection produces an <see cref="ErrorMessage"/>
    /// rather than a silent drop, so a client can tell refusal from a dead socket (§7.3).</summary>
    public async ValueTask<object?> HandleClientMessageAsync(CompanionSubscriber sub, string json)
    {
        string? type = CompanionJson.PeekType(json);
        if (type is null)
            return new ErrorMessage(CompanionErrorCode.BadRequest, "frame has no type");

        switch (type)
        {
            case MessageTypes.SessionSubscribe:
            {
                var m = CompanionJson.Deserialize<SessionSubscribeMessage>(json);
                if (m is null || string.IsNullOrEmpty(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.BadRequest, "missing sessionId");
                if (!KnownSession(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.UnknownSession, m.SessionId, m.SessionId);

                sub.Subscribe(m.SessionId);
                return await _source.GetSnapshotAsync(m.SessionId, maxLines: 500).ConfigureAwait(false)
                       ?? (object)new ErrorMessage(CompanionErrorCode.UnknownSession, m.SessionId, m.SessionId);
            }

            case MessageTypes.SessionResume:
            {
                var m = CompanionJson.Deserialize<SessionResumeMessage>(json);
                if (m is null || string.IsNullOrEmpty(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.BadRequest, "missing sessionId");
                if (!KnownSession(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.UnknownSession, m.SessionId, m.SessionId);

                sub.Subscribe(m.SessionId);
                sub.SetResumePoint(m.LastReceivedSequence);

                // Replay when scrollback still holds the gap; otherwise rebuild. Falling
                // back to a snapshot is the SAFE outcome — serving a partial replay would
                // silently skip lines the client never saw (§6).
                OutputBatchMessage? replay =
                    await _source.TryReplayAsync(m.SessionId, m.LastReceivedSequence).ConfigureAwait(false);
                if (replay is not null) return replay;

                return await _source.GetSnapshotAsync(m.SessionId, maxLines: 500).ConfigureAwait(false)
                       ?? (object)new ErrorMessage(CompanionErrorCode.ResumeTooOld, m.SessionId, m.SessionId);
            }

            case MessageTypes.CommandSend:
            {
                var m = CompanionJson.Deserialize<SendCommandMessage>(json);
                if (m is null || string.IsNullOrEmpty(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.BadRequest, "missing sessionId");
                if (!KnownSession(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.UnknownSession, m.SessionId, m.SessionId);

                // The device declares no privilege of its own: the origin is built here from
                // what this connection was granted at authentication time.
                var origin = CommandOrigin.Companion(sub.MayRunScripts);
                CommandSubmitResult result =
                    await _source.SubmitCommandAsync(m.SessionId, m.Command ?? "", origin).ConfigureAwait(false);

                return result == CommandSubmitResult.RejectedScriptingNotPermitted
                    ? new ErrorMessage(CompanionErrorCode.PermissionDenied,
                        "this device may not run script console commands", m.SessionId)
                    : null;
            }

            case MessageTypes.HudAction:
            {
                var m = CompanionJson.Deserialize<HudActionMessage>(json);
                if (m is null || string.IsNullOrEmpty(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.BadRequest, "missing sessionId");
                if (!KnownSession(m.SessionId))
                    return new ErrorMessage(CompanionErrorCode.UnknownSession, m.SessionId, m.SessionId);
                if (string.IsNullOrEmpty(m.PluginId) || string.IsNullOrEmpty(m.Action))
                    return new ErrorMessage(CompanionErrorCode.BadRequest, "malformed panelId or action", m.SessionId);

                // Deliberately NOT behind the scripting permission: the action id was minted
                // by the desktop's own plugin runtime and published in a panel spec, so a
                // device can only fire callbacks the user's own plugins already defined.
                bool ok = await _source.InvokeHudActionAsync(m.SessionId, m.PluginId, m.Action)
                                       .ConfigureAwait(false);
                return ok ? null : new ErrorMessage(
                    CompanionErrorCode.BadRequest, $"no such panel action '{m.Action}'", m.SessionId);
            }

            default:
                return new ErrorMessage(CompanionErrorCode.BadRequest, $"unsupported type '{type}'");
        }
    }

    private bool KnownSession(string sessionId) =>
        _source.GetSessions().Any(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
}
