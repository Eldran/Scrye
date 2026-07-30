using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Scrye.Core.Automation;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

/// <summary>A key/value row in the global variables editor.</summary>
public sealed class VariableRowViewModel : ViewModelBase
{
    private string _key = "";
    public string Key { get => _key; set => SetField(ref _key, value); }
    private string _value = "";
    public string Value { get => _value; set => SetField(ref _value, value); }

    public VariableRowViewModel() { }
    public VariableRowViewModel(string key, string value) { _key = key; _value = value; }
}

/// <summary>Backs the global settings dialog: app-level appearance plus the
/// Global profile layer's triggers/aliases/timers/variables — the automation and
/// values that apply to every world via the cascade. Persisted to
/// <c>global.json</c> by <see cref="ProfileStore.SaveGlobal"/>.</summary>
public sealed class GlobalSettingsViewModel : ViewModelBase
{
    private readonly ProfileLayer _layer;

    public string Title => "Global Settings";

    // ---- appearance ----
    private string _fontFamily;
    public string FontFamily { get => _fontFamily; set => SetField(ref _fontFamily, value); }
    private string _fontSize;
    public string FontSize { get => _fontSize; set => SetField(ref _fontSize, value); }

    // ---- global rule sets + variables ----
    public ObservableCollection<TriggerRowViewModel> Triggers { get; } = new();
    public ObservableCollection<AliasRowViewModel> Aliases { get; } = new();
    public ObservableCollection<TimerRowViewModel> Timers { get; } = new();
    public ObservableCollection<SequenceRowViewModel> Sequences { get; } = new();
    public ObservableCollection<VariableRowViewModel> Variables { get; } = new();

    private TriggerRowViewModel? _selectedTrigger;
    public TriggerRowViewModel? SelectedTrigger { get => _selectedTrigger; set => SetField(ref _selectedTrigger, value); }
    private AliasRowViewModel? _selectedAlias;
    public AliasRowViewModel? SelectedAlias { get => _selectedAlias; set => SetField(ref _selectedAlias, value); }
    private TimerRowViewModel? _selectedTimer;
    public TimerRowViewModel? SelectedTimer { get => _selectedTimer; set => SetField(ref _selectedTimer, value); }
    private SequenceRowViewModel? _selectedSequence;
    public SequenceRowViewModel? SelectedSequence { get => _selectedSequence; set => SetField(ref _selectedSequence, value); }
    private VariableRowViewModel? _selectedVariable;
    public VariableRowViewModel? SelectedVariable { get => _selectedVariable; set => SetField(ref _selectedVariable, value); }

    public RelayCommand AddTriggerCommand { get; }
    public RelayCommand RemoveTriggerCommand { get; }
    public RelayCommand AddAliasCommand { get; }
    public RelayCommand RemoveAliasCommand { get; }
    public RelayCommand AddTimerCommand { get; }
    public RelayCommand RemoveTimerCommand { get; }
    public RelayCommand AddSequenceCommand { get; }
    public RelayCommand RemoveSequenceCommand { get; }
    public RelayCommand AddVariableCommand { get; }
    public RelayCommand RemoveVariableCommand { get; }

    public GlobalSettingsViewModel(ProfileLayer layer)
    {
        _layer = layer;
        _fontFamily = layer.FontFamily ?? "";
        _fontSize = layer.FontSize?.ToString(CultureInfo.InvariantCulture) ?? "";

        foreach (TriggerDef t in _layer.Triggers) Triggers.Add(new TriggerRowViewModel(t));
        foreach (AliasDef a in _layer.Aliases) Aliases.Add(new AliasRowViewModel(a));
        foreach (TimerDef tm in _layer.Timers) Timers.Add(new TimerRowViewModel(tm));
        foreach (SequenceSpec s in _layer.Sequences) Sequences.Add(new SequenceRowViewModel(s));
        foreach (KeyValuePair<string, string> kv in _layer.Variables) Variables.Add(new VariableRowViewModel(kv.Key, kv.Value));

        AddTriggerCommand = new RelayCommand(() =>
        {
            var row = new TriggerRowViewModel { Name = Unique("trigger", Triggers, r => r.Name), Pattern = "*" };
            Triggers.Add(row); SelectedTrigger = row;
        });
        RemoveTriggerCommand = new RelayCommand(() => { if (SelectedTrigger is not null) Triggers.Remove(SelectedTrigger); });

        AddAliasCommand = new RelayCommand(() =>
        {
            var row = new AliasRowViewModel { Name = Unique("alias", Aliases, r => r.Name) };
            Aliases.Add(row); SelectedAlias = row;
        });
        RemoveAliasCommand = new RelayCommand(() => { if (SelectedAlias is not null) Aliases.Remove(SelectedAlias); });

        AddTimerCommand = new RelayCommand(() =>
        {
            var row = new TimerRowViewModel { Name = Unique("timer", Timers, r => r.Name), IntervalText = "5" };
            Timers.Add(row); SelectedTimer = row;
        });
        RemoveTimerCommand = new RelayCommand(() => { if (SelectedTimer is not null) Timers.Remove(SelectedTimer); });

        AddSequenceCommand = new RelayCommand(() =>
        {
            var row = new SequenceRowViewModel { Name = Unique("walk", Sequences, r => r.Name), Source = "north; north; east" };
            Sequences.Add(row); SelectedSequence = row;
        });
        RemoveSequenceCommand = new RelayCommand(() => { if (SelectedSequence is not null) Sequences.Remove(SelectedSequence); });

        AddVariableCommand = new RelayCommand(() =>
        {
            var row = new VariableRowViewModel(Unique("var", Variables, r => r.Key), "");
            Variables.Add(row); SelectedVariable = row;
        });
        RemoveVariableCommand = new RelayCommand(() => { if (SelectedVariable is not null) Variables.Remove(SelectedVariable); });
    }

    private static string Unique<T>(string stem, ObservableCollection<T> rows, Func<T, string> nameOf)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (T r in rows) taken.Add(nameOf(r));
        for (int i = 1; ; i++)
            if (!taken.Contains($"{stem}{i}")) return $"{stem}{i}";
    }

    /// <summary>Fold appearance + rule sets + variables back into the global layer.</summary>
    public ProfileLayer ToLayer()
    {
        _layer.Kind = LayerKind.Global;
        _layer.Name = "global";
        _layer.FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? null : FontFamily.Trim();
        _layer.FontSize = double.TryParse(FontSize, NumberStyles.Any, CultureInfo.InvariantCulture, out double sz) && sz > 0
            ? sz : null;

        var triggers = new List<TriggerDef>();
        foreach (TriggerRowViewModel r in Triggers) if (!string.IsNullOrWhiteSpace(r.Name)) triggers.Add(r.ToDef());
        _layer.Triggers = triggers;

        var aliases = new List<AliasDef>();
        foreach (AliasRowViewModel r in Aliases) if (!string.IsNullOrWhiteSpace(r.Name)) aliases.Add(r.ToDef());
        _layer.Aliases = aliases;

        var timers = new List<TimerDef>();
        foreach (TimerRowViewModel r in Timers) if (!string.IsNullOrWhiteSpace(r.Name)) timers.Add(r.ToDef());
        _layer.Timers = timers;

        var sequences = new List<SequenceSpec>();
        foreach (SequenceRowViewModel r in Sequences) if (!string.IsNullOrWhiteSpace(r.Name)) sequences.Add(r.ToSpec());
        _layer.Sequences = sequences;

        var vars = new Dictionary<string, string>();
        foreach (VariableRowViewModel r in Variables)
            if (!string.IsNullOrWhiteSpace(r.Key)) vars[r.Key.Trim()] = r.Value ?? "";
        _layer.Variables = vars;

        return _layer;
    }
}
