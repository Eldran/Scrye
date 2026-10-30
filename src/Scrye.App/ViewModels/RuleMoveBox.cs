using System.Collections.Generic;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

/// <summary>
/// The "Move to…" picker under a rule list, shared by the profile editor and the global
/// settings (Joakim, 28 Oct 2026: a rule made in Settings landed on every character). Pick a
/// layer, press Move, and the selected rule leaves this list and is queued for that layer.
/// Nothing is written until the form is SAVED - Cancel leaves both layers as they were - and
/// then the rules are added to their new homes BEFORE this layer is saved without them, so a
/// failure part-way costs a duplicate, never the rule (see ProfileStore.MoveRules).
/// </summary>
public sealed class RuleMoveBox : ViewModelBase
{
    private IReadOnlyList<RuleHome> _targets = System.Array.Empty<RuleHome>();
    /// <summary>Every layer but the one being edited, Global first (ProfileStore.RuleHomes).</summary>
    public IReadOnlyList<RuleHome> Targets { get => _targets; private set => SetField(ref _targets, value); }

    private RuleHome? _target;
    public RuleHome? Target
    {
        get => _target;
        set { if (SetField(ref _target, value) && _pending.Count == 0) Status = ""; }   // "choose where…" is answered
    }

    private string _status = "";
    /// <summary>What is queued, or what to do first. Empty when nothing is.</summary>
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _charactersHint = "";
    /// <summary>"Characters here: Eldran, Nille" - the names an Only for on this layer can use.</summary>
    public string CharactersHint { get => _charactersHint; private set => SetField(ref _charactersHint, value); }

    private readonly List<(RuleHome Home, object Rule)> _pending = new();
    /// <summary>The rules moved out of this form, waiting for its Save.</summary>
    public IReadOnlyList<(RuleHome Home, object Rule)> Pending => _pending;

    /// <summary>Offer every home but <paramref name="own"/> (null = a layer not saved yet, which
    /// is not among the homes anyway), and name the characters it reaches.</summary>
    public void SetTargets(IEnumerable<RuleHome> homes, RuleHome? own, IReadOnlyList<string> characters)
    {
        var list = new List<RuleHome>();
        foreach (RuleHome h in homes) if (h != own) list.Add(h);
        Targets = list;
        // a pick that is no longer a layer (renamed, deleted) must not stay picked
        if (Target is RuleHome t && !list.Contains(t)) Target = null;
        CharactersHint = characters.Count == 0
            ? "No characters under this yet."
            : "Characters here: " + string.Join(", ", characters);
    }

    /// <summary>Queue <paramref name="rule"/> for the picked layer. False (and says why) when no
    /// layer is picked or the rule has no name - the caller then leaves the row where it is.</summary>
    public bool Queue(object rule, string what, string name)
    {
        if (Target is not RuleHome home)
        {
            Status = "Choose where to move it, then press Move.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "Give it a name first.";
            return false;
        }
        _pending.Add((home, rule));
        Status = _pending.Count == 1
            ? $"{what} '{name.Trim()}' goes to {home.Label} when you save."
            : $"{_pending.Count} rules go to their new places when you save (last: {what} '{name.Trim()}' to {home.Label}).";
        return true;
    }

    /// <summary>The queued moves to one layer have landed: drop them, so a retry after a later
    /// layer failed does not move them a second time.</summary>
    public void Drop(RuleHome home)
    {
        _pending.RemoveAll(p => p.Home == home);
        if (_pending.Count == 0) Status = "";
    }

    /// <summary>After a save: nothing pending any more.</summary>
    public void Clear()
    {
        _pending.Clear();
        Status = "";
    }
}
