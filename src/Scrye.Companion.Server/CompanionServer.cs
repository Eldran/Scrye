using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scrye.Companion.Protocol;
using Scrye.Companion.Server.Hub;
using Scrye.Companion.Server.Sessions;

namespace Scrye.Companion.Server;

/// <summary>
/// The in-process Kestrel host. Runs inside Scrye.App so it sits next to the state it taps,
/// with no IPC (design §9).
///
/// <para>Lifecycle: construct, <see cref="StartAsync"/>, publish through <see cref="Hub"/>,
/// <see cref="DisposeAsync"/>. Each accepted socket gets a reader loop and a writer loop;
/// the writer drains only that device's queue, so one stalled client cannot hold up
/// another, and neither can reach back into UI-thread state (§4.1).</para>
/// </summary>
public sealed class CompanionServer : IAsyncDisposable
{
    private readonly CompanionServerOptions _options;
    private readonly ICompanionSessionSource _source;
    private readonly ILogger<CompanionServer>? _logger;
    private WebApplication? _app;
    private int _connectionCounter;

    public CompanionServer(
        CompanionServerOptions options,
        ICompanionSessionSource source,
        ILogger<CompanionServer>? logger = null)
    {
        _options = options;
        _source = source;
        _logger = logger;
        Hub = new CompanionHub(source);
    }

    /// <summary>Publish desktop-side events through this.</summary>
    public CompanionHub Hub { get; }

    /// <summary>The bound port. Equals <c>Options.Port</c> unless 0 was requested, in which
    /// case the OS-assigned port is resolved after start (useful for tests).</summary>
    public int BoundPort { get; private set; }

    public string WebSocketUrl => $"ws://{_options.BindAddress}:{BoundPort}/companion";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();          // the desktop owns user-facing logging
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(IPAddress.Parse(_options.BindAddress), _options.Port));

        WebApplication app = builder.Build();
        app.UseWebSockets();

        // Client assets, served from the same origin as the socket so no page CSP is
        // involved (a chrome:// or third-party page restricts connect-src and blocks the
        // upgrade). All unauthenticated on purpose: they are inert, and the token is still
        // required to open the socket.
        //
        // Cache-Control: no-cache means "revalidate", not "never store" — the shell may sit
        // in the service worker's cache but must not go stale against a rebuilt desktop
        // while the protocol is still moving.
        static IResult Asset(string body, string type) =>
            Results.Text(body, type);

        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return Asset(Client.AppShell.Html, "text/html; charset=utf-8");
        });

        app.MapGet("/app.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return Asset(Client.AppScript.Js, "text/javascript; charset=utf-8");
        });

        // Must be served from the scope it claims ("/"), or registration fails.
        app.MapGet("/sw.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return Asset(Client.PwaAssets.ServiceWorker, "text/javascript; charset=utf-8");
        });

        app.MapGet("/manifest.webmanifest", () =>
            Asset(Client.PwaAssets.Manifest, "application/manifest+json; charset=utf-8"));

        app.MapGet("/icon.svg", () => Asset(Client.PwaAssets.Icon, "image/svg+xml; charset=utf-8"));

        // Lets the client find out whether it needs a token *before* opening the socket.
        // A failed WebSocket handshake gives the browser almost nothing to report — no
        // status code reaches script — so without this the app could only guess why it
        // could not connect, and would prompt for a token even when none was required.
        app.MapGet("/whoami", (HttpContext ctx) =>
        {
            string login = ctx.Request.Headers[TailscaleLoginHeader].ToString().Trim();
            bool viaTailnet = IsTrustedTailnetUser(ctx);
            return Results.Json(new
            {
                authorized = viaTailnet,
                login = login.Length > 0 ? login : null,
                needsToken = !viaTailnet,
            });
        });

        // The protocol scope from the previous pass, kept for debugging the wire directly.
        app.MapGet("/debug", () => Asset(DebugClient.Html, "text/html; charset=utf-8"));

        app.Map("/companion", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!IsAuthorized(ctx))
            {
                // Rejected before the upgrade, so an unauthenticated peer never gets a socket.
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await RunConnectionAsync(socket, ctx.RequestAborted).ConfigureAwait(false);
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        BoundPort = ResolveBoundPort(app) ?? _options.Port;
        _logger?.LogInformation("Companion server listening on {Url}", WebSocketUrl);
    }

    // ---- auth ----------------------------------------------------------------

    /// <summary>The tailnet login this request carries, or null. Set by <c>tailscale serve</c>,
    /// which strips any client-supplied copy first.</summary>
    internal const string TailscaleLoginHeader = "Tailscale-User-Login";

    private bool IsAuthorized(HttpContext ctx) => IsTrustedTailnetUser(ctx) || HasValidToken(ctx);

    /// <summary>Whether the proxy vouched for a login we allow. This is what lets a phone
    /// open the tailnet URL and simply work, with no credential to type.</summary>
    private bool IsTrustedTailnetUser(HttpContext ctx)
    {
        if (_options.TrustedTailnetLogins.Count == 0) return false;

        string login = ctx.Request.Headers[TailscaleLoginHeader].ToString().Trim();
        if (login.Length == 0) return false;

        foreach (string allowed in _options.TrustedTailnetLogins)
            if (string.Equals(login, allowed.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private bool HasValidToken(HttpContext ctx)
    {
        string? presented = null;

        string? header = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = header["Bearer ".Length..].Trim();

        // Browsers cannot set headers on a WebSocket handshake, so the query string is a
        // necessary alternative for the PWA client (§8.1).
        if (string.IsNullOrEmpty(presented) && ctx.Request.Query.TryGetValue("token", out var q))
            presented = q.ToString();

        return TokensMatch(presented, _options.Token);
    }

    /// <summary>Fixed-time comparison. A token check that returns early on the first wrong
    /// character leaks its length and prefix to anything that can time it — cheap to avoid,
    /// awkward to retrofit.</summary>
    private static bool TokensMatch(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        byte[] a = Encoding.UTF8.GetBytes(presented);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---- connection ----------------------------------------------------------

    private async Task RunConnectionAsync(WebSocket socket, CancellationToken requestAborted)
    {
        string id = $"device-{Interlocked.Increment(ref _connectionCounter)}";
        CompanionSubscriber sub = Hub.Add(id, _options.MayRunScripts);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        try
        {
            // Tell the client what worlds exist before it subscribes to one.
            sub.TryPublish(new SessionListMessage(_source.GetSessions()));

            Task reader = ReadLoopAsync(socket, sub, cts.Token);
            Task writer = WriteLoopAsync(socket, sub, cts.Token);

            // Either side finishing ends the connection; cancel the other so neither leaks.
            await Task.WhenAny(reader, writer).ConfigureAwait(false);
            cts.Cancel();
            await Task.WhenAll(
                reader.ContinueWith(_ => { }, TaskScheduler.Default),
                writer.ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);

            // Complete the closing handshake here, once both loops are done — a close frame
            // sent while the writer still had a send in flight would throw. Skipping this
            // aborts the socket, and a browser logs "closed without completing the close
            // handshake" on every ordinary disconnect.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                                .ConfigureAwait(false);
                }
                catch (WebSocketException) { /* peer already gone */ }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            Hub.Remove(id);
            _logger?.LogInformation("Companion device {Id} disconnected ({Dropped} frames dropped)",
                id, sub.DroppedFrames);
        }
    }

    private async Task ReadLoopAsync(WebSocket socket, CompanionSubscriber sub, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var accumulated = new List<byte>(capacity: 16 * 1024);

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException) { return; }

            if (result.MessageType == WebSocketMessageType.Close) return;

            accumulated.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;   // a frame may arrive in pieces

            string json = Encoding.UTF8.GetString(accumulated.ToArray());
            accumulated.Clear();

            object? reply;
            try
            {
                reply = await Hub.HandleClientMessageAsync(sub, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One malformed frame must not kill the connection.
                _logger?.LogWarning(ex, "Companion device {Id} sent a frame that threw", sub.Id);
                reply = new ErrorMessage(CompanionErrorCode.BadRequest, "could not process frame");
            }

            if (reply is not null) sub.TryPublish(reply);
        }
    }

    private static async Task WriteLoopAsync(WebSocket socket, CompanionSubscriber sub, CancellationToken ct)
    {
        try
        {
            await foreach (object message in sub.Outbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open) return;
                byte[] payload = Encoding.UTF8.GetBytes(CompanionJson.Serialize(message));
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct)
                            .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* connection closing */ }
        catch (WebSocketException) { /* peer vanished */ }
    }

    private static int? ResolveBoundPort(WebApplication app)
    {
        var addresses = app.Services
            .GetService<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;

        // WebApplication exposes addresses via the server feature collection.
        addresses ??= app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            ?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;

        foreach (string address in addresses ?? Array.Empty<string>())
            if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
                return uri.Port;

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (CompanionSubscriber sub in Hub.Subscribers) sub.Complete();

        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
