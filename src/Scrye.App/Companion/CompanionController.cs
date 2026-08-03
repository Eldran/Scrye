using Scrye.App.ViewModels;
using Scrye.Companion.Protocol;
using Scrye.Companion.Server;
using Scrye.Core.Plugins;

namespace Scrye.App.Companion;

/// <summary>
/// Owns the companion server's lifetime and keeps it attached to the worlds that exist.
///
/// <para>Separate from <c>MainWindowViewModel</c> on purpose: starting a listener, minting a
/// credential and wiring per-world publishers is not view-model work, and keeping it apart
/// means the companion can be removed or disabled without touching the window.</para>
///
/// <para>Started on demand from the UI, never at launch — an idle Scrye should not be
/// listening on a socket the user did not ask for (companion design §7).</para>
/// </summary>
public sealed class CompanionController : IAsyncDisposable
{
    private readonly MainWindowViewModel _main;
    private CompanionServer? _server;

    public CompanionController(MainWindowViewModel main) => _main = main;

    public bool IsRunning => _server is not null;

    /// <summary>URL to hand to a client, or null when stopped.</summary>
    public string? Url => _server?.WebSocketUrl;

    /// <summary>The bearer token for this run, or null when stopped. Show it in the UI so it
    /// can be copied; it becomes the QR payload once pairing lands (§7.1).</summary>
    public string? Token { get; private set; }

    /// <summary>The tailnet login allowed to connect without a token, when one was found.
    /// Null when Tailscale is absent or signed out.</summary>
    public string? TrustedLogin { get; private set; }

    public async Task StartAsync()
    {
        if (_server is not null) return;

        // If this machine is signed into a tailnet, trust that identity: `tailscale serve`
        // strips client-supplied identity headers and sets its own, so a phone reaching us
        // through the proxy is already authenticated and has nothing to type. The token
        // remains for loopback and for setups without Tailscale.
        Scrye.Companion.Server.Tailscale.TailscaleStatus ts =
            await Scrye.Companion.Server.Tailscale.TailscaleInfo.QueryAsync().ConfigureAwait(true);
        TrustedLogin = ts is { Running: true, Login: { Length: > 0 } } ? ts.Login : null;

        // Push identity and subscriptions live next to the profiles, so they survive
        // restarts. A regenerated VAPID key silently invalidates every subscription.
        string dataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye");

        CompanionServerOptions options = new()
        {
            Token = CompanionServerOptions.NewToken(),
            TrustedTailnetLogins = TrustedLogin is null
                ? Array.Empty<string>()
                : new[] { TrustedLogin },
            VapidKeyPath = System.IO.Path.Combine(dataDir, "companion-vapid.json"),
            PushSubscriptionPath = System.IO.Path.Combine(dataDir, "companion-push.json"),
        };
        Token = options.Token;

        var server = new CompanionServer(options, new AppSessionSource(_main));
        await server.StartAsync().ConfigureAwait(true);
        _server = server;

        foreach (WorldViewModel w in _main.Worlds) Attach(w);
    }

    public async Task StopAsync()
    {
        if (_server is null) return;

        foreach (WorldViewModel w in _main.Worlds) Detach(w);

        await _server.DisposeAsync().ConfigureAwait(true);
        _server = null;
        Token = null;
    }

    /// <summary>Hook a world up to the running server. Call for every newly opened world;
    /// a no-op while the server is stopped, so callers need no condition.</summary>
    public void Attach(WorldViewModel world)
    {
        if (_server is null) return;

        world.Companion = _server.Hub;
        world.Notifier = _server.Notifier;

        // Panels already built (plugins load before the server may have started) plus any
        // built from here on. Streaming the spec, not the rendering, is what gives every
        // plugin a mobile UI for free (§2).
        world.Hud.PanelAdded = (key, spec) =>
            _server?.Hub.PublishHudPanel(new HudPanelMessage(world.SessionId, key, spec));
        world.Hud.PanelRemoved = key =>
            _server?.Hub.PublishHudPanelRemoved(new HudPanelRemovedMessage(world.SessionId, key));

        foreach (KeyValuePair<string, PanelSpec> kv in world.Hud.PanelSpecs)
            _server.Hub.PublishHudPanel(new HudPanelMessage(world.SessionId, kv.Key, kv.Value));

        _server.Hub.PublishSessionState(AppSessionSource.Describe(world));
    }

    /// <summary>Unhook a world — on close, or when the server stops.</summary>
    public void Detach(WorldViewModel world)
    {
        world.Companion = null;
        world.Notifier = null;
        world.Hud.PanelAdded = null;
        world.Hud.PanelRemoved = null;

        // Tell devices it is gone; otherwise a phone keeps offering a session that
        // no longer exists in its picker.
        _server?.Hub.PublishSessionState(
            new SessionStateMessage(world.SessionId, false, world.Ref?.Character ?? world.Title,
                                    world.Ref?.Mud ?? world.Title));
    }

    /// <summary>Re-announce a world whose connection state changed, so session pickers on
    /// every device stay honest.</summary>
    public void NotifySessionState(WorldViewModel world) =>
        _server?.Hub.PublishSessionState(AppSessionSource.Describe(world));

    /// <summary>Devices currently registered for push, for status output.</summary>
    public int PushSubscriberCount => _server?.Notifier.SubscriberCount ?? 0;

    /// <summary>Send a test notification to every registered device.</summary>
    public Task<int> TestNotifyAsync() =>
        _server is null
            ? Task.FromResult(0)
            : _server.Notifier.NotifyAsync("Scrye", "Test notification from your PC.", null,
                                           DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
