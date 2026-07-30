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
    private readonly Action<string, string>? _invokeAction;   // (pluginId, actionId) → run on loop
    // State-watch subscriptions per plugin, so a reload/disable can dispose exactly its watches.
    // Only mutated on the construction thread (pre-loop) or the loop thread — never concurrently.
    private readonly Dictionary<string, List<IDisposable>> _pluginSubs = new();

    public ObservableCollection<HudPanelViewModel> Panels { get; } = new();

    public HudViewModel(StateStore state, Action<string, string>? invokeAction = null)
    {
        _state = state;
        _invokeAction = invokeAction;
    }

    /// <summary>Add a panel from a spec. Called during plugin load — on the UI thread at
    /// construction (pre-connect), or on the loop thread during a hot-reload. State watches
    /// register on the calling thread (safe: pre-loop or on-loop); only the <see cref="Panels"/>
    /// edit is marshalled to the UI.</summary>
    public void AddPanel(string pluginId, PanelSpec spec)
    {
        var panel = new HudPanelViewModel(string.IsNullOrWhiteSpace(spec.Title) ? pluginId : spec.Title, pluginId);
        var subs = new List<IDisposable>();
        foreach (WidgetSpec w in spec.Widgets) panel.Widgets.Add(BuildWidget(pluginId, w, subs));
        if (subs.Count > 0)
        {
            if (!_pluginSubs.TryGetValue(pluginId, out List<IDisposable>? list)) _pluginSubs[pluginId] = list = new();
            list.AddRange(subs);
        }
        Post(() => Panels.Add(panel));
    }

    /// <summary>Remove a plugin's panels + state-watches (on reload/disable). Subscription
    /// disposal runs on the caller's (loop) thread; the collection edit is marshalled to the UI.</summary>
    public void RemovePanels(string pluginId)
    {
        if (_pluginSubs.TryGetValue(pluginId, out List<IDisposable>? subs))
        {
            foreach (IDisposable s in subs) s.Dispose();
            _pluginSubs.Remove(pluginId);
        }
        Post(() =>
        {
            for (int i = Panels.Count - 1; i >= 0; i--)
                if (Panels[i].PluginId == pluginId) Panels.RemoveAt(i);
        });
    }

    private object BuildWidget(string pluginId, WidgetSpec w, List<IDisposable> subs)
    {
        switch ((w.Type ?? "label").ToLowerInvariant())
        {
            case "button":
            {
                string? actionId = w.Action;
                return new ButtonWidgetViewModel(w.Text ?? "Button",
                    () => { if (actionId is not null) _invokeAction?.Invoke(pluginId, actionId); });
            }
            case "progress":
            {
                var vm = new ProgressWidgetViewModel(w.Text ?? "");
                BindNumber(w.Value, v => vm.Value = v, subs);
                BindNumber(w.Max, v => vm.Maximum = v, subs);
                return vm;
            }
            case "value":
            {
                var vm = new LabelWidgetViewModel { Prefix = w.Text ?? "" };
                BindText(w.Bind, vm.SetValue, subs);
                return vm;
            }
            default: // "label"
            {
                var vm = new LabelWidgetViewModel { Text = w.Text ?? "" };
                if (!string.IsNullOrEmpty(w.Bind)) BindText(w.Bind, s => vm.Text = s, subs);
                return vm;
            }
        }
    }

    private void BindText(string? path, Action<string> set, List<IDisposable> subs)
    {
        if (string.IsNullOrEmpty(path)) return;
        set(_state.Get(path).Text);                          // seed the current value
        subs.Add(_state.Watch(path, (_, v) => Post(() => set(v.Text))));
    }

    private void BindNumber(string? pathOrLiteral, Action<double> set, List<IDisposable> subs)
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
        subs.Add(_state.Watch(pathOrLiteral, (_, v) => Post(() => set(ParseNum(v.Text)))));
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
        foreach (List<IDisposable> subs in _pluginSubs.Values)
            foreach (IDisposable s in subs) s.Dispose();
        _pluginSubs.Clear();
        Panels.Clear();
    }
}

/// <summary>One HUD panel: a title and a heterogeneous list of widget view-models
/// (rendered by type via DataTemplates). <see cref="PluginId"/> lets the host drop a
/// plugin's panels on reload/disable.</summary>
public sealed class HudPanelViewModel
{
    public string Title { get; }
    public string PluginId { get; }
    public ObservableCollection<object> Widgets { get; } = new();
    public HudPanelViewModel(string title, string pluginId) { Title = title; PluginId = pluginId; }
}

/// <summary>A clickable button widget: its <see cref="Command"/> invokes the plugin's callback.</summary>
public sealed class ButtonWidgetViewModel : ViewModelBase
{
    public string Text { get; }
    public RelayCommand Command { get; }
    public ButtonWidgetViewModel(string text, Action onClick)
    {
        Text = text;
        Command = new RelayCommand(onClick);
    }
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
