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

    /// <summary>Does this trigger currently raise a notification? Bound two-way to the tick box,
    /// so setting it persists and re-applies through <see cref="SetNotify"/>.</summary>
    public bool Notify { get; init; }

    /// <summary>Applies a new Notify state: persist to the profile and re-apply to the live
    /// session. Null when the trigger cannot be edited from here — see <see cref="Why"/>.</summary>
    public Action<bool>? SetNotify { get; init; }

    /// <summary>Empty when editable; otherwise why not, shown beside the row. An unnamed trigger
    /// inherited from a shallower layer is the case: the cascade merges rules by NAME, so there
    /// is no way to write an override that would find its way back to this one.</summary>
    public string Why { get; init; } = "";

    public bool CanEdit => SetNotify is not null;
    public bool HasWhy => Why.Length > 0;

    /// <summary>Two-way binding target for the tick box. The getter is the flag as loaded; the
    /// setter routes through <see cref="SetNotify"/>, which persists and re-applies, then
    /// rebuilds the list — so the row you are looking at is replaced rather than mutated.</summary>
    public bool NotifyChecked
    {
        get => Notify;
        set { if (value != Notify) SetNotify?.Invoke(value); }
    }
}

/// <summary>One notification source a plugin reported via the <c>plugin.&lt;id&gt;.notify</c>
/// state convention (see the guide): newline-joined rows of
/// <c>label \t detail \t state \t command</c>. The command runs through the normal input
/// pipeline, so the plugin's own alias handles it — the panel never needs to know what the
/// command means, which is the whole reason the convention is text.
///
/// <para>Four row kinds, by <c>state</c>:</para>
/// <list type="bullet">
/// <item><c>on</c> / <c>off</c> with a command — a switch.</item>
/// <item><c>add</c> — an editable list's entry field. The command is a TEMPLATE containing
/// <c>{}</c>, replaced by what the user types (e.g. <c>chat watch {}</c>).</item>
/// <item><c>item</c> — one entry of such a list, with a command that removes it.</item>
/// <item>anything else — informational, rendered as plain text.</item>
/// </list>
/// </summary>
public sealed record PluginNotifyRow(string Label, string Detail, string State, RelayCommand? Toggle)
{
    /// <summary>Substituted with the user's text in an <c>add</c> row's command template.</summary>
    public const string Placeholder = "{}";

    /// <summary>Set on an <c>add</c> row: runs the template with the argument substituted.
    /// Separate from <see cref="Toggle"/> because it needs the text the user typed.</summary>
    public RelayCommand<string>? Add { get; init; }

    /// <summary>Rows whose state isn't literally on/off (e.g. "watching: Goran") are
    /// informational; they render without a toggle even if a command was supplied.</summary>
    public bool CanToggle => Toggle is not null;

    public bool IsAdd => Add is not null;
    /// <summary>An <c>item</c> row: one entry in a list, removable with the ✕.</summary>
    public bool IsItem => Toggle is not null
        && string.Equals(State, "item", StringComparison.OrdinalIgnoreCase);

    // The view shows one of these affordances; they partition cleanly because Toggle is
    // non-null only for a literal on/off or an item, and Add only for an add row.
    public bool IsOn => CanToggle && !IsItem && string.Equals(State, "on", StringComparison.OrdinalIgnoreCase);
    public bool IsOff => CanToggle && !IsItem && !IsOn;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Informational rows: no affordance at all, just label and detail.</summary>
    public bool IsPlain => !CanToggle && !IsAdd;
}

/// <summary>A plugin's reported notification sources, grouped under its id.</summary>
public sealed record PluginNotifyGroup(string PluginId, IReadOnlyList<PluginNotifyRow> Rows);

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
    /// <summary>Persist a trigger's Notify flag and re-apply it live.</summary>
    private readonly Action<TriggerDef, bool>? _setTriggerNotify;
    /// <summary>Whether a change would actually be saved. Asked each refresh rather than once at
    /// construction, because the shell assigns the persistence hook AFTER building this panel —
    /// a snapshot taken in the constructor would always read "no".</summary>
    private readonly Func<bool>? _canPersistTriggers;
    private readonly Action<string> _copy;
    private readonly Action<Action<IReadOnlyList<(string PluginId, string Rows)>>>? _collectPluginSources;
    private readonly Action<string>? _runCommand;

    public ObservableCollection<NotifySourceRow> NotifySources { get; } = new();

    /// <summary>What each plugin says it will notify about, with tap-to-toggle rows.
    /// Rebuilt wholesale on refresh, so no row mutability is needed.</summary>
    public ObservableCollection<PluginNotifyGroup> PluginSources { get; } = new();

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
                              Action<string> copyToClipboard,
                              Action<Action<IReadOnlyList<(string PluginId, string Rows)>>>? collectPluginSources = null,
                              Action<TriggerDef, bool>? setTriggerNotify = null,
                              Func<bool>? canPersistTriggers = null,
                              Action<string>? runCommand = null)
    {
        _controller = controller;
        _sessionId = sessionId;
        _notifyingTriggers = notifyingTriggers;
        _setTriggerNotify = setTriggerNotify;
        _canPersistTriggers = canPersistTriggers;
        _copy = copyToClipboard;
        // Both are session-loop couriers: collect posts a state snapshot back to the UI
        // thread; runCommand submits through the input pipeline so plugin aliases fire.
        // The session mailbox is FIFO, which is what makes toggle-then-recollect race-free.
        _collectPluginSources = collectPluginSources;
        _runCommand = runCommand;

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
        RefreshPluginSources();
        _ = RefreshTailscaleAsync();
    }

    /// <summary>Ask the session loop for every <c>plugin.*.notify</c> value; the reply lands
    /// back on the UI thread via <see cref="ApplyPluginSources"/>.</summary>
    private void RefreshPluginSources() => _collectPluginSources?.Invoke(ApplyPluginSources);

    private void ApplyPluginSources(IReadOnlyList<(string PluginId, string Rows)> found)
    {
        PluginSources.Clear();
        foreach ((string pluginId, string rows) in found)
        {
            var parsed = new List<PluginNotifyRow>();
            foreach (string raw in rows.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] f = raw.Split('\t');
                string label = f[0].Trim();
                string detail = f.Length > 1 ? f[1].Trim() : "";
                string state = f.Length > 2 ? f[2].Trim() : "";
                string command = f.Length > 3 ? f[3].Trim() : "";

                // A command alone is never enough to earn an affordance: the state has to say
                // what KIND of control it is, or an unrecognised row would render as a mystery
                // button. Only on/off and item become clickable; only 'add' takes typed text.
                bool clickable = command.Length > 0
                    && (state.Equals("on", StringComparison.OrdinalIgnoreCase)
                        || state.Equals("off", StringComparison.OrdinalIgnoreCase)
                        || state.Equals("item", StringComparison.OrdinalIgnoreCase));
                bool addable = command.Length > 0
                    && state.Equals("add", StringComparison.OrdinalIgnoreCase)
                    && command.Contains(PluginNotifyRow.Placeholder, StringComparison.Ordinal);

                RelayCommand? toggle = clickable ? new RelayCommand(() => RunAndRefresh(command)) : null;
                RelayCommand<string>? add = addable
                    ? new RelayCommand<string>(text =>
                    {
                        // The typed text is a command ARGUMENT, so a newline in it would submit a
                        // second line of the user's choosing. Flatten it and refuse a blank.
                        string arg = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (arg.Length == 0) return;
                        RunAndRefresh(command.Replace(PluginNotifyRow.Placeholder, arg, StringComparison.Ordinal));
                    })
                    : null;
                parsed.Add(new PluginNotifyRow(label, detail, state, toggle) { Add = add });
            }
            if (parsed.Count > 0) PluginSources.Add(new PluginNotifyGroup(pluginId, parsed));
        }
        OnPropertyChanged(nameof(HasPluginSources));
        OnPropertyChanged(nameof(NotifySummary));
    }

    /// <summary>Send a command the way a plugin's own alias expects, then re-read the sources.
    /// The session mailbox is FIFO, so the collect queued here runs after the alias did and the
    /// snapshot already shows the result — no polling, no guessed delay.</summary>
    private void RunAndRefresh(string command)
    {
        _runCommand?.Invoke(command);
        RefreshPluginSources();
    }

    public bool HasPluginSources => PluginSources.Count > 0;

    private void RefreshNotifySources()
    {
        NotifySources.Clear();
        // Notifying first: the list answers "what will buzz my phone", and the rest are here
        // so you can switch one ON without leaving for the world editor.
        var all = new List<(TriggerDef Def, bool Enabled)>(_notifyingTriggers());
        all.Sort((a, b) =>
        {
            if (a.Def.Notify != b.Def.Notify) return a.Def.Notify ? -1 : 1;
            return string.Compare(a.Def.Name, b.Def.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach ((TriggerDef def, bool enabled) in all)
        {
            bool editable = _setTriggerNotify is not null && (_canPersistTriggers?.Invoke() ?? true);
            string why = !editable ? "read-only — a quick-connect world has no profile to save to"
                : string.IsNullOrWhiteSpace(def.Name) ? "unnamed — name it to change this here"
                : "";
            TriggerDef captured = def;
            NotifySources.Add(new NotifySourceRow(
                string.IsNullOrWhiteSpace(def.Name) ? "(unnamed)" : def.Name,
                def.Pattern,
                def.Group ?? "",
                enabled)
            {
                Notify = def.Notify,
                Why = why,
                SetNotify = why.Length > 0 ? null : on =>
                {
                    _setTriggerNotify!(captured, on);
                    // Rebuilding the list destroys the row whose setter is still on the stack,
                    // so let the binding finish first.
                    Dispatcher.UIThread.Post(RefreshNotifySources);
                },
            });
        }

        OnPropertyChanged(nameof(NotifySummary));
    }

    /// <summary>Plugins that follow the <c>plugin.&lt;id&gt;.notify</c> convention are listed
    /// with live toggles; the closing caveat stays because the convention is voluntary — a
    /// list that looked complete while a non-reporting plugin called <c>scrye.notify()</c>
    /// would be worse than one that admits the gap.</summary>
    public string NotifySummary
    {
        get
        {
            int on = 0;
            foreach (NotifySourceRow r in NotifySources) if (r.Notify) on++;
            string triggers = on == 0
                ? "No triggers in this world are set to Notify."
                : $"{on} trigger(s) set to Notify.";
            return HasPluginSources
                ? triggers + " Plugin sources below; plugins that don't report may still notify."
                : triggers + " Plugins may still notify on their own.";
        }
    }

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
            var outcome = await c.TestNotifyAsync();
            // The outcome sentence names the failure ("403 from web.push.apple.com: BadJwtToken")
            // instead of the old boolean shrug; the only case it can't express is "there was
            // nothing to send to", which gets its own hint because it's the most common one.
            Notice = outcome is { Delivered: 0, Failed: 0, Expired: 0 }
                ? "no devices are registered — tap 'Enable notifications' in the companion app on the phone"
                : outcome.ToString();
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
