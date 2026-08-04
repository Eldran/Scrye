using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Scrye.Core.Util;

namespace Scrye.App.Controls;

/// <summary>
/// Draws <see cref="Text"/> as a QR code. Used by the companion panel so a phone can reach
/// the server by pointing a camera at the desktop instead of typing a <c>.ts.net</c> URL.
///
/// <para>Two things matter for a code that actually scans, and both are about pixels rather
/// than encoding. Modules are snapped to <b>whole pixels</b>: a fractional module size makes
/// the renderer blend adjacent modules, and a camera reading a blurred edge guesses. And the
/// code is drawn on its own <b>white</b> field regardless of theme — QR contrast is defined
/// as dark-on-light, and inverting it defeats a good number of phone cameras that never try
/// the other polarity.</para>
/// </summary>
public class QrCodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    /// <summary>The payload. Null or empty draws nothing at all rather than a code that
    /// scans to the empty string — an inert grey box reads as "not ready yet", which is
    /// exactly what it means before the server has started.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(TextProperty);
        AffectsMeasure<QrCodeView>(TextProperty);
    }

    private bool[,]? _matrix;
    private string? _encoded;

    private bool[,]? Matrix()
    {
        string? text = Text;
        if (string.IsNullOrEmpty(text)) return null;
        if (_matrix is not null && _encoded == text) return _matrix;

        try
        {
            // Medium correction: plenty for a screen, and it keeps the version — and so the
            // module count — small enough that each module still lands on several pixels.
            _matrix = QrCode.Encode(text, QrCode.Ecc.Medium, border: 2);
            _encoded = text;
        }
        catch (ArgumentException)
        {
            // Only reachable if a URL ever exceeded version 20. Drawing nothing beats
            // throwing out of a render pass and taking the window down with it.
            _matrix = null;
            _encoded = text;
        }

        return _matrix;
    }

    public override void Render(DrawingContext context)
    {
        bool[,]? m = Matrix();
        if (m is null) return;

        int modules = m.GetLength(0);
        double available = Math.Min(Bounds.Width, Bounds.Height);
        int scale = (int)(available / modules);
        if (scale < 1) return;                       // too small to draw honestly

        double side = scale * modules;
        double ox = Math.Floor((Bounds.Width - side) / 2);
        double oy = Math.Floor((Bounds.Height - side) / 2);

        // The quiet zone is part of the matrix, so filling the whole square white gives the
        // border the spec requires without drawing it separately.
        context.FillRectangle(Brushes.White, new Rect(ox, oy, side, side));

        for (int y = 0; y < modules; y++)
        {
            // Coalesce horizontal runs into one rectangle: same picture, a fraction of the
            // draw calls, and no hairline seams between adjacent modules.
            int x = 0;
            while (x < modules)
            {
                if (!m[y, x]) { x++; continue; }

                int start = x;
                while (x < modules && m[y, x]) x++;

                context.FillRectangle(Brushes.Black,
                    new Rect(ox + start * scale, oy + y * scale, (x - start) * scale, scale));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        bool[,]? m = Matrix();
        if (m is null) return default;

        // Ask for a square that is a whole number of modules, so layout does not hand back a
        // size that forces fractional scaling.
        int modules = m.GetLength(0);
        double limit = Math.Min(
            double.IsInfinity(availableSize.Width) ? modules * 4 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? modules * 4 : availableSize.Height);

        int scale = Math.Max(1, (int)(limit / modules));
        double side = scale * modules;
        return new Size(side, side);
    }
}
