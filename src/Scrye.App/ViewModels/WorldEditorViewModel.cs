using System.Collections.ObjectModel;
using Scrye.Core.Automation;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

/// <summary>Backs the world settings form: the scalar connection fields plus the
/// per-world triggers, aliases, and timers (edited as master/detail lists). All
/// of it folds back into the world's <see cref="ProfileLayer"/> on save.</summary>
public sealed class WorldEditorViewModel : ViewModelBase
{
    private readonly ProfileLayer _layer;

    public bool IsNew { get; }
    public string OriginalName { get; }
    public string Title => IsNew ? "New World" : "Edit World";

    private string _name;
    public string Name { get => _name; set => SetField(ref _name, value); }
    private string _host;
    public string Host { get => _host; set => SetField(ref _host, value); }
    private string _port;
    public string Port { get => _port; set => SetField(ref _port, value); }
    private string _username;
    public string Username { get => _username; set => SetField(ref _username, value); }
    private string _encoding;
    public string Encoding { get => _encoding; set => SetField(ref _encoding, value); }
    private bool _useTls;
    public bool UseTls { get => _useTls; set => SetField(ref _useTls, value); }
    private bool _enableMip;
    public bool EnableMip { get => _enableMip; set => SetField(ref _enableMip, value); }

    // ---- rule collections (master/detail) ----
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = new();
    public ObservableCollection<AliasRowViewModel> Aliases { get; } = new();
    public ObservableCollection<TimerRowViewModel> Timers { get; } = new();

    private TriggerRowViewModel? _selectedTrigger;
    public TriggerRowViewModel? SelectedTrigger { get => _selectedTrigger; set => SetField(ref _selectedTrigger, value); }
    private AliasRowViewModel? _selectedAlias;
    public AliasRowViewModel? SelectedAlias { get => _selectedAlias; set => SetField(ref _selectedAlias, value); }
    private TimerRowViewModel? _selectedTimer;
    public TimerRowViewModel? SelectedTimer { get => _selectedTimer; set => SetField(ref _selectedTimer, value); }

    public RelayCommand AddTriggerCommand { get; }
    public RelayCommand RemoveTriggerCommand { get; }
    public RelayCommand AddAliasCommand { get; }
    public RelayCommand RemoveAliasCommand { get; }
    public RelayCommand AddTimerCommand { get; }
    public RelayCommand RemoveTimerCommand { get; }

    public WorldEditorViewModel(string name, ProfileLayer? layer, bool isNew)
    {
        IsNew = isNew;
        OriginalName = name;
        _layer = layer ?? new ProfileLayer { Kind = LayerKind.Mud };
        _name = name;
        _host = _layer.Host ?? "";
        _port = (_layer.Port ?? 23).ToString();
        _username = _layer.Username ?? "";
        _encoding = _layer.EncodingName ?? "utf-8";
        _useTls = _layer.UseTls ?? false;
        _enableMip = _layer.EnableMip ?? false;

        foreach (TriggerDef t in _layer.Triggers) Triggers.Add(new TriggerRowViewModel(t));
        foreach (AliasDef a in _layer.Aliases) Aliases.Add(new AliasRowViewModel(a));
        foreach (TimerDef tm in _layer.Timers) Timers.Add(new TimerRowViewModel(tm));

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
        _layer.Kind = LayerKind.Mud;
        _layer.Name = Name;
        _layer.Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        _layer.Port = port > 0 ? port : 23;
        _layer.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        _layer.EncodingName = string.IsNullOrWhiteSpace(Encoding) ? null : Encoding.Trim();
        _layer.UseTls = UseTls ? true : null;
        _layer.AcceptInvalidCertificates = UseTls ? true : null;   // accept self-signed when TLS on
        _layer.EnableMip = EnableMip ? true : null;

        _layer.Triggers = BuildTriggers();
        _layer.Aliases = BuildAliases();
        _layer.Timers = BuildTimers();
        return _layer;
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
