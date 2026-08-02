namespace Scrye.Core.Text;

/// <summary>An immutable 24-bit RGB colour. The engine works in truecolour; the
/// ANSI parser maps 16- and 256-colour codes into this space.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb DefaultFore = new(0xC0, 0xC0, 0xC0);
    public static readonly Rgb DefaultBack = new(0x00, 0x00, 0x00);

    /// <summary>"#RRGGBB" form (used by highlights, colorgrid palettes, config JSON).</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Parse "#RRGGBB" or "RRGGBB" (case-insensitive). Returns false on null/blank/malformed.</summary>
    public static bool TryParseHex(string? s, out Rgb rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        ReadOnlySpan<char> h = s.AsSpan().Trim();
        if (h.Length > 0 && h[0] == '#') h = h[1..];
        if (h.Length != 6) return false;
        if (byte.TryParse(h[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(h[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(h[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            rgb = new Rgb(r, g, b);
            return true;
        }
        return false;
    }

    /// <summary>The active 16-colour ANSI palette. <see cref="AnsiPaletteMode.Modern"/> is the
    /// xterm/VGA palette (default); <see cref="AnsiPaletteMode.Classic"/> matches MUSHclient's
    /// default (pure-primary brights, olive normal-yellow). Set from the global appearance setting;
    /// affects lines parsed after the change.</summary>
    public enum AnsiPaletteMode { Modern, Classic }
    public static AnsiPaletteMode AnsiPalette = AnsiPaletteMode.Modern;

    /// <summary>The 16 standard ANSI colours (0-7 normal, bright = high-intensity),
    /// resolved through the currently-selected <see cref="AnsiPalette"/>.</summary>
    public static Rgb Ansi16(int index, bool bright) =>
        AnsiPalette == AnsiPaletteMode.Classic ? Ansi16Classic(index, bright) : Ansi16Modern(index, bright);

    // modern xterm/VGA palette (normal at 0xAA, bright at 0x55-lifted)
    private static Rgb Ansi16Modern(int index, bool bright)
    {
        (byte r, byte g, byte b) = (index & 7) switch
        {
            0 => bright ? ((byte)0x55, (byte)0x55, (byte)0x55) : ((byte)0x00, (byte)0x00, (byte)0x00), // black / grey
            1 => bright ? ((byte)0xFF, (byte)0x55, (byte)0x55) : ((byte)0xAA, (byte)0x00, (byte)0x00), // red
            2 => bright ? ((byte)0x55, (byte)0xFF, (byte)0x55) : ((byte)0x00, (byte)0xAA, (byte)0x00), // green
            3 => bright ? ((byte)0xFF, (byte)0xFF, (byte)0x55) : ((byte)0xAA, (byte)0x55, (byte)0x00), // yellow/brown
            4 => bright ? ((byte)0x55, (byte)0x55, (byte)0xFF) : ((byte)0x00, (byte)0x00, (byte)0xAA), // blue
            5 => bright ? ((byte)0xFF, (byte)0x55, (byte)0xFF) : ((byte)0xAA, (byte)0x00, (byte)0xAA), // magenta
            6 => bright ? ((byte)0x55, (byte)0xFF, (byte)0xFF) : ((byte)0x00, (byte)0xAA, (byte)0xAA), // cyan
            _ => bright ? ((byte)0xFF, (byte)0xFF, (byte)0xFF) : ((byte)0xAA, (byte)0xAA, (byte)0xAA), // white
        };
        return new Rgb(r, g, b);
    }

    // MUSHclient default palette (normal at 0x80, bright = pure primaries)
    private static Rgb Ansi16Classic(int index, bool bright)
    {
        (byte r, byte g, byte b) = (index & 7) switch
        {
            0 => bright ? ((byte)0x80, (byte)0x80, (byte)0x80) : ((byte)0x00, (byte)0x00, (byte)0x00), // black / grey
            1 => bright ? ((byte)0xFF, (byte)0x00, (byte)0x00) : ((byte)0x80, (byte)0x00, (byte)0x00), // red
            2 => bright ? ((byte)0x00, (byte)0xFF, (byte)0x00) : ((byte)0x00, (byte)0x80, (byte)0x00), // green
            3 => bright ? ((byte)0xFF, (byte)0xFF, (byte)0x00) : ((byte)0x80, (byte)0x80, (byte)0x00), // yellow / olive
            4 => bright ? ((byte)0x00, (byte)0x00, (byte)0xFF) : ((byte)0x00, (byte)0x00, (byte)0x80), // blue
            5 => bright ? ((byte)0xFF, (byte)0x00, (byte)0xFF) : ((byte)0x80, (byte)0x00, (byte)0x80), // magenta
            6 => bright ? ((byte)0x00, (byte)0xFF, (byte)0xFF) : ((byte)0x00, (byte)0x80, (byte)0x80), // cyan
            _ => bright ? ((byte)0xFF, (byte)0xFF, (byte)0xFF) : ((byte)0xC0, (byte)0xC0, (byte)0xC0), // white / silver
        };
        return new Rgb(r, g, b);
    }

    /// <summary>xterm-256 palette: 0-15 system, 16-231 6x6x6 cube, 232-255 greyscale ramp.</summary>
    public static Rgb Xterm256(int n)
    {
        if (n < 16) return Ansi16(n & 7, n >= 8);
        if (n >= 232)
        {
            byte v = (byte)(8 + (n - 232) * 10);
            return new Rgb(v, v, v);
        }
        int c = n - 16;
        int ri = c / 36, gi = (c % 36) / 6, bi = c % 6;
        static byte Level(int i) => (byte)(i == 0 ? 0 : 55 + i * 40);
        return new Rgb(Level(ri), Level(gi), Level(bi));
    }
}
