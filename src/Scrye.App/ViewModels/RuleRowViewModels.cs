using System;
using Scrye.Core.Automation;

namespace Scrye.App.ViewModels;

/// <summary>Shared editable fields for a trigger or alias row in the rule editor.
/// The immutable <see cref="TriggerDef"/>/<see cref="AliasDef"/> records are
/// projected into these mutable properties for two-way binding, then folded back
/// via the subclass's <c>ToDef</c>.</summary>
public abstract class RuleRowViewModel : ViewModelBase
{
    /// <summary>Enum choices for the send-to combo (bind via x:Static).</summary>
    public static SendTo[] SendToValues { get; } = (SendTo[])Enum.GetValues(typeof(SendTo));

    protected RuleRowViewModel() => TestCommand = new RelayCommand(RunTest);

    private string _name = "";
    public string Name { get => _name; set => SetField(ref _name, value); }

    private string _pattern = "";
    public string Pattern { get => _pattern; set => SetField(ref _pattern, value); }

    private bool _isRegex;
    public bool IsRegex { get => _isRegex; set => SetField(ref _isRegex, value); }

    private bool _ignoreCase = true;
    public bool IgnoreCase { get => _ignoreCase; set => SetField(ref _ignoreCase, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    private bool _keepEvaluating;
    public bool KeepEvaluating { get => _keepEvaluating; set => SetField(ref _keepEvaluating, value); }

    private bool _oneShot;
    public bool OneShot { get => _oneShot; set => SetField(ref _oneShot, value); }

    private SendTo _sendTo = SendTo.World;
    public SendTo SendTo { get => _sendTo; set => SetField(ref _sendTo, value); }

    private string _send = "";
    public string Send { get => _send; set => SetField(ref _send, value); }

    private string _variable = "";
    public string Variable { get => _variable; set => SetField(ref _variable, value); }

    private string _script = "";
    public string Script { get => _script; set => SetField(ref _script, value); }

    private string _group = "";
    public string Group { get => _group; set => SetField(ref _group, value); }

    private string _sequenceText = "100";
    public string SequenceText { get => _sequenceText; set => SetField(ref _sequenceText, value); }

    protected int SequenceValue => int.TryParse(SequenceText, out int v) ? v : 100;
    protected string? OrNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---- "test this pattern" box ----
    private string _testInput = "";
    public string TestInput { get => _testInput; set => SetField(ref _testInput, value); }

    private string _testResult = "";
    public string TestResult { get => _testResult; private set => SetField(ref _testResult, value); }

    public RelayCommand TestCommand { get; }

    /// <summary>Match the sample line against this rule's (unsaved) pattern and show the
    /// result — no engine registration, no side effects. Reuses the real matcher/template.</summary>
    private void RunTest()
    {
        if (string.IsNullOrEmpty(Pattern)) { TestResult = "(enter a pattern first)"; return; }
        try
        {
            MatchResult? m = new CompiledPattern(Pattern, IsRegex, IgnoreCase).Match(TestInput ?? "");
            if (m is null) { TestResult = "✗ no match"; return; }
            string expanded = Template.Expand(Send, m, new VariableStore());
            string wilds = m.Wildcards.Count > 0 ? "   wildcards: [" + string.Join(", ", m.Wildcards) + "]" : "";
            TestResult = SendTo switch
            {
                SendTo.Variable => $"✓ match → set {Variable}={expanded}{wilds}",
                SendTo.Script => $"✓ match → script {Script}{wilds}",
                SendTo.Output or SendTo.Command => $"✓ match → echo: {expanded}{wilds}",
                _ => $"✓ match → send: {expanded}{wilds}",
            };
        }
        catch (Exception ex)
        {
            TestResult = "pattern error: " + ex.Message;
        }
    }
}

/// <summary>A trigger row (matches incoming MUD lines).</summary>
public sealed class TriggerRowViewModel : RuleRowViewModel
{
    public TriggerRowViewModel() { }

    public TriggerRowViewModel(TriggerDef d)
    {
        Name = d.Name; Pattern = d.Pattern; IsRegex = d.IsRegex; IgnoreCase = d.IgnoreCase;
        Enabled = d.Enabled; KeepEvaluating = d.KeepEvaluating; OneShot = d.OneShot;
        SendTo = d.SendTo; Send = d.Send ?? ""; Variable = d.Variable ?? ""; Script = d.Script ?? "";
        Group = d.Group ?? ""; SequenceText = d.Sequence.ToString();
    }

    public TriggerDef ToDef() => new()
    {
        Name = Name.Trim(), Pattern = Pattern, IsRegex = IsRegex, IgnoreCase = IgnoreCase,
        Enabled = Enabled, KeepEvaluating = KeepEvaluating, OneShot = OneShot,
        Sequence = SequenceValue, Group = OrNull(Group),
        SendTo = SendTo, Send = OrNull(Send), Variable = OrNull(Variable), Script = OrNull(Script),
    };
}

/// <summary>An alias row (matches typed commands before send).</summary>
public sealed class AliasRowViewModel : RuleRowViewModel
{
    public AliasRowViewModel() { }

    public AliasRowViewModel(AliasDef d)
    {
        Name = d.Name; Pattern = d.Pattern; IsRegex = d.IsRegex; IgnoreCase = d.IgnoreCase;
        Enabled = d.Enabled; KeepEvaluating = d.KeepEvaluating; OneShot = d.OneShot;
        SendTo = d.SendTo; Send = d.Send ?? ""; Variable = d.Variable ?? ""; Script = d.Script ?? "";
        Group = d.Group ?? ""; SequenceText = d.Sequence.ToString();
    }

    public AliasDef ToDef() => new()
    {
        Name = Name.Trim(), Pattern = Pattern, IsRegex = IsRegex, IgnoreCase = IgnoreCase,
        Enabled = Enabled, KeepEvaluating = KeepEvaluating, OneShot = OneShot,
        Sequence = SequenceValue, Group = OrNull(Group),
        SendTo = SendTo, Send = OrNull(Send), Variable = OrNull(Variable), Script = OrNull(Script),
    };
}

/// <summary>A timer row (fires on an interval).</summary>
public sealed class TimerRowViewModel : ViewModelBase
{
    public static SendTo[] SendToValues { get; } = (SendTo[])Enum.GetValues(typeof(SendTo));

    private string _name = "";
    public string Name { get => _name; set => SetField(ref _name, value); }

    private string _intervalText = "1";
    public string IntervalText { get => _intervalText; set => SetField(ref _intervalText, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }

    private bool _oneShot;
    public bool OneShot { get => _oneShot; set => SetField(ref _oneShot, value); }

    private SendTo _sendTo = SendTo.World;
    public SendTo SendTo { get => _sendTo; set => SetField(ref _sendTo, value); }

    private string _send = "";
    public string Send { get => _send; set => SetField(ref _send, value); }

    private string _variable = "";
    public string Variable { get => _variable; set => SetField(ref _variable, value); }

    private string _script = "";
    public string Script { get => _script; set => SetField(ref _script, value); }

    private string _group = "";
    public string Group { get => _group; set => SetField(ref _group, value); }

    public TimerRowViewModel() { }

    public TimerRowViewModel(TimerDef d)
    {
        Name = d.Name; IntervalText = d.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Enabled = d.Enabled; OneShot = d.OneShot;
        SendTo = d.SendTo; Send = d.Send ?? ""; Variable = d.Variable ?? ""; Script = d.Script ?? "";
        Group = d.Group ?? "";
    }

    public TimerDef ToDef()
    {
        double interval = double.TryParse(IntervalText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1;
        return new TimerDef
        {
            Name = Name.Trim(), IntervalSeconds = interval, Enabled = Enabled, OneShot = OneShot,
            SendTo = SendTo, Send = string.IsNullOrWhiteSpace(Send) ? null : Send,
            Variable = string.IsNullOrWhiteSpace(Variable) ? null : Variable,
            Script = string.IsNullOrWhiteSpace(Script) ? null : Script,
            Group = string.IsNullOrWhiteSpace(Group) ? null : Group,
        };
    }
}
