using System.Collections.ObjectModel;
using Scrye.Core.Automation;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

/// <summary>Backs the layer settings form: the scalar connection fields plus the
/// layer's triggers, aliases, and timers (edited as master/detail lists). Edits ONE
/// <see cref="ProfileLayer"/> — a MUD, an account, or a character — identified by
/// <see cref="TargetKind"/> + <see cref="ParentMud"/>/<see cref="ParentAccount"/>;
/// empty scalar fields stay null and inherit from the parent layers.</summary>
public sealed class WorldEditorViewModel : ViewModelBase
{
    private readonly ProfileLayer _layer;

    public bool IsNew { get; }
    public string OriginalName { get; }

    public LayerKind TargetKind { get; }
    /// <summary>The MUD this account/character belongs to (null when editing a MUD).</summary>
    public string? ParentMud { get; }
    /// <summary>The account this character belongs to (null = directly on the MUD).</summary>
    public string? ParentAccount { get; }

    private string KindLabel => TargetKind switch
    {
        LayerKind.Account => "Account",
        LayerKind.Character => "Character",
        _ => "MUD",
    };

    public string Title => (IsNew ? "New " : "Edit ") + KindLabel;

    public string Subtitle => TargetKind switch
    {
        LayerKind.Account =>
            $"Account on {ParentMud} — settings and rules here are shared by every character in this account. Empty fields inherit from the MUD.",
        LayerKind.Character => ParentAccount is null
            ? $"Character on {ParentMud} — empty fields inherit from the MUD."
            : $"Character in {ParentAccount} on {ParentMud} — empty fields inherit from the account and MUD.",
        _ => "Connection, shared triggers and rules for everyone on this MUD.",
    };

    private string _name;
    public string Name { get => _name; set => SetField(ref _name, value); }
    private string _host;
    public string Host { get => _host; set => SetField(ref _host, value); }
    private string _port;
    public string Port { get => _port; set => SetField(ref _port, value); }
    private string _username;
    public string Username { get => _username; set => SetField(ref _username, value); }

    /// <summary>New password to store for auto-login. Never pre-filled; blank = keep the
    /// existing stored secret. Saved to the OS credential store, not the profile json.</summary>
    private string _password = "";
    public string Password { get => _password; set => SetField(ref _password, value); }

    /// <summary>The layer's existing credential-store key (null = none stored yet).</summary>
    public string? ExistingPasswordRef { get; }

    public string PasswordHint => ExistingPasswordRef is not null
        ? "a password is stored — leave blank to keep it"
        : "stored in Windows Credential Manager, not in the profile file";
    private string _encoding;
    public string Encoding { get => _encoding; set => SetField(ref _encoding, value); }
    private bool _useTls;
    public bool UseTls { get => _useTls; set => SetField(ref _useTls, value); }
    private bool _enableMip;
    public bool EnableMip { get => _enableMip; set => SetField(ref _enableMip, value); }
    private bool _enableMxp;
    public bool EnableMxp { get => _enableMxp; set => SetField(ref _enableMxp, value); }

    // ---- rule collections (master/detail) ----
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = new();
    public ObservableCollection<AliasRowViewModel> Aliases { get; } = new();
    public ObservableCollection<TimerRowViewModel> Timers { get; } = new();
    public ObservableCollection<SequenceRowViewModel> Sequences { get; } = new();

    private TriggerRowViewModel? _selectedTrigger;
    public TriggerRowViewModel? SelectedTrigger { get => _selectedTrigger; set => SetField(ref _selectedTrigger, value); }
    private AliasRowViewModel? _selectedAlias;
    public AliasRowViewModel? SelectedAlias { get => _selectedAlias; set => SetField(ref _selectedAlias, value); }
    private TimerRowViewModel? _selectedTimer;
    public TimerRowViewModel? SelectedTimer { get => _selectedTimer; set => SetField(ref _selectedTimer, value); }
    private SequenceRowViewModel? _selectedSequence;
    public SequenceRowViewModel? SelectedSequence { get => _selectedSequence; set => SetField(ref _selectedSequence, value); }

    public RelayCommand AddTriggerCommand { get; }
    public RelayCommand RemoveTriggerCommand { get; }
    public RelayCommand AddAliasCommand { get; }
    public RelayCommand RemoveAliasCommand { get; }
    public RelayCommand AddTimerCommand { get; }
    public RelayCommand RemoveTimerCommand { get; }
    public RelayCommand AddSequenceCommand { get; }
    public RelayCommand RemoveSequenceCommand { get; }

    public WorldEditorViewModel(string name, ProfileLayer? layer, bool isNew,
                                LayerKind kind = LayerKind.Mud,
                                string? parentMud = null, string? parentAccount = null)
    {
        IsNew = isNew;
        OriginalName = name;
        TargetKind = kind;
        ParentMud = parentMud;
        ParentAccount = parentAccount;
        _layer = layer ?? new ProfileLayer { Kind = kind };
        ExistingPasswordRef = _layer.PasswordRef;
        _name = name;
        _host = _layer.Host ?? "";
        // MUD layers get a concrete default port; deeper layers stay blank = inherit.
        _port = _layer.Port?.ToString() ?? (kind == LayerKind.Mud ? "23" : "");
        _username = _layer.Username ?? "";
        _encoding = _layer.EncodingName ?? (kind == LayerKind.Mud ? "utf-8" : "");
        _useTls = _layer.UseTls ?? false;
        _enableMip = _layer.EnableMip ?? false;
        _enableMxp = _layer.EnableMxp ?? true;   // on by default; negotiation-gated anyway

        foreach (TriggerDef t in _layer.Triggers) Triggers.Add(new TriggerRowViewModel(t));
        foreach (AliasDef a in _layer.Aliases) Aliases.Add(new AliasRowViewModel(a));
        foreach (TimerDef tm in _layer.Timers) Timers.Add(new TimerRowViewModel(tm));
        foreach (SequenceSpec s in _layer.Sequences) Sequences.Add(new SequenceRowViewModel(s));

        AddTriggerCommand = new RelayCommand(() =>
        {
            var row = new TriggerRowViewModel { Name = UniqueName("trigger", Triggers, r => r.Name), Pattern = "*" };
            Triggers.Add(row); SelectedTrigger = row;
        });
        RemoveTriggerCommand = new RelayCommand(() => { if (SelectedTrigger is not null) Triggers.Remove(SelectedTrigger); });

        AddAliasCommand = new RelayCommand(() =>
        {
            var row = new AliasRowViewModel { Name = UniqueName("alias", Aliases, r => r.Name), Pattern = "" };
            Aliases.Add(row); SelectedAlias = row;
        });
        RemoveAliasCommand = new RelayCommand(() => { if (SelectedAlias is not null) Aliases.Remove(SelectedAlias); });

        AddTimerCommand = new RelayCommand(() =>
        {
            var row = new TimerRowViewModel { Name = UniqueName("timer", Timers, r => r.Name), IntervalText = "5" };
            Timers.Add(row); SelectedTimer = row;
        });
        RemoveTimerCommand = new RelayCommand(() => { if (SelectedTimer is not null) Timers.Remove(SelectedTimer); });

        AddSequenceCommand = new RelayCommand(() =>
        {
            var row = new SequenceRowViewModel { Name = UniqueName("walk", Sequences, r => r.Name), Source = "north; north; east" };
            Sequences.Add(row); SelectedSequence = row;
        });
        RemoveSequenceCommand = new RelayCommand(() => { if (SelectedSequence is not null) Sequences.Remove(SelectedSequence); });
    }

    private static string UniqueName<T>(string stem, ObservableCollection<T> rows, System.Func<T, string> nameOf)
    {
        var taken = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (T r in rows) taken.Add(nameOf(r));
        for (int i = 1; ; i++)
        {
            string candidate = $"{stem}{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Fold every edited field — scalars and rule lists — back into the layer.</summary>
    public ProfileLayer ToLayer()
    {
        int.TryParse(Port, out int port);
        _layer.Kind = TargetKind;
        _layer.Name = Name;
        _layer.Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        // Blank port on an account/character layer stays null and inherits.
        _layer.Port = port > 0 ? port : (TargetKind == LayerKind.Mud ? 23 : null);
        _layer.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        _layer.EncodingName = string.IsNullOrWhiteSpace(Encoding) ? null : Encoding.Trim();
        _layer.UseTls = UseTls ? true : null;
        _layer.AcceptInvalidCertificates = UseTls ? true : null;   // accept self-signed when TLS on
        _layer.EnableMip = EnableMip ? true : null;
        _layer.EnableMxp = EnableMxp ? null : false;   // default-on: only an explicit OFF is stored

        _layer.Triggers = BuildTriggers();
        _layer.Aliases = BuildAliases();
        _layer.Timers = BuildTimers();
        _layer.Sequences = BuildSequences();
        return _layer;
    }

    private System.Collections.Generic.List<SequenceSpec> BuildSequences()
    {
        var list = new System.Collections.Generic.List<SequenceSpec>();
        foreach (SequenceRowViewModel r in Sequences)
            if (!string.IsNullOrWhiteSpace(r.Name)) list.Add(r.ToSpec());
        return list;
    }

    private System.Collections.Generic.List<TriggerDef> BuildTriggers()
    {
        var list = new System.Collections.Generic.List<TriggerDef>();
        foreach (TriggerRowViewModel r in Triggers)
            if (!string.IsNullOrWhiteSpace(r.Name)) list.Add(r.ToDef());
        return list;
    }

    private System.Collections.Generic.List<AliasDef> BuildAliases()
    {
        var list = new System.Collections.Generic.List<AliasDef>();
        foreach (AliasRowViewModel r in Aliases)
            if (!string.IsNullOrWhiteSpace(r.Name)) list.Add(r.ToDef());
        return list;
    }

    private System.Collections.Generic.List<TimerDef> BuildTimers()
    {
        var list = new System.Collections.Generic.List<TimerDef>();
        foreach (TimerRowViewModel r in Timers)
            if (!string.IsNullOrWhiteSpace(r.Name)) list.Add(r.ToDef());
        return list;
    }
}
