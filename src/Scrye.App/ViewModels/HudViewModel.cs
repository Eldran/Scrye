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
    private readonly Action<string, string, int, int, string>? _invokeCellAction;  // (pluginId, actionId, col, row, char)
    private readonly Action<string, string, string>? _invokeSubmit;  // (pluginId, actionId, text) → input widget submit
    // State-watch subscriptions per plugin, so a reload/disable can dispose exactly its watches.
    // Only mutated on the construction thread (pre-loop) or the loop thread — never concurrently.
    private readonly Dictionary<string, List<IDisposable>> _pluginSubs = new();

    public ObservableCollection<HudPanelViewModel> Panels { get; } = new();

    /// <summary>Saved canvas position for a panel key (pluginId|title), or null. Set by the
    /// world before plugins load so restored panels come back where the user dragged them.</summary>
    public Func<string, (double X, double Y)?>? LoadPosition { get; set; }

    /// <summary>Raised after the user drags a panel (persist the layout).</summary>
    public Action? PanelMoved { get; set; }

    public HudViewModel(StateStore state, Action<string, string>? invokeAction = null,
                        Action<string, string, int, int, string>? invokeCellAction = null,
                        Action<string, string, string>? invokeSubmit = null)
    {
        _state = state;
        _invokeAction = invokeAction;
        _invokeCellAction = invokeCellAction;
        _invokeSubmit = invokeSubmit;
    }

    /// <summary>Add a panel from a spec. Called during plugin load — on the UI thread at
    /// construction (pre-connect), or on the loop thread during a hot-reload. State watches
    /// register on the calling thread (safe: pre-loop or on-loop); only the <see cref="Panels"/>
    /// edit is marshalled to the UI.</summary>
    public void AddPanel(string pluginId, PanelSpec spec)
    {
        var panel = new HudPanelViewModel(string.IsNullOrWhiteSpace(spec.Title) ? pluginId : spec.Title, pluginId)
        {
            Width = spec.Width > 0 ? spec.Width : 220,
            BackgroundBrush = HudColor.Brush(spec.Background),
            AccentBrush = HudColor.Brush(spec.Accent),
        };
        if (LoadPosition?.Invoke(panel.Key) is (double px, double py)) { panel.X = px; panel.Y = py; }
        panel.Moved = _ => PanelMoved?.Invoke();
        string? panelFg = spec.Foreground;   // default text colour for widgets that don't set their own
        var subs = new List<IDisposable>();
        if (spec.Tabs.Count > 0)
        {
            foreach (PanelTabSpec tab in spec.Tabs)
            {
                var tabVm = new HudTabViewModel(tab.Title);
                foreach (WidgetSpec w in tab.Widgets) tabVm.Widgets.Add(BuildWidget(pluginId, w, subs, panelFg));
                panel.Tabs.Add(tabVm);
            }
        }
        else
        {
            foreach (WidgetSpec w in spec.Widgets) panel.Widgets.Add(BuildWidget(pluginId, w, subs, panelFg));
        }
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

    private object BuildWidget(string pluginId, WidgetSpec w, List<IDisposable> subs, string? panelFg = null)
    {
        // text-bearing widgets fall back to the panel's default foreground when they set no colour
        string? textColor = w.Color ?? panelFg;
        switch ((w.Type ?? "label").ToLowerInvariant())
        {
            case "button":
            {
                string? actionId = w.Action;
                return new ButtonWidgetViewModel(w.Text ?? "Button",
                    () => { if (actionId is not null) _invokeAction?.Invoke(pluginId, actionId); });
            }
            case "buttonrow":
            {
                var row = new ButtonRowWidgetViewModel();
                if (w.Children is not null)
                    foreach (WidgetSpec child in w.Children)
                    {
                        string? childAction = child.Action;
                        row.Buttons.Add(new ButtonWidgetViewModel(child.Text ?? "Button",
                            () => { if (childAction is not null) _invokeAction?.Invoke(pluginId, childAction); }));
                    }
                return row;
            }
            case "progress":
            {
                var vm = new ProgressWidgetViewModel(w.Text ?? "", w.Color);   // bar honours explicit colour only
                BindNumber(w.Value, v => vm.Value = v, subs);
                BindNumber(w.Max, v => vm.Maximum = v, subs);
                return vm;
            }
            case "value":
            {
                var vm = new LabelWidgetViewModel(textColor) { Prefix = w.Text ?? "" };
                BindText(w.Bind, vm.SetValue, subs);
                return vm;
            }
            case "gauge":
            {
                var vm = new GaugeWidgetViewModel(w.Text ?? "", w.Color, w.Dim);   // dim: darken as value drops
                BindNumber(w.Value, v => vm.Value = v, subs);
                BindNumber(w.Max, v => vm.Maximum = v, subs);
                return vm;
            }
            case "text":
            {
                var vm = new TextWidgetViewModel(textColor);
                BindText(w.Bind, s => vm.Text = s, subs);
                return vm;
            }
            case "barlist":
            {
                var vm = new BarListWidgetViewModel();
                BindText(w.Bind, s => vm.Rows = s, subs);
                return vm;
            }
            case "input":
            {
                string? actionId = w.Action;
                var vm = new InputWidgetViewModel(w.Text ?? "",
                    text => { if (actionId is not null) _invokeSubmit?.Invoke(pluginId, actionId, text); });
                BindText(w.Bind, vm.SetValue, subs);   // seed + track the current value
                return vm;
            }
            case "colorgrid":
            {
                var vm = new ColorGridWidgetViewModel(w.Palette);
                if (!string.IsNullOrEmpty(w.Action))
                {
                    string actionId = w.Action;
                    vm.CellCommand = new RelayCommand<Controls.GridCell>(cell =>
                        _invokeCellAction?.Invoke(pluginId, actionId, cell.Col, cell.Row, cell.Ch.ToString()));
                }
                BindText(w.Bind, s => vm.GridText = s, subs);
                return vm;
            }
            default: // "label"
            {
                var vm = new LabelWidgetViewModel(textColor) { Text = w.Text ?? "" };
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
        // Guarded: a plugin feeding bad widget data must never crash the whole client —
        // log it and carry on. (Posted actions run on the UI thread where a throw is fatal.)
        void Safe() => Services.CrashLog.Guard("Hud widget update", action);
        if (Dispatcher.UIThread.CheckAccess()) Safe();
        else Dispatcher.UIThread.Post(Safe);
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
/// (rendered by type via DataTemplates) — or a set of <see cref="Tabs"/> when the
/// spec is tabbed. <see cref="PluginId"/> lets the host drop a plugin's panels on
/// reload/disable.</summary>
public sealed class HudPanelViewModel
{
    public string Title { get; }
    public string PluginId { get; }
    public ObservableCollection<object> Widgets { get; } = new();
    public ObservableCollection<HudTabViewModel> Tabs { get; } = new();
    public bool HasTabs => Tabs.Count > 0;
    public double Width { get; init; } = 220;

    /// <summary>Plugin-chosen panel background / accent (border + title). Null = follow the theme.
    /// Set from PanelSpec.Background / .Accent; the XAML overrides the themed defaults when present.</summary>
    public Avalonia.Media.IBrush? BackgroundBrush { get; init; }
    public bool HasBackground => BackgroundBrush is not null;
    public Avalonia.Media.IBrush? AccentBrush { get; init; }
    public bool HasAccent => AccentBrush is not null;

    /// <summary>Stable key for saved-position lookup across reloads/restarts.</summary>
    public string Key => PluginId + "|" + Title;

    /// <summary>Canvas position. NaN = not yet placed — the HUD surface assigns a default
    /// (stacked down the right edge) on first layout; a drag overwrites it.</summary>
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;

    /// <summary>Set by <see cref="HudViewModel"/>; the drag behavior calls <see cref="ReportMoved"/>.</summary>
    internal Action<HudPanelViewModel>? Moved;
    public void ReportMoved() => Moved?.Invoke(this);

    public HudPanelViewModel(string title, string pluginId) { Title = title; PluginId = pluginId; }
}

/// <summary>One tab in a tabbed HUD panel.</summary>
public sealed class HudTabViewModel
{
    public string Title { get; }
    public ObservableCollection<object> Widgets { get; } = new();
    public HudTabViewModel(string title) => Title = title;
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

/// <summary>A row of buttons rendered side by side as equal-width columns (a "buttonrow" widget).</summary>
public sealed class ButtonRowWidgetViewModel : ViewModelBase
{
    public System.Collections.ObjectModel.ObservableCollection<ButtonWidgetViewModel> Buttons { get; } = new();
}

/// <summary>An inline text field with a label; pressing Enter (or the Set button) submits the
/// current text to the plugin. <see cref="Prefix"/> labels it; <see cref="Text"/> is two-way and
/// seeded from the widget's bound state so it shows the current value.</summary>
public sealed class InputWidgetViewModel : ViewModelBase
{
    private readonly Action<string> _submit;
    public string Prefix { get; }

    private string _text = "";
    public string Text { get => _text; set => SetField(ref _text, value); }

    public RelayCommand SubmitCommand { get; }

    public InputWidgetViewModel(string prefix, Action<string> submit)
    {
        Prefix = prefix;
        _submit = submit;
        SubmitCommand = new RelayCommand(() => _submit(_text ?? ""));
    }

    /// <summary>Update the displayed value from state without firing a submit.</summary>
    public void SetValue(string v) => Text = v ?? "";
}

/// <summary>Parses "#RRGGBB" into a brush; null for empty/invalid (so the theme colour shows).</summary>
internal static class HudColor
{
    // IMMUTABLE brush: HUD widgets are built on the session-loop thread (scrye.addPanel
    // runs there), but rendered on the UI thread. A mutable SolidColorBrush has thread
    // affinity and Avalonia 12 throws a cross-thread error when the compositor touches it;
    // immutable brushes are frozen and safe to use from any thread.
    public static Avalonia.Media.IBrush? Brush(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' &&
            uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return new Avalonia.Media.Immutable.ImmutableSolidColorBrush(
                Avalonia.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
        return null;
    }
}

/// <summary>A text widget: static text, or a prefix + a live bound value. An optional
/// "#RRGGBB" colour overrides the theme foreground (see <see cref="HasColor"/>).</summary>
public sealed class LabelWidgetViewModel : ViewModelBase
{
    public string Prefix { get; set; } = "";

    /// <summary>Custom foreground brush, or null to follow the theme.</summary>
    public Avalonia.Media.IBrush? ColorBrush { get; }
    public bool HasColor => ColorBrush is not null;

    public LabelWidgetViewModel(string? colorHex = null) => ColorBrush = HudColor.Brush(colorHex);

    private string _text = "";
    public string Text { get => _text; set => SetField(ref _text, value); }

    /// <summary>Set the bound portion; the displayed text is <see cref="Prefix"/> + value.</summary>
    public void SetValue(string value) => Text = Prefix + value;
}

/// <summary>A labelled gauge: current/max readout inside the bar, fill colour shifting
/// with the percentage (cyan healthy → amber warning → red critical).</summary>
public sealed class GaugeWidgetViewModel : ViewModelBase
{
    // immutable (thread-safe) — built off the UI thread during plugin load
    private static readonly Avalonia.Media.IBrush Healthy =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0x35, 0xC4, 0xD6));
    private static readonly Avalonia.Media.IBrush Warning =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0xA8, 0x30));
    private static readonly Avalonia.Media.IBrush Critical =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0x50, 0x50));

    public string Label { get; }
    private readonly Avalonia.Media.IBrush? _custom;   // plugin-chosen bar colour (overrides the gradient)
    private readonly bool _dim;                         // dim mode: bar darkens as the value drops
    private readonly Avalonia.Media.Color _base;        // base hue for dim mode

    public GaugeWidgetViewModel(string label, string? colorHex = null, bool dim = false)
    {
        Label = label;
        _dim = dim;
        _base = ParseColor(colorHex) ?? Avalonia.Media.Color.FromRgb(0x46, 0xB4, 0x5A);  // default green
        _custom = dim ? null : HudColor.Brush(colorHex);   // fixed colour only when not dimming
    }

    private static Avalonia.Media.Color? ParseColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' &&
        uint.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? Avalonia.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : (Avalonia.Media.Color?)null;

    private double _value;
    public double Value
    {
        get => _value;
        set { if (SetField(ref _value, value)) Changed(); }
    }

    private double _maximum = 100;
    public double Maximum
    {
        get => _maximum;
        set { if (SetField(ref _maximum, value <= 0 ? 1 : value)) Changed(); }
    }

    public string Caption => $"{_value:0}/{_maximum:0}";
    public Avalonia.Media.IBrush BarBrush
    {
        get
        {
            double r = System.Math.Clamp(_maximum > 0 ? _value / _maximum : 0, 0, 1);
            if (_dim)
            {
                double b = 0.30 + 0.70 * r;   // brightness 30% (empty) → 100% (full)
                return new Avalonia.Media.Immutable.ImmutableSolidColorBrush(
                    Avalonia.Media.Color.FromRgb((byte)(_base.R * b), (byte)(_base.G * b), (byte)(_base.B * b)));
            }
            return _custom ?? (r >= 0.5 ? Healthy : r >= 0.25 ? Warning : Critical);
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(BarBrush));
    }
}

/// <summary>A multi-line monospace text block bound to a state path (plugins compose
/// whole report sections into one path). Optional "#RRGGBB" foreground override.</summary>
public sealed class TextWidgetViewModel : ViewModelBase
{
    public Avalonia.Media.IBrush Foreground { get; }

    public TextWidgetViewModel(string? colorHex)
    {
        Avalonia.Media.Color c = Avalonia.Media.Color.FromRgb(0xD6, 0xDE, 0xE8);
        if (colorHex is { Length: 7 } && colorHex[0] == '#' &&
            uint.TryParse(colorHex[1..], System.Globalization.NumberStyles.HexNumber, null, out uint hex))
            c = Avalonia.Media.Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        Foreground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c);   // immutable: built off the UI thread
    }

    private string _text = "";
    public string Text { get => _text; set => SetField(ref _text, value); }
}

/// <summary>A dynamic list of labelled "fill × quality" bars. <see cref="Rows"/> is a
/// newline-separated string; each line is <c>label \t caption \t value \t max \t refined</c>.
/// Rendered by <c>BarListView</c>.</summary>
public sealed class BarListWidgetViewModel : ViewModelBase
{
    private string _rows = "";
    public string Rows { get => _rows; set => SetField(ref _rows, value); }
}

/// <summary>A grid of coloured cells: newline-separated rows of characters, coloured
/// via the palette (char → colour). Rendered by <c>Controls.ColorGridView</c>.</summary>
public sealed class ColorGridWidgetViewModel : ViewModelBase
{
    public Dictionary<char, Avalonia.Media.Color> Palette { get; }

    /// <summary>Set when the colorgrid is clickable — bound to ColorGridView.CellCommand.</summary>
    public System.Windows.Input.ICommand? CellCommand { get; set; }

    public ColorGridWidgetViewModel(IReadOnlyDictionary<string, string>? palette)
    {
        Palette = new Dictionary<char, Avalonia.Media.Color>();
        if (palette is not null)
            foreach ((string key, string val) in palette)
                if (key.Length >= 1 && val is { Length: 7 } && val[0] == '#' &&
                    uint.TryParse(val[1..], System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                    Palette[key[0]] = Avalonia.Media.Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    }

    private string _gridText = "";
    public string GridText { get => _gridText; set => SetField(ref _gridText, value); }
}

/// <summary>A progress bar widget bound to a current value and a maximum. An optional
/// "#RRGGBB" colour overrides the theme accent for the bar fill.</summary>
public sealed class ProgressWidgetViewModel : ViewModelBase
{
    public string Label { get; }
    public Avalonia.Media.IBrush? ColorBrush { get; }
    public bool HasColor => ColorBrush is not null;

    public ProgressWidgetViewModel(string label, string? colorHex = null)
    {
        Label = label;
        ColorBrush = HudColor.Brush(colorHex);
    }

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
