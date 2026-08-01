using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;

namespace Scrye.App.Services;

/// <summary>Enumerates the system fonts and keeps only the monospaced ones — the
/// families whose glyphs share a fixed advance width, so the MUD's column layouts
/// (market tables, HUD panes, ASCII maps) stay aligned. Proportional fonts (Arial,
/// Segoe UI, …) are filtered out so they can't be picked by accident. The scan runs
/// once on the UI thread and is cached for the life of the process.</summary>
public static class FontScanner
{
    private static IReadOnlyList<string>? _cache;

    /// <summary>Sorted, de-duplicated names of the installed monospaced font families.
    /// Must be called on the UI thread (it measures glyphs via the text stack).</summary>
    public static IReadOnlyList<string> MonospacedFamilies()
    {
        if (_cache is not null) return _cache;

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (FontFamily family in FontManager.Current.SystemFonts)
            {
                string? name = family.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (IsMonospaced(family)) found.Add(name);
            }
        }
        catch
        {
            // if font enumeration is unavailable for any reason, fall through to the
            // known-good defaults below rather than leaving the picker empty
        }

        if (found.Count == 0)
        {
            foreach (string n in new[] { "Cascadia Mono", "Cascadia Code", "Consolas",
                                         "Courier New", "Lucida Console", "Menlo", "monospace" })
                found.Add(n);
        }

        _cache = found.ToList();
        return _cache;
    }

    /// <summary>A font is monospaced when a narrow glyph and a wide glyph advance by the
    /// same width. We compare 'l' against 'W' at a large size and allow a sub-pixel slack.</summary>
    private static bool IsMonospaced(FontFamily family)
    {
        try
        {
            var typeface = new Typeface(family);
            double narrow = Advance("l", typeface);
            double wide = Advance("W", typeface);
            if (narrow <= 0 || wide <= 0) return false;
            return Math.Abs(narrow - wide) < 0.1;
        }
        catch
        {
            return false;
        }
    }

    private static double Advance(string glyph, Typeface typeface) =>
        new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                          typeface, 24, Brushes.White).Width;
}
