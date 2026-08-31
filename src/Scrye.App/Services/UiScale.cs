using System;
using System.Collections.Generic;

namespace Scrye.App.Services;

/// <summary>
/// Whole-window zoom: one factor applied to the main window's content, so every control,
/// glyph and line of output grows or shrinks together — what Cmd/Ctrl +, - and 0 do in a
/// browser.
///
/// <para>A static with a <see cref="Changed"/> event, the same shape as
/// <see cref="ThemeService"/>: the window applies it, the view-model persists it, and neither
/// has to know about the other. Scaling the window's content is deliberately not the same as
/// the output font size in Global Settings — that one sizes the MUD's text against the pane it
/// lives in, this one sizes the whole client against your screen, and they compose.</para>
/// </summary>
public static class UiScale
{
    /// <summary>The zoom ladder. Fixed steps rather than a multiply-by-1.1, so that stepping
    /// down and back up lands on exactly the value you started from, and so 100% is always
    /// reachable by stepping instead of only by reset.</summary>
    public static readonly IReadOnlyList<double> Steps =
        new[] { 0.70, 0.80, 0.90, 1.00, 1.10, 1.25, 1.40, 1.60, 1.80, 2.00 };

    /// <summary>Unzoomed. Also what a profile with no saved scale restores to.</summary>
    public const double Default = 1.00;

    private static double _current = Default;

    /// <summary>The factor in force. Always one of <see cref="Steps"/>.</summary>
    public static double Current => _current;

    /// <summary>Raised after <see cref="Current"/> changes — never for a no-op step at either
    /// end of the ladder, so a held-down key at 200% doesn't churn the profile file.</summary>
    public static event Action? Changed;

    /// <summary>Restore a saved factor (null = <see cref="Default"/>), snapped to the nearest
    /// rung so that stepping from a restored value stays on the ladder and a hand-edited
    /// profile can't zoom the client past what it can be zoomed back from.</summary>
    public static void Apply(double? scale) => Set(Nearest(scale ?? Default));

    public static void Increase() => Set(Step(+1));
    public static void Decrease() => Set(Step(-1));
    public static void Reset() => Set(Default);

    private static void Set(double scale)
    {
        if (Math.Abs(scale - _current) < 0.0001) return;
        _current = scale;
        Changed?.Invoke();
    }

    /// <summary>The rung <paramref name="direction"/> away from the current one, clamped at
    /// both ends (so the extremes stop rather than wrap).</summary>
    private static double Step(int direction)
    {
        int i = IndexOf(Nearest(_current)) + direction;
        return Steps[Math.Clamp(i, 0, Steps.Count - 1)];
    }

    private static double Nearest(double scale)
    {
        double best = Steps[0];
        foreach (double step in Steps)
            if (Math.Abs(step - scale) < Math.Abs(best - scale)) best = step;
        return best;
    }

    private static int IndexOf(double value)
    {
        for (int i = 0; i < Steps.Count; i++)
            if (Math.Abs(Steps[i] - value) < 0.0001) return i;
        return 0;
    }
}
