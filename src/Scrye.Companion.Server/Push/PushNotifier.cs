using Microsoft.Extensions.Logging;

namespace Scrye.Companion.Server.Push;

/// <summary>
/// What a fan-out actually did — the test command shows this verbatim, so a failing
/// delivery is a visible sentence instead of a silent shrug.
/// </summary>
public sealed record PushOutcome(int Delivered, int Expired, int Failed, string? LastError)
{
    public override string ToString() =>
        $"delivered {Delivered}, pruned {Expired} expired, {Failed} failed"
        + (LastError is not null ? $" ({LastError})" : "");
}

/// <summary>
/// Fans a notification out to every registered device, pruning the ones the push service
/// says are gone.
///
/// <para>Kept separate from <see cref="PushSender"/> so the desktop has one call to make and
/// no bookkeeping to remember. Fire-and-forget by design: a slow push service must never
/// stall the session loop, which is the thread that raised the notification in the first
/// place.</para>
/// </summary>
public sealed class PushNotifier
{
    private readonly PushStore _store;
    private readonly PushSender _sender;
    private readonly ILogger? _logger;

    public PushNotifier(PushStore store, PushSender sender, ILogger? logger = null)
    {
        _store = store;
        _sender = sender;
        _logger = logger;
    }

    public int SubscriberCount => _store.Count;

    /// <summary>Notify every device and report exactly what happened — used by the test
    /// command and status output; callers in the hot path use <see cref="NotifyInBackground"/>.</summary>
    public async Task<PushOutcome> NotifyAsync(string title, string body, string? sessionId, DateTimeOffset now,
                                               CancellationToken ct = default)
    {
        string payload = PushSender.BuildPayload(title, body, sessionId);
        int delivered = 0, expired = 0, failed = 0;
        string? lastError = null;

        foreach (PushSubscription sub in _store.All)
        {
            PushResult result = await _sender.SendAsync(sub, payload, now, ct: ct).ConfigureAwait(false);
            switch (result)
            {
                case PushResult.Delivered:
                    delivered++;
                    break;
                case PushResult.Expired:
                    // The device uninstalled, cleared data, or revoked permission. Keeping
                    // it would mean retrying forever and courting a rate limit.
                    _store.Remove(sub.Id);
                    expired++;
                    _logger?.LogInformation("Pruned expired push subscription");
                    break;
                case PushResult.Failed:
                    failed++;
                    lastError = _sender.LastError ?? lastError;
                    _logger?.LogWarning("Push delivery failed: {Reason}", _sender.LastError);
                    break;
            }
        }

        return new PushOutcome(delivered, expired, failed, lastError);
    }

    /// <summary>Notify without waiting. Exceptions are swallowed deliberately: a failed
    /// notification must never propagate into the MUD session that triggered it.</summary>
    public void NotifyInBackground(string title, string body, string? sessionId, DateTimeOffset now)
    {
        if (_store.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try { await NotifyAsync(title, body, sessionId, now).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Push fan-out threw"); }
        });
    }
}
