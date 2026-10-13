using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KorEng;

public static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new LinearGradientBrush(new Rectangle(0, 0, 64, 64),
                Color.FromArgb(98, 148, 255), Color.FromArgb(70, 80, 212), 50f);
            using var shape = ToggleSwitch.RoundedRectangle(new RectangleF(2, 2, 60, 60), 15);
            graphics.FillPath(background, shape);
            using var letter = new Pen(Color.White, 5.5f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            graphics.DrawLines(letter, [new PointF(14, 43), new PointF(25, 17), new PointF(36, 43)]);
            graphics.DrawLine(letter, 19, 33, 31, 33);
            using var caret = new Pen(Color.FromArgb(210, 228, 255), 3.2f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round
            };
            graphics.DrawLine(caret, 47, 19, 47, 43);
            graphics.DrawLine(caret, 42, 19, 52, 19);
            graphics.DrawLine(caret, 42, 43, 52, 43);
            graphics.DrawLine(caret, 43, 51, 51, 51);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
