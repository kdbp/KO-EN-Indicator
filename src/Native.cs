using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace KorEng;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct GUITHREADINFO
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public RECT CaretRect;
    }
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool LogicalToPhysicalPointForPerMonitorDPI(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("imm32.dll")] internal static extern nint ImmGetDefaultIMEWnd(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback callback, nint param);
    internal delegate bool EnumWindowsCallback(nint hwnd, nint param);

    internal static bool TryGetContext(out nint foreground, out nint focus, out uint thread, out GUITHREADINFO info)
    {
        foreground = GetForegroundWindow(); focus = 0; thread = 0;
        info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (foreground == 0) return false;
        thread = GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == Environment.ProcessId || !GetGUIThreadInfo(thread, ref info)) return false;
        focus = info.Focus;
        return focus != 0 && foreground == GetForegroundWindow();
    }

    internal static Rectangle? SystemCaret(nint foreground, in GUITHREADINFO info)
    {
        if (info.Caret == 0 || !IsWindowVisible(info.Caret) || (info.Flags & 1) == 0 || (info.Flags & 0x1e) != 0) return null;
        if (GetAncestor(info.Caret, 2) != foreground) return null;
        var rc = info.CaretRect;
        if (rc.Bottom <= rc.Top || rc.Right < rc.Left) return null;
        var a = new POINT { X = rc.Left, Y = rc.Top };
        var b = new POINT { X = rc.Right, Y = rc.Bottom };
        // GUITHREADINFO coordinates are in the target window's logical coordinate space.
        // Match that window's DPI awareness while mapping, then explicitly obtain physical pixels.
        nint previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(info.Caret));
        try
        {
            if (!ClientToScreen(info.Caret, ref a) || !ClientToScreen(info.Caret, ref b)) return null;
            LogicalToPhysicalPointForPerMonitorDPI(info.Caret, ref a);
            LogicalToPhysicalPointForPerMonitorDPI(info.Caret, ref b);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        var caret = Rectangle.FromLTRB(a.X, a.Y, Math.Max(a.X + 1, b.X), b.Y);
        if (!CaretRules.Plausible(caret)) return null;
        if (!GetWindowRect(foreground, out var window) || !Rectangle.FromLTRB(window.Left, window.Top, window.Right, window.Bottom).IntersectsWith(caret)) return null;
        return caret;
    }

    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

internal enum InputMode { Unknown, Korean, English, Other }
internal static class CaretRules
{
    internal static bool Plausible(Rectangle r) => r.Width is >= 1 and <= 16 && r.Height is >= 2 and <= 200 && Math.Abs((long)r.X) < 100000 && Math.Abs((long)r.Y) < 100000;
    internal static InputMode Classify(ushort language, bool openOk, nuint open, bool conversionOk, nuint conversion)
    {
        int primary = language & 0x3ff;
        if (primary == 9) return InputMode.English;
        if (primary != 0x12) return InputMode.Other;
        if (!openOk) return InputMode.Unknown;
        if (open == 0) return InputMode.English;
        if (!conversionOk) return InputMode.Unknown;
        return (conversion & 1) != 0 ? InputMode.Korean : InputMode.English;
    }
    internal static InputMode ReadMode(nint focus, uint thread)
    {
        ushort lang = (ushort)(Native.GetKeyboardLayout(thread).ToInt64() & 0xffff);
        if ((lang & 0x3ff) != 0x12) return Classify(lang, false, 0, false, 0);
        nint ime = Native.ImmGetDefaultIMEWnd(focus);
        if (ime == 0) return InputMode.Unknown;
        return ReadImeWindow(lang, ime);
    }
    internal static InputMode ReadImeWindow(ushort lang, nint ime)
    {
        bool opened = Native.SendMessageTimeout(ime, 0x0283, 5, 0, 0x23, 30, out nuint open) != 0;
        nuint conversion = 0;
        bool converted = opened && open != 0 && Native.SendMessageTimeout(ime, 0x0283, 1, 0, 0x23, 30, out conversion) != 0;
        return Classify(lang, opened, open, converted, conversion);
    }
}
