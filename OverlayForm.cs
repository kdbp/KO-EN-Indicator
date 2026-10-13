using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KorEng;

public sealed class OverlayForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private int _renderedDpi;
    private Point _position = new(int.MinValue, int.MinValue);

    public OverlayForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        // WinForms TopMost triggers FocusActiveControlInternal during Show().
        // Keep the managed flag false; SetWindowPos below applies HWND_TOPMOST
        // together with SWP_NOACTIVATE on every display/move instead.
        TabStop = false;
        Text = string.Empty;
        AccessibleRole = AccessibleRole.None;
        Size = new Size(26, 24);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent | WsExLayered;
            return parameters;
        }
    }

    public void ShowIndicator(Rectangle caret)
    {
        if (IsDisposed || caret.Height <= 0 || caret.Width < 0)
        {
            HideIndicator();
            return;
        }

        var visibleCaret = new Rectangle(caret.X, caret.Y, Math.Max(1, caret.Width), caret.Height);
        Screen? screen = null;
        foreach (var candidate in Screen.AllScreens)
        {
            if (!candidate.Bounds.IntersectsWith(visibleCaret)) continue;
            screen = candidate;
            break;
        }
        if (screen is null)
        {
            HideIndicator();
            return;
        }

        var dpi = GetScreenDpi(new Point(caret.Left, caret.Bottom - 1));
        var scale = dpi / 96f;
        var width = Math.Max(20, (int)Math.Round(26 * scale));
        var height = Math.Max(18, (int)Math.Round(24 * scale));
        var gap = Math.Max(2, (int)Math.Round(4 * scale));
        var bounds = screen.Bounds;
        var x = Math.Clamp(caret.Left + caret.Width / 2 - width / 2, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        var y = Math.Clamp(caret.Bottom + gap, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        var destination = new Point(x, y);

        if (_renderedDpi != dpi || !IsHandleCreated)
        {
            Size = new Size(width, height);
            Location = destination;
            if (!RenderSurface(dpi, destination))
            {
                HideIndicator();
                return;
            }
            _renderedDpi = dpi;
        }

        if (!Visible) Show();
        if (_position != destination || !Visible || Location != destination)
        {
            SetWindowPos(Handle, HwndTopmost, x, y, width, height, 0x0010 | 0x0200 | 0x0040);
            _position = destination;
        }
        else
        {
            // Keep a borderless/fullscreen foreground window from covering the marker.
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }
    }

    public void HideIndicator()
    {
        if (!IsDisposed && Visible) Hide();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _renderedDpi = 0;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
        if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }

    private bool RenderSurface(int dpi, Point destination)
    {
        var scale = dpi / 96f;
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            var rectangle = new RectangleF(scale, scale, Width - scale * 2, Height - scale * 2);
            using var path = ToggleSwitch.RoundedRectangle(rectangle, 6 * scale);
            using var fill = new SolidBrush(Color.FromArgb(244, 18, 26, 43));
            using var border = new Pen(Color.FromArgb(225, 104, 144, 223), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            using var font = new Font("Segoe UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(Color.FromArgb(248, 251, 255));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("A", font, foreground, new RectangleF(0, -0.5f * scale, Width, Height), format);
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        var memoryDc = IntPtr.Zero;
        var bitmapHandle = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return false;
            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memoryDc, bitmapHandle);
            var nativePosition = new NativePoint(destination.X, destination.Y);
            var size = new NativeSize(Width, Height);
            var origin = new NativePoint(0, 0);
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            return UpdateLayeredWindow(Handle, screenDc, ref nativePosition, ref size,
                memoryDc, ref origin, 0, ref blend, 2);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, oldBitmap);
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static int GetScreenDpi(Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint(point.X, point.Y), 2);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var x, out _) == 0)
            return (int)Math.Clamp(x, 96u, 768u);
        return 96;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height) { public int Width = width; public int Height = height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref NativePoint destination,
        ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
