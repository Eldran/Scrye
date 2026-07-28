namespace Scrye.Core.Text;

/// <summary>An immutable 24-bit RGB colour. The engine works in truecolour; the
/// ANSI parser maps 16- and 256-colour codes into this space.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb DefaultFore = new(0xC0, 0xC0, 0xC0);
    public static readonly Rgb DefaultBack = new(0x00, 0x00, 0x00);

    /// <summary>The 16 standard ANSI colours (0-7 normal, bright = high-intensity).</summary>
    public static Rgb Ansi16(int index, bool bright)
    {
        // classic VGA-ish palette
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
