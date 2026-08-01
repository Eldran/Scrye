using System;
using System.Text;
using Avalonia.Input;

namespace Scrye.App.Services;

/// <summary>
/// Canonicalises key gestures for macros — both from a live <see cref="KeyEventArgs"/>
/// and from a stored config string — into one comparable form (e.g. "Ctrl+Shift+K",
/// "F1", "NumPad1"). This lets a macro's saved key match whatever the user pressed,
/// regardless of how they typed the modifiers ("ctrl-k", "Control+K", "K+Ctrl" all
/// normalise the same). Modifier order in the canonical form is Ctrl, Alt, Shift, Win.
/// </summary>
public static class MacroKeys
{
    /// <summary>Should this key event even be considered for a macro? True when a
    /// Ctrl/Alt modifier is held, or the key is a function/numpad/navigation key —
    /// so ordinary typing (bare letters, Shift+letter) is never intercepted.</summary>
    public static bool IsEligible(Key key, KeyModifiers mods) =>
        mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || IsNonTextKey(key);

    private static bool IsNonTextKey(Key key) =>
        (key >= Key.F1 && key <= Key.F24)
        || (key >= Key.NumPad0 && key <= Key.NumPad9)
        || key is Key.Up or Key.Down or Key.Left or Key.Right
                or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    /// <summary>Canonical gesture for a live key press, or null for a bare modifier key.</summary>
    public static string? FromEvent(Key key, KeyModifiers mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return null;
        string? main = MainTokenFromKey(key);
        if (main is null) return null;
        return Build(
            mods.HasFlag(KeyModifiers.Control),
            mods.HasFlag(KeyModifiers.Alt),
            mods.HasFlag(KeyModifiers.Shift),
            mods.HasFlag(KeyModifiers.Meta),
            main);
    }

    /// <summary>Canonicalise a stored/user-typed gesture string for lookup. Returns "" if
    /// no usable main key is present.</summary>
    public static string Normalize(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "";
        bool ctrl = false, alt = false, shift = false, win = false;
        string? main = null;
        foreach (string rawTok in gesture.Split('+', '-'))
        {
            string tok = rawTok.Trim();
            if (tok.Length == 0) continue;
            switch (tok.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "meta" or "cmd" or "super": win = true; break;
                default: main = MainTokenFromString(tok); break;
            }
        }
        return main is null ? "" : Build(ctrl, alt, shift, win, main);
    }

    private static string Build(bool ctrl, bool alt, bool shift, bool win, string main)
    {
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        if (win) sb.Append("Win+");
        sb.Append(main);
        return sb.ToString();
    }

    private static string? MainTokenFromKey(Key key)
    {
        if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "NumPad" + (key - Key.NumPad0);
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        return key switch
        {
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Insert => "Insert", Key.Delete => "Delete", Key.Space => "Space",
            Key.Enter => "Enter", Key.Tab => "Tab", Key.Escape => "Escape", Key.Back => "Backspace",
            _ => null,
        };
    }

    private static string? MainTokenFromString(string tok)
    {
        string t = tok.ToLowerInvariant();

        if (t.Length >= 2 && t[0] == 'f' && int.TryParse(t.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return "F" + fn;
        if (t.StartsWith("numpad") && int.TryParse(t.AsSpan(6), out int np) && np is >= 0 and <= 9)
            return "NumPad" + np;
        if (t.StartsWith("num") && int.TryParse(t.AsSpan(3), out int np2) && np2 is >= 0 and <= 9)
            return "NumPad" + np2;
        if (t.Length == 1 && t[0] >= '0' && t[0] <= '9') return t;
        if (t.Length == 1 && t[0] >= 'a' && t[0] <= 'z') return t.ToUpperInvariant();
        return t switch
        {
            "up" => "Up", "down" => "Down", "left" => "Left", "right" => "Right",
            "home" => "Home", "end" => "End", "pageup" or "pgup" => "PageUp",
            "pagedown" or "pgdn" => "PageDown", "insert" or "ins" => "Insert",
            "delete" or "del" => "Delete", "space" => "Space", "enter" or "return" => "Enter",
            "tab" => "Tab", "escape" or "esc" => "Escape", "backspace" => "Backspace",
            _ => tok,   // pass through unknown tokens verbatim
        };
    }
}
