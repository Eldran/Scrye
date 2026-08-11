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
    private readonly Action<string, string, string, int>? _invokeChoice;  // (pluginId, actionId, label, index) → bound buttonrow
    private readonly Action<string, bool>? _runCommand;   // (command, prompt) → a click= run in widget text
    // State-watch subscriptions per PANEL (keyed pluginId|title), not per plugin: rebuilding one
    // panel must drop exactly that panel's watches and leave its siblings alone. RemovePanels
    // still disposes a whole plugin's worth by matching the key prefix.
    // Only mutated on the construction thread (pre-loop) or the loop thread — never concurrently.
    private readonly Dictionary<string, List<IDisposable>> _panelSubs = new(StringComparer.Ordinal);

    // Panel view models by key, mutated on the same thread as _specs. The Panels collection is
    // only touched on the UI thread, so a rebuild cannot safely search it — this index is how the
    // loop thread finds the panel it is replacing.
    private readonly Dictionary<string, HudPanelViewModel> _panelsByKey = new(StringComparer.Ordinal);

    public ObservableCollection<HudPanelViewModel> Panels { get; } = new();

    // The original specs, kept alongside the rendered panels. AddPanel turns a spec into
    // view models and would otherwise discard it — but the companion streams the *spec*, not
    // the rendering, so a mobile client can draw the panel itself (companion design §2).
    private readonly Dictionary<string, PanelSpec> _specs = new(StringComparer.Ordinal);

    /// <summary>Live panel specs by panel key (<c>pluginId|title</c>), for companion
    /// snapshots. Mutated on the same threads as <see cref="_panelSubs"/>.</summary>
    public IReadOnlyDictionary<string, PanelSpec> PanelSpecs => _specs;

    /// <summary>Raised with (panelKey, spec) when a panel is built, so a companion server can
    /// stream it. Null when no companion server is running.</summary>
    public Action<string, PanelSpec>? PanelAdded { get; set; }

    /// <summary>Raised with the panel key when a panel goes away (plugin disabled or reloaded).</summary>
    public Action<string>? PanelRemoved { get; set; }

    /// <summary>Saved canvas placement for a panel key (pluginId|title), or null. Set by the
    /// world before plugins load so restored panels come back where the user dragged them.
    /// W/H are the user-resized size; 0 = never resized (the panel auto-sizes).</summary>
    public Func<string, (double X, double Y, double W, double H)?>? LoadPosition { get; set; }

    /// <summary>Raised after the user drags or resizes a panel (persist the layout).</summary>
    public Action? PanelMoved { get; set; }

    public HudViewModel(StateStore state, Action<string, string>? invokeAction = null,
                        Action<string, string, int, int, string>? invokeCellAction = null,
                        Action<string, string, string>? invokeSubmit = null,
                        Action<string, string, string, int>? invokeChoice = null,
                        Action<string, bool>? runCommand = null)
    {
        _state = state;
        _invokeAction = invokeAction;
        _invokeCellAction = invokeCellAction;
        _invokeSubmit = invokeSubmit;
        _invokeChoice = invokeChoice;
        _runCommand = runCommand;
    }

    /// <summary>
    /// Add a panel from a spec, or replace one that already exists under the same key.
    ///
    /// <para><b>Replacing is the point.</b> A panel's widget set is fixed at build time, so a
    /// plugin that needs to offer something it could not know at load — a town list that arrives
    /// with the feed, the resolve options for the node you are standing on — used to have no way
    /// to say so. Calling <c>scrye.addPanel</c> again with the same title now rebuilds that panel
    /// in place: same view model, so the canvas position and any drag survive, and the companion
    /// gets the new spec under the same id (its client replaces by key already).</para>
    ///
    /// <para>Called during plugin load on the UI thread (pre-connect) or on the loop thread during
    /// a hot-reload or a live rebuild. State watches register on the calling thread; only the
    /// collection edits are marshalled to the UI.</para>
    /// </summary>
    public void AddPanel(string pluginId, PanelSpec spec)
    {
        string title = string.IsNullOrWhiteSpace(spec.Title) ? pluginId : spec.Title;
        string key = pluginId + "|" + title;
        bool rebuild = _panelsByKey.TryGetValue(key, out HudPanelViewModel? existing);
        HudPanelViewModel panel;

        if (rebuild)
        {
            panel = existing!;
            // Drop the old panel's watches first: BuildWidget below seeds each bound widget with
            // the current value, and leaving the previous subscriptions alive would keep writing
            // into view models that are about to be thrown away.
            DisposeSubs(key);
        }
        else
        {
            panel = new HudPanelViewModel(title, pluginId);
            if (LoadPosition?.Invoke(key) is (double px, double py, double pw, double ph))
            {
                panel.X = px; panel.Y = py;
                if (pw > 0) panel.UserWidth = pw;
                if (ph > 0) panel.UserHeight = ph;
            }
            panel.Moved = _ => PanelMoved?.Invoke();
        }

        // Brushes are built here (immutable, so they cross threads safely) but ASSIGNED on the UI
        // thread below: on a rebuild the panel is already bound, and raising PropertyChanged off
        // the UI thread would violate Avalonia's thread affinity.
        double width = spec.Width > 0 ? spec.Width : 220;
        Avalonia.Media.IBrush? bgBrush = HudColor.Brush(spec.Background);
        Avalonia.Media.IBrush? acBrush = HudColor.Brush(spec.Accent);

        string? panelFg = spec.Foreground;   // default text colour for widgets that don't set their own
        var subs = new List<IDisposable>();
        var widgets = new List<object>();
        var tabs = new List<HudTabViewModel>();
        if (spec.Tabs.Count > 0)
        {
            foreach (PanelTabSpec tab in spec.Tabs)
            {
                var tabVm = new HudTabViewModel(tab.Title);
                foreach (WidgetSpec w in tab.Widgets) tabVm.Widgets.Add(BuildWidget(pluginId, w, subs, panelFg));
                tabs.Add(tabVm);
            }
        }
        else
        {
            foreach (WidgetSpec w in spec.Widgets) widgets.Add(BuildWidget(pluginId, w, subs, panelFg));
        }
        if (subs.Count > 0) _panelSubs[key] = subs;

        _panelsByKey[key] = panel;
        _specs[key] = spec;
        // The companion protocol has no separate "updated" message and needs none: the client
        // stores panels in a map by id, so re-sending the spec replaces it (and keeps the
        // selected tab, which is tracked separately).
        PanelAdded?.Invoke(key, spec);

        Post(() =>
        {
            // Chrome is re-applied on every build so a rebuild can change width or accent.
            panel.Width = width;
            panel.BackgroundBrush = bgBrush;
            panel.AccentBrush = acBrush;

            // Refill in place rather than swapping the panel object: the HUD surface places a
            // panel when it first appears, and replacing the instance would make it jump back to
            // the default position on every rebuild.
            panel.Widgets.Clear();
            foreach (object w in widgets) panel.Widgets.Add(w);
            panel.Tabs.Clear();
            foreach (HudTabViewModel t in tabs) panel.Tabs.Add(t);
            panel.RaiseHasTabsChanged();
            if (!rebuild) Panels.Add(panel);
        });
    }

    /// <summary>Dispose and forget one panel's state watches.</summary>
    private void DisposeSubs(string key)
    {
        if (!_panelSubs.TryGetValue(key, out List<IDisposable>? subs)) return;
        foreach (IDisposable s in subs) s.Dispose();
        _panelSubs.Remove(key);
    }

    /// <summary>Remove a plugin's panels + state-watches (on reload/disable). Subscription
    /// disposal runs on the caller's (loop) thread; the collection edit is marshalled to the UI.</summary>
    public void RemovePanels(string pluginId)
    {
        // Drop the retained specs on the caller's thread (same as the subscriptions below),
        // not inside the marshalled UI edit — otherwise a companion snapshot taken between
        // the two would still advertise panels that are already gone.
        string prefix = pluginId + "|";
        var doomed = new List<string>();
        foreach (string key in _specs.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) doomed.Add(key);
        foreach (string key in doomed)
        {
            DisposeSubs(key);
            _panelsByKey.Remove(key);
            _specs.Remove(key);
            PanelRemoved?.Invoke(key);
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
            case "row":
            {
                // side-by-side container (API 1.8): children are ordinary widgets built by
                // this same method, so anything that stacks can also sit in a row
                var hrow = new RowWidgetViewModel();
                if (w.Children is not null)
                    foreach (WidgetSpec child in w.Children)
                        hrow.Children.Add(BuildWidget(pluginId, child, subs, panelFg));
                return hrow;
            }
            case "buttonrow":
            {
                var row = new ButtonRowWidgetViewModel();
                // Bound form: the labels come from state rather than the spec, so a plugin can
                // offer choices it could not know at load time (voyage resolve options, a town
                // list that arrives with the feed). One callback serves the whole row and gets
                // the clicked label plus its 1-based index.
                if (!string.IsNullOrEmpty(w.Bind) && !string.IsNullOrEmpty(w.Action))
                {
                    string actionId = w.Action;
                    BindText(w.Bind, s => RebuildChoices(row, s, pluginId, actionId), subs);
                    return row;
                }
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
                // click= runs in the text go through the same path as typing the command, so a
                // plugin's own aliases get first refusal — no per-widget callback needed.
                if (_runCommand is not null)
                    vm.LinkCommand = new RelayCommand<Scrye.Core.Text.LinkInfo>(
                        link => _runCommand(link.Action, link.Prompt));
                BindText(w.Bind, s => vm.Text = s, subs);
                return vm;
            }
            case "barlist":
            {
                var vm = new BarListWidgetViewModel();
                BindText(w.Bind, s => vm.Rows = s, subs);
                return vm;
            }
            // list and table share one view-model and one control: a list IS a two-column,
            // headerless table whose trailing column is dimmed and right-aligned. Keeping them
            // one implementation means they can never drift apart visually.
            case "list":
            {
                var vm = new TableWidgetViewModel(textColor, w.Separator, columns: null,
                                                  align: w.Align ?? "lr", dimTrailing: true);
                BindText(w.Bind, s => vm.Rows = s, subs);
                return vm;
            }
            case "table":
            {
                var vm = new TableWidgetViewModel(textColor, w.Separator,
                                                  w.Columns is null ? null : System.Linq.Enumerable.ToArray(w.Columns),
                                                  w.Align, dimTrailing: false);
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
                var vm = new ColorGridWidgetViewModel(w.Palette, w.Labels, w.Weave, w.Icons, w.Cell);
                if (!string.IsNullOrEmpty(w.Action))
                {
                    string actionId = w.Action;
                    vm.CellCommand = new RelayCommand<Controls.GridCell>(cell =>
                        _invokeCellAction?.Invoke(pluginId, actionId, cell.Col, cell.Row, cell.Ch.ToString()));
                }
                if (!string.IsNullOrEmpty(w.HoverAction))
                {
                    // onHover (1.6): same (col,row,char) path as onClick; the control fires it
                    // only when the hovered CELL changes, and once with (-1,-1,"") on exit.
                    string hoverId = w.HoverAction;
                    vm.HoverCommand = new RelayCommand<Controls.GridCell>(cell =>
                        _invokeCellAction?.Invoke(pluginId, hoverId, cell.Col, cell.Row,
                            cell.Ch == '\0' ? "" : cell.Ch.ToString()));
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

    /// <summary>Repopulate a bound buttonrow from a newline-separated list of labels. Blank
    /// lines are skipped, and an empty value clears the row — which is how a plugin says
    /// "nothing to choose right now" without leaving stale buttons on screen.</summary>
    private void RebuildChoices(ButtonRowWidgetViewModel row, string labels, string pluginId, string actionId)
    {
        row.Buttons.Clear();
        if (string.IsNullOrWhiteSpace(labels)) return;
        int index = 0;
        foreach (string raw in labels.Split('\n'))
        {
            string label = raw.Trim();
            if (label.Length == 0) continue;
            index++;
            int captured = index;                       // capture per iteration, not by reference
            row.Buttons.Add(new ButtonWidgetViewModel(label,
                () => _invokeChoice?.Invoke(pluginId, actionId, label, captured)));
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
        foreach (List<IDisposable> subs in _panelSubs.Values)
            foreach (IDisposable s in subs) s.Dispose();
        _panelSubs.Clear();
        _panelsByKey.Clear();
        Panels.Clear();
    }
}

/// <summary>One HUD panel: a title and a heterogeneous list of widget view-models
/// (rendered by type via DataTemplates) — or a set of <see cref="Tabs"/> when the
/// spec is tabbed. <see cref="PluginId"/> lets the host drop a plugin's panels on
/// reload/disable.</summary>
public sealed class HudPanelViewModel : ViewModelBase
{
    public string Title { get; }
    public string PluginId { get; }
    public ObservableCollection<object> Widgets { get; } = new();
    public ObservableCollection<HudTabViewModel> Tabs { get; } = new();
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>Tabs is observable, but HasTabs is derived from its Count and so has to be
    /// raised by hand after a rebuild swaps a tabbed panel for a flat one or back.</summary>
    internal void RaiseHasTabsChanged() => OnPropertyChanged(nameof(HasTabs));

    private double _width = 220;
    public double Width
    {
        get => _width;
        set { if (SetField(ref _width, value)) OnPropertyChanged(nameof(EffectiveWidth)); }
    }

    /// <summary>User-resized width, NaN = never resized. Overrides the spec's
    /// <see cref="Width"/> in <see cref="EffectiveWidth"/> and survives rebuilds
    /// (a rebuild re-applies the spec width, which must not undo a user resize).</summary>
    private double _userWidth = double.NaN;
    public double UserWidth
    {
        get => _userWidth;
        set { if (SetField(ref _userWidth, value)) OnPropertyChanged(nameof(EffectiveWidth)); }
    }

    /// <summary>What the panel Border binds: the user's width when they resized, else the spec's.</summary>
    public double EffectiveWidth => double.IsNaN(_userWidth) ? _width : _userWidth;

    /// <summary>User-resized height, NaN = auto-size to content (bound straight to
    /// Border.Height, where NaN means "unset"). When fixed and the content is taller,
    /// the panel's ScrollViewer takes over.</summary>
    private double _userHeight = double.NaN;
    public double UserHeight
    {
        get => _userHeight;
        set { if (SetField(ref _userHeight, value)) OnPropertyChanged(nameof(TabMaxHeight)); }
    }

    /// <summary>Cap for a tabbed panel's per-tab ScrollViewer: 460 while auto-sizing (the
    /// long tabs would otherwise grow the panel unbounded), uncapped once the user fixed
    /// the height — the panel itself is the constraint then.</summary>
    public double TabMaxHeight => double.IsNaN(_userHeight) ? 460 : double.PositiveInfinity;

    /// <summary>Plugin-chosen panel background / accent (border + title). Null = follow the theme.
    /// Set from PanelSpec.Background / .Accent; the XAML overrides the themed defaults when present.
    /// Settable and notifying because AddPanel re-applies them when a panel is rebuilt.</summary>
    private Avalonia.Media.IBrush? _backgroundBrush;
    public Avalonia.Media.IBrush? BackgroundBrush
    {
        get => _backgroundBrush;
        set { if (SetField(ref _backgroundBrush, value)) OnPropertyChanged(nameof(HasBackground)); }
    }
    public bool HasBackground => BackgroundBrush is not null;

    private Avalonia.Media.IBrush? _accentBrush;
    public Avalonia.Media.IBrush? AccentBrush
    {
        get => _accentBrush;
        set { if (SetField(ref _accentBrush, value)) OnPropertyChanged(nameof(HasAccent)); }
    }
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

/// <summary>Widgets laid out side by side (the "row" container, API 1.8). Children are
/// ordinary widget view-models rendered by the same DataTemplates as stacked widgets;
/// each takes its measured width — a chart on the left, its notes on the right.</summary>
public sealed class RowWidgetViewModel : ViewModelBase
{
    public System.Collections.ObjectModel.ObservableCollection<object> Children { get; } = new();
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

/// <summary>
/// Resolves a plugin colour string — either a "#RRGGBB" literal or a semantic
/// <see cref="ThemeToken"/> name — into a brush. Null for empty/unrecognised, so the widget
/// falls back to the theme default rather than rendering in some arbitrary colour.
///
/// <para>Tokens are looked up in <see cref="Services.ThemeService.Current"/> at the moment the
/// panel is built. That is a deliberate snapshot, not a binding: see the remarks on
/// <see cref="Services.ThemeService.Current"/> for why plugin brushes must be immutable.</para>
/// </summary>
internal static class HudColor
{
    // IMMUTABLE brush: HUD widgets are built on the session-loop thread (scrye.addPanel
    // runs there), but rendered on the UI thread. A mutable SolidColorBrush has thread
    // affinity and Avalonia 12 throws a cross-thread error when the compositor touches it;
    // immutable brushes are frozen and safe to use from any thread.
    public static Avalonia.Media.IBrush? Brush(string? colour)
    {
        Avalonia.Media.Color? c = Resolve(colour);
        return c is null ? null : new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c.Value);
    }

    /// <summary>The resolved colour, or null when the string names nothing we know.</summary>
    public static Avalonia.Media.Color? Resolve(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour)) return null;
        string v = colour.Trim();

        if (v[0] == '#')
            return v.Length == 7 &&
                   uint.TryParse(v.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint hex)
                ? Avalonia.Media.Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex)
                : null;

        Services.ThemeScheme s = Services.ThemeService.Current;
        return v.ToLowerInvariant() switch
        {
            ThemeToken.Accent => s.Accent,
            ThemeToken.Text => s.Text,
            ThemeToken.Dim => s.TextDim,
            ThemeToken.Bg => s.Bg,
            ThemeToken.Panel => s.Panel,
            ThemeToken.PanelAlt => s.PanelAlt,
            ThemeToken.Inset => s.InsetBg,
            ThemeToken.Line => s.Line,
            ThemeToken.Success => s.Success,
            ThemeToken.Warning => s.Warning,
            ThemeToken.Error => s.Error,
            ThemeToken.Info => s.Info,
            _ => null,
        };
    }

    /// <summary>
    /// The same lookup as <see cref="Resolve"/>, in the engine's <see cref="Scrye.Core.Text.Rgb"/>
    /// form. This is what <c>Scrye.Core.Text.Markup</c> is handed so a plugin's inline colour
    /// markup resolves theme tokens through exactly the table the HUD widgets use — one place
    /// where "accent" is defined, whether it appears in a widget spec or mid-sentence.
    /// </summary>
    public static Scrye.Core.Text.Rgb? ResolveRgb(string? colour)
    {
        Avalonia.Media.Color? c = Resolve(colour);
        return c is null ? null : new Scrye.Core.Text.Rgb(c.Value.R, c.Value.G, c.Value.B);
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

    private static Avalonia.Media.Color? ParseColor(string? colour) => HudColor.Resolve(colour);

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

    /// <summary>Set when the host can run a command — bound to StyledTextView.LinkCommand so
    /// <c>click=</c> runs in the text become clickable. Null leaves the text inert.</summary>
    public System.Windows.Input.ICommand? LinkCommand { get; set; }

    public TextWidgetViewModel(string? colour)
    {
        // Falls back to the scheme's body text rather than a hard-coded light grey, so a plugin
        // that sets no colour reads correctly in the Light scheme too.
        Avalonia.Media.Color c = HudColor.Resolve(colour) ?? Services.ThemeService.Current.Text;
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

/// <summary>
/// The <c>list</c> and <c>table</c> widgets: <see cref="Rows"/> is newline-separated, each line
/// split into cells by <see cref="Separator"/>. Rendered by <c>Controls.DataTableView</c>.
///
/// <para>Unlike every other widget, the number of rows shown here follows the bound state value
/// rather than the panel spec — which is the point. The widget <i>set</i> is still fixed at
/// build time; what varies is the content of this one widget.</para>
/// </summary>
public sealed class TableWidgetViewModel : ViewModelBase
{
    public Avalonia.Media.IBrush? ColorBrush { get; }
    public string Separator { get; }
    public string[]? Columns { get; }
    public string? Align { get; }
    public bool DimTrailing { get; }

    public TableWidgetViewModel(string? colour, string? separator, string[]? columns,
                                string? align, bool dimTrailing)
    {
        ColorBrush = HudColor.Brush(colour);
        Separator = string.IsNullOrEmpty(separator) ? "\t" : separator;
        Columns = columns is { Length: > 0 } ? columns : null;
        Align = align;
        DimTrailing = dimTrailing;
    }

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

    /// <summary>Set when the colorgrid has an onHover callback (API 1.6) — bound to
    /// ColorGridView.HoverCommand. Fired per cell-change, and with (-1,-1,'\0') on exit.</summary>
    public System.Windows.Input.ICommand? HoverCommand { get; set; }

    /// <summary>Characters drawn as a letter on top of their tile; see WidgetSpec.Labels.</summary>
    public string LabelChars { get; }

    /// <summary>Weave mode (API 1.7) — even cells tiles, odd cells thin connector lines;
    /// see WidgetSpec.Weave. Bound to ColorGridView.Weave.</summary>
    public bool Weave { get; }

    /// <summary>Micro-icon map (API 1.8) — character to glyph name; see WidgetSpec.Icons.
    /// Bound to ColorGridView.Icons; null when the widget declared none.</summary>
    public Dictionary<char, string>? Icons { get; }

    /// <summary>Cell-size ceiling (API 1.8) — the compact default 12 unless the spec raised
    /// it; see WidgetSpec.Cell. Bound to ColorGridView.MaxCell.</summary>
    public double MaxCell { get; }

    public ColorGridWidgetViewModel(IReadOnlyDictionary<string, string>? palette, string? labels = null,
        bool weave = false, IReadOnlyDictionary<string, string>? icons = null, double cell = 0)
    {
        MaxCell = cell > 0 ? System.Math.Clamp(cell, 3, 64) : 12;
        Palette = new Dictionary<char, Avalonia.Media.Color>();
        if (palette is not null)
            foreach ((string key, string val) in palette)
                if (key.Length >= 1 && HudColor.Resolve(val) is { } c)
                    Palette[key[0]] = c;
        LabelChars = labels ?? "";
        Weave = weave;
        if (icons is not null && icons.Count > 0)
        {
            Icons = new Dictionary<char, string>();
            foreach ((string key, string val) in icons)
                if (key.Length >= 1 && !string.IsNullOrWhiteSpace(val))
                    Icons[key[0]] = val.Trim().ToLowerInvariant();
        }
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
