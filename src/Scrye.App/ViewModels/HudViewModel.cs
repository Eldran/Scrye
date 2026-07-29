using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Scrye.Core.Plugins;
using Scrye.Core.State;

namespace Scrye.App.ViewModels;

/// <summary>
/// The per-world HUD: the set of declarative panels plugins contribute via
/// <c>scrye.addPanel</c> (Foundation D). Builds a panel's widgets from a
/// <see cref="PanelSpec"/> and keeps their bound values in sync with the session's
/// <see cref="StateStore"/>. State fires on the session loop thread, so updates are
/// marshalled to the UI thread. Watches are disposed with the world.
/// </summary>
public sealed class HudViewModel : IDisposable
{
    private readonly StateStore _state;
    private readonly List<IDisposable> _subscriptions = new();

    public ObservableCollection<HudPanelViewModel> Panels { get; } = new();

    public HudViewModel(StateStore state) => _state = state;

    /// <summary>Called (on the UI thread, during plugin load) to add a panel from a spec.</summary>
    public void AddPanel(string pluginId, PanelSpec spec)
    {
        var panel = new HudPanelViewModel(string.IsNullOrWhiteSpace(spec.Title) ? pluginId : spec.Title);
        foreach (WidgetSpec w in spec.Widgets) panel.Widgets.Add(BuildWidget(w));
        Panels.Add(panel);
    }

    private object BuildWidget(WidgetSpec w)
    {
        switch ((w.Type ?? "label").ToLowerInvariant())
        {
            case "progress":
            {
                var vm = new ProgressWidgetViewModel(w.Text ?? "");
                BindNumber(w.Value, v => vm.Value = v);
                BindNumber(w.Max, v => vm.Maximum = v);
                return vm;
            }
            case "value":
            {
                var vm = new LabelWidgetViewModel { Prefix = w.Text ?? "" };
                BindText(w.Bind, vm.SetValue);
                return vm;
            }
            default: // "label"
            {
                var vm = new LabelWidgetViewModel { Text = w.Text ?? "" };
                if (!string.IsNullOrEmpty(w.Bind)) BindText(w.Bind, s => vm.Text = s);
                return vm;
            }
        }
    }

    private void BindText(string? path, Action<string> set)
    {
        if (string.IsNullOrEmpty(path)) return;
        set(_state.Get(path).Text);                          // seed (we're on the UI thread here)
        _subscriptions.Add(_state.Watch(path, (_, v) => Post(() => set(v.Text))));
    }

    private void BindNumber(string? pathOrLiteral, Action<double> set)
    {
        if (string.IsNullOrEmpty(pathOrLiteral)) return;
        // A purely-numeric string that isn't an existing path is a literal (e.g. max = "100").
        if (double.TryParse(pathOrLiteral, NumberStyles.Any, CultureInfo.InvariantCulture, out double literal)
            && !_state.Has(pathOrLiteral))
        {
            set(literal);
            return;
        }
        set(ParseNum(_state.Get(pathOrLiteral).Text));
        _subscriptions.Add(_state.Watch(pathOrLiteral, (_, v) => Post(() => set(ParseNum(v.Text)))));
    }

    private static double ParseNum(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        foreach (IDisposable s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        Panels.Clear();
    }
}

/// <summary>One HUD panel: a title and a heterogeneous list of widget view-models
/// (rendered by type via DataTemplates).</summary>
public sealed class HudPanelViewModel
{
    public string Title { get; }
    public ObservableCollection<object> Widgets { get; } = new();
    public HudPanelViewModel(string title) => Title = title;
}

/// <summary>A text widget: static text, or a prefix + a live bound value.</summary>
public sealed class LabelWidgetViewModel : ViewModelBase
{
    public string Prefix { get; set; } = "";

    private string _text = "";
    public string Text { get => _text; set => SetField(ref _text, value); }

    /// <summary>Set the bound portion; the displayed text is <see cref="Prefix"/> + value.</summary>
    public void SetValue(string value) => Text = Prefix + value;
}

/// <summary>A progress bar widget bound to a current value and a maximum.</summary>
public sealed class ProgressWidgetViewModel : ViewModelBase
{
    public string Label { get; }
    public ProgressWidgetViewModel(string label) => Label = label;

    private double _value;
    public double Value
    {
        get => _value;
        set { if (SetField(ref _value, value)) OnPropertyChanged(nameof(Caption)); }
    }

    private double _maximum = 100;
    public double Maximum
    {
        get => _maximum;
        set { if (SetField(ref _maximum, value <= 0 ? 1 : value)) OnPropertyChanged(nameof(Caption)); }
    }

    public string Caption => $"{_value:0}/{_maximum:0}";
}
