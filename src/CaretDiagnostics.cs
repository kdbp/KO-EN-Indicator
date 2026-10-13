using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KorEng;

// Explicit, short-lived opt-in diagnostics. Never queries text, titles, paths,
// descendants, clipboard contents, or screenshots. Only Explorer is sampled.
internal static class CaretDiagnostics
{
    internal static int Run(int seconds, bool preview = false)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "caret-diagnostics.txt");
        File.WriteAllText(output, "KorEng geometry-only diagnostic\n");
        using var context = new ApplicationContext();
        bool finished = false;
        using var overlay = new OverlayForm();
        using var timer = new System.Windows.Forms.Timer { Interval = 300 };
        Process? probe = null;
        long end = Environment.TickCount64 + seconds * 1000L;
        int samples = 0, explorerSamples = 0;
        bool busy = false;
        timer.Tick += async (_, _) =>
        {
            if (Environment.TickCount64 >= end)
            {
                File.AppendAllText(output, $"samples={samples};explorer-samples={explorerSamples}\n");
                finished = true; context.ExitThread(); return;
            }
            if (busy) return;
            samples++;
            nint fg = Native.GetForegroundWindow();
            var name = new StringBuilder(64);
            GetClassName(fg, name, name.Capacity);
            if (name.ToString() != "CabinetWClass" && name.ToString() != "ExploreWClass") { overlay.HideIndicator(); return; }
            explorerSamples++;
            busy = true;
            try
            {
                if (probe == null || probe.HasExited)
                {
                    probe?.Dispose();
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
                    start.ArgumentList.Add("--caret-worker");
                    probe = Process.Start(start)!;
                }
                string data = await Task.Run(() => Snapshot(fg));
                await probe.StandardInput.WriteLineAsync("D:" + fg.ToInt64().ToString(CultureInfo.InvariantCulture));
                await probe.StandardInput.FlushAsync();
                string? detail = await probe.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(700));
                if (!finished) File.AppendAllText(output, DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + data + ";" + detail + "\n");
                if (preview && !finished)
                {
                    var caret = ParseFound(detail);
                    if (caret.HasValue && Native.TryGetContext(out nint current, out nint focus, out uint thread, out _) &&
                        current == fg && CaretRules.ReadMode(focus, thread) == InputMode.English) overlay.ShowIndicator(caret.Value);
                    else overlay.HideIndicator();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                overlay.HideIndicator();
                File.AppendAllText(output, "diagnostic-provider-timeout-or-exit\n");
                if (probe is { HasExited: false }) probe.Kill();
                probe?.Dispose(); probe = null;
            }
            finally { busy = false; }
        };
        timer.Start();
        Application.Run(context);
        if (probe is { HasExited: false }) probe.Kill();
        probe?.Dispose();
        return 0;
    }

    private static System.Drawing.Rectangle? ParseFound(string? detail)
    {
        if (detail == null) return null;
        int marker = detail.IndexOf(";found=", StringComparison.Ordinal);
        if (marker < 0) return null;
        string[] parts = detail[(marker + 7)..].Split(',');
        if (parts.Length != 4) return null;
        int[] numbers = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) return null;
            numbers[i] = (int)Math.Round(value);
        }
        var rect = new System.Drawing.Rectangle(numbers[0], numbers[1], Math.Max(1, numbers[2]), numbers[3]);
        return CaretRules.Plausible(rect) ? rect : null;
    }

    private static string Snapshot(nint expected)
    {
        if (!Native.TryGetContext(out nint fg, out nint focus, out uint thread, out var info) || fg != expected) return "context-unavailable";
        var mode = CaretRules.ReadMode(focus, thread);
        var caret = Native.SystemCaret(fg, info);
        uint focusThread = Native.GetWindowThreadProcessId(focus, out _);
        var focusedMode = CaretRules.ReadMode(focus, focusThread);
        return $"mode={mode};focused-thread-mode={focusedMode};same-thread={thread == focusThread};flags={info.Flags};caret-handle={info.Caret != 0};rect={info.CaretRect.Left},{info.CaretRect.Top},{info.CaretRect.Right},{info.CaretRect.Bottom};native={caret.HasValue}";
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);
}
