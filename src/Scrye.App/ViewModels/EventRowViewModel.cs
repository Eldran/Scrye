using Avalonia.Media;
using Scrye.Core.Events;

namespace Scrye.App.ViewModels;

/// <summary>
/// A single row in the debugger's event timeline. Immutable display projection of
/// a <see cref="SessionEvent"/> — rows never change after creation, so no change
/// notification is needed. <see cref="Category"/> drives both the filter buckets
/// and the row colour.
/// </summary>
public sealed class EventRowViewModel
{
    public long Seq { get; }
    public string Time { get; }
    public string Kind { get; }
    public string Label { get; }
    public string Text { get; }
    public string Detail { get; }
    public string Category { get; }
    public IBrush Brush { get; }

    public EventRowViewModel(SessionEvent ev)
    {
        Seq = ev.Seq;
        Time = ev.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff");
        Kind = ev.Kind.ToString();
        Label = ev.Label ?? "";
        Text = ev.Text;
        Detail = ev.Detail ?? "";
        (Category, Brush) = Classify(ev.Kind);
    }

    /// <summary>The one-line rendering shown in the list.</summary>
    public string Display
    {
        get
        {
            string label = Label.Length > 0 ? $"[{Label}] " : "";
            string detail = Detail.Length > 0 ? $"  ({Detail})" : "";
            return $"{Time}  {Kind,-14} {label}{Text}{detail}";
        }
    }

    /// <summary>The full, untruncated detail shown in the drill-down pane when this row
    /// is selected — the whole point being to read long MIP/GMCP/output payloads.</summary>
    public string Full
    {
        get
        {
            string s = $"#{Seq}  {Time}  {Kind}";
            if (Label.Length > 0) s += $"\nlabel:  {Label}";
            if (Text.Length > 0) s += $"\ntext:   {Text}";
            if (Detail.Length > 0) s += $"\ndetail: {Detail}";
            return s;
        }
    }

    private static (string category, IBrush brush) Classify(SessionEventKind kind) => kind switch
    {
        SessionEventKind.LineReceived or SessionEventKind.Prompt
            => ("Output", new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4))),
        SessionEventKind.InputSubmitted or SessionEventKind.Sent
            => ("Input", new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0xF0))),
        SessionEventKind.TriggerMatched or SessionEventKind.AliasMatched or SessionEventKind.TimerFired
            => ("Automation", new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE))),
        SessionEventKind.VariableChanged
            => ("State", new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0))),
        SessionEventKind.Gmcp or SessionEventKind.Mip
            => ("Protocol", new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))),
        SessionEventKind.ScriptError
            => ("System", new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47))),
        _ => ("System", new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x40))),
    };
}
