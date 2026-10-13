using System;
using System.Windows;

namespace KorEng;

internal static class ExplorerCaretGeometry
{
    // Empty WinUI TextBox controls do not expose glyph/caret bounds. Use the
    // documented WinUI TextControlThemePadding (10,5,6,6) plus its 1-DIP border.
    // This fallback is used ONLY after proving a focused Explorer edit is empty.
    // https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/Common_themeresources.xaml
    internal static Rect Empty(Rect bounds, uint dpi, double fontPoints, int alignment)
    {
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || dpi < 96 || dpi > 768 ||
            !double.IsFinite(fontPoints) || fontPoints is < 5 or > 48 || alignment is < 0 or > 2) return Rect.Empty;
        double scale = dpi / 96d;
        // Reject multiline/content surfaces: this is not a generic control-box fallback.
        if (bounds.Height / scale is < 20 or > 64 || bounds.Width < 24 * scale) return Rect.Empty;
        double height = Math.Min(fontPoints * (96d / 72d) * 1.25 * scale, bounds.Height - 12 * scale);
        if (height < 4) return Rect.Empty;
        double x = alignment == 2 ? bounds.Right - 7 * scale : alignment == 1 ? bounds.Left + bounds.Width / 2 : bounds.Left + 11 * scale;
        return new Rect(x, bounds.Top + (bounds.Height - height) / 2, Math.Max(1, scale), height);
    }
}
