using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using Scrye.App.Companion;
using Scrye.Companion.Server.Tailscale;
using Scrye.Core.Automation;

namespace Scrye.App.ViewModels;

/// <summary>One row in the "what will buzz my phone" list.</summary>
/// <param name="Name">The trigger's name, or a placeholder when it has none.</param>
/// <param name="Pattern">What it matches, so an unnamed rule is still identifiable.</param>
/// <param name="Enabled">Disabled rules are listed too — a rule that is set to notify but
/// switched off is exactly the thing someone hunts for when notifications go quiet.</param>
public sealed record NotifySourceRow(string Name, string Pattern, string Group, bool Enabled)
{
    public string Detail => string.IsNullOrEmpty(Group) ? Pattern : $"[{Group}] {Pattern}";
    public string StateLabel => Enabled ? "" : "disabled";
}

/// <summary>
/// Backs the companion panel: start/stop the phone server, show how to reach it, and list
/// what can raise a notification.
///
/// <para>This replaces <c>.companion</c> as the main way in, and the reason is not
/// convenience. The command printed the access token into the output pane, which means
/// session logging wrote the credential to disk — a panel can show it without it ever
/// entering scrollback. The command survives for muscle memory but no longer prints it.</para>
///
/// <para>Everything here is per-world even though the server is per-process, because the
/// question people actually have is "how do I get <em>this</em> character on my phone" —
/// the session id and the notify list are properties of the world, not of the server.</para>
/// </summary>
public sealed class CompanionViewModel : ViewModelBase
{
    private readonly Func<CompanionController?> _controller;
    private readonly Func<string> _sessionId;
    private readonly Func<IReadOnlyList<(TriggerDef Def, bool Enabled)>> _notifyingTriggers;
    private readonly Action<string> _copy;

    public ObservableCollection<NotifySourceRow> NotifySources { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand CopyTokenCommand { get; }
    public RelayCommand CopyServeCommand { get; }
    public RelayCommand TestNotifyCommand { get; }

    public CompanionViewModel(Func<CompanionController?> controller,
                              Func<string> sessionId,
                              Func<IReadOnlyList<(TriggerDef, bool)>> notifyingTriggers,
                              Action<string> copyToClipboard)
    {
        _controller = controller;
        _sessionId = sessionId;
        _notifyingTriggers = notifyingTriggers;
        _copy = copyToClipboard;

        StartCommand = new RelayCommand(() => _ = StartAsync());
        StopCommand = new RelayCommand(() => _ = StopAsync());
        RefreshCommand = new RelayCommand(Refresh);
        CloseCommand = new RelayCommand(Close);
        CopyUrlCommand = new RelayCommand(() => Copy(PhoneUrl ?? LocalUrl, "address"));
        CopyTokenCommand = new RelayCommand(() => Copy(Token, "token"));
        CopyServeCommand = new RelayCommand(() => Copy(ServeCommandText, "command"));
        TestNotifyCommand = new RelayCommand(() => _ = TestNotifyAsync());
    }

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set { if (SetField(ref _isOpen, value) && value) Refresh(); }
    }

    /// <summary>Open the panel, refreshing even when it was already open — the command path
    /// calls this right after starting the server, and a stale "stopped" panel would be a
    /// confusing thing to be looking at.</summary>
    public void Open()
    {
        if (IsOpen) Refresh();
        else IsOpen = true;          // the setter refreshes on the way in
    }

    public void Close() => IsOpen = false;

    // ---- server state --------------------------------------------------------

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanTestNotify));   // gated on the server too, not just devices
        }
    }

    public bool IsStopped => !IsRunning;

    public string StatusText => IsRunning ? "running" : "stopped";

    private string? _localUrl;
    /// <summary>The loopback address the server is actually bound to. Useful on this machine
    /// and for a browser tab while testing; useless from a phone, which is what the tailnet
    /// section below is for.</summary>
    public string? LocalUrl
    {
        get => _localUrl;
        private set => SetField(ref _localUrl, value);
    }

    private string? _token;
    /// <summary>Only needed off-tailnet, and regenerated on every start. Shown here rather
    /// than printed so it never reaches the session log.</summary>
    public string? Token
    {
        get => _token;
        private set { if (SetField(ref _token, value)) OnPropertyChanged(nameof(HasToken)); }
    }

    public bool HasToken => !string.IsNullOrEmpty(Token);

    private string? _trustedLogin;
    /// <summary>The tailnet identity that may connect without typing anything. When this is
    /// set, the token is a fallback rather than the normal path.</summary>
    public string? TrustedLogin
    {
        get => _trustedLogin;
        private set { if (SetField(ref _trustedLogin, value)) OnPropertyChanged(nameof(HasTrustedLogin)); }
    }

    public bool HasTrustedLogin => !string.IsNullOrEmpty(TrustedLogin);

    private string _sessionIdText = "";
    /// <summary>Which session on the phone corresponds to this tab.</summary>
    public string SessionIdText
    {
        get => _sessionIdText;
        private set => SetField(ref _sessionIdText, value);
    }

    private int _pushDevices;
    public int PushDevices
    {
        get => _pushDevices;
        private set
        {
            if (!SetField(ref _pushDevices, value)) return;
            OnPropertyChanged(nameof(PushDevicesText));
            OnPropertyChanged(nameof(CanTestNotify));
        }
    }

    public string PushDevicesText => PushDevices switch
    {
        0 => "no phones registered for notifications",
        1 => "1 phone registered for notifications",
        _ => $"{PushDevices} phones registered for notifications",
    };

    public bool CanTestNotify => IsRunning && PushDevices > 0;

    // ---- reaching it from a phone --------------------------------------------

    private string? _phoneUrl;
    /// <summary>The tailnet URL, once Tailscale is up. This is what the QR code encodes.</summary>
    public string? PhoneUrl
    {
        get => _phoneUrl;
        private set
        {
            if (!SetField(ref _phoneUrl, value)) return;
            OnPropertyChanged(nameof(HasPhoneUrl));
            OnPropertyChanged(nameof(QrPayload));
        }
    }

    public bool HasPhoneUrl => !string.IsNullOrEmpty(PhoneUrl);

    /// <summary>What the QR code shows. Empty until there is a real tailnet URL: encoding
    /// the loopback address would produce a code that scans and then fails to load, which is
    /// worse than no code at all.</summary>
    public string QrPayload => PhoneUrl ?? "";

    private string _tailscaleStatus = "checking…";
    public string TailscaleStatusText
    {
        get => _tailscaleStatus;
        private set => SetField(ref _tailscaleStatus, value);
    }

    private string? _serveCommandText;
    /// <summary>The <c>tailscale serve</c> line to paste into a terminal. Printed, never run:
    /// it changes the user's tailnet configuration, and its first use opens a browser consent
    /// page — not something a UI should trigger behind someone's back.</summary>
    public string? ServeCommandText
    {
        get => _serveCommandText;
        private set { if (SetField(ref _serveCommandText, value)) OnPropertyChanged(nameof(HasServeCommand)); }
    }

    public bool HasServeCommand => !string.IsNullOrEmpty(ServeCommandText);

    private string _notice = "";
    /// <summary>Transient feedback for the last action — copied, started, test sent.</summary>
    public string Notice
    {
        get => _notice;
        private set => SetField(ref _notice, value);
    }

    // ---- actions -------------------------------------------------------------

    public void Refresh()
    {
        CompanionController? c = _controller();

        SessionIdText = _sessionId();
        IsRunning = c?.IsRunning ?? false;
        LocalUrl = c?.IsRunning == true ? c.Url : null;
        Token = c?.IsRunning == true ? c.Token : null;
        TrustedLogin = c?.IsRunning == true ? c.TrustedLogin : null;
        PushDevices = c?.PushSubscriberCount ?? 0;

        RefreshNotifySources();
        _ = RefreshTailscaleAsync();
    }

    private void RefreshNotifySources()
    {
        NotifySources.Clear();
        foreach ((TriggerDef def, bool enabled) in _notifyingTriggers())
            NotifySources.Add(new NotifySourceRow(
                string.IsNullOrWhiteSpace(def.Name) ? "(unnamed)" : def.Name,
                def.Pattern,
                def.Group ?? "",
                enabled));

        OnPropertyChanged(nameof(NotifySummary));
    }

    /// <summary>Deliberately mentions plugins even though they cannot be enumerated: plugin
    /// code is arbitrary, and a list that looked complete while omitting <c>scrye.notify()</c>
    /// would be worse than one that admits the gap.</summary>
    public string NotifySummary => NotifySources.Count == 0
        ? "No triggers in this world are set to Notify. Plugins may still notify on their own."
        : $"{NotifySources.Count} trigger(s) set to Notify. Plugins may notify on their own too.";

    private async Task RefreshTailscaleAsync()
    {
        TailscaleStatus ts;
        try
        {
            ts = await TailscaleInfo.QueryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Querying an external CLI is exactly the sort of thing that can hang or vanish;
            // it must not be able to take the panel down with it.
            TailscaleStatusText = $"could not query tailscale: {ex.Message}";
            PhoneUrl = null;
            ServeCommandText = null;
            return;
        }

        if (!ts.Installed)
        {
            TailscaleStatusText = "Tailscale is not installed — it is what lets a phone reach this.";
            PhoneUrl = null;
            ServeCommandText = null;
            return;
        }

        if (!ts.Running || ts.DnsName is null)
        {
            TailscaleStatusText = ts.Detail ?? "Tailscale is installed but not logged in.";
            PhoneUrl = null;
            ServeCommandText = null;
            return;
        }

        TailscaleStatusText = ts.DnsName;
        PhoneUrl = ts.PublicUrl;
        ServeCommandText = TailscaleInfo.ServeCommand(4747);
    }

    private async Task StartAsync()
    {
        if (_controller() is not CompanionController c) { Notice = "companion server unavailable"; return; }

        try
        {
            await c.StartAsync();
            Notice = "server started";
        }
        catch (Exception ex)
        {
            // Almost always the port already being in use. Say so rather than failing silently.
            Notice = $"could not start: {ex.Message}";
        }

        Refresh();
    }

    private async Task StopAsync()
    {
        if (_controller() is not CompanionController c) return;

        try
        {
            await c.StopAsync();
            Notice = "server stopped";
        }
        catch (Exception ex)
        {
            Notice = $"stopped with errors: {ex.Message}";
        }

        Refresh();
    }

    private async Task TestNotifyAsync()
    {
        if (_controller() is not CompanionController c) return;

        Notice = "sending a test notification…";
        try
        {
            int delivered = await c.TestNotifyAsync();
            Notice = delivered > 0
                ? $"delivered to {delivered} device(s)"
                : "no device accepted it — check the phone's notification settings";
        }
        catch (Exception ex)
        {
            Notice = $"notification failed: {ex.Message}";
        }
    }

    private void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        _copy(text);
        Notice = $"{what} copied to clipboard";
    }

    /// <summary>Called when the server's state changed underneath the panel, e.g. because
    /// <c>.companion</c> was used instead. Safe from any thread.</summary>
    public void NotifyStateChanged()
    {
        if (!IsOpen) return;
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }
}
