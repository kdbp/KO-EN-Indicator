using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KorEng;

internal sealed class InputMonitor : IDisposable
{
    private readonly OverlayForm overlay;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 80 };
    private readonly CaretClient worker = new();
    private bool enabled, busy, disposed;
    private long generation;
    private nint shownFor;
    private long lastSuccess;

    internal InputMonitor(OverlayForm overlay)
    {
        this.overlay = overlay;
        timer.Tick += Tick;
    }

    internal void SetEnabled(bool value)
    {
        enabled = value;
        generation++;
        worker.SetActive(value);
        if (value) timer.Start();
        else { timer.Stop(); overlay.HideIndicator(); worker.Stop(); }
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (disposed || !enabled) return;
        nint current = Native.GetForegroundWindow();
        if (current != shownFor || Environment.TickCount64 - lastSuccess > 300) overlay.HideIndicator();
        if (busy) return;
        busy = true;
        long version = generation;
        try
        {
            Detection result = await Task.Run(Probe);
            if (disposed || !enabled || version != generation) return;
            if (result.Caret.HasValue && result.Foreground != 0 && Native.GetForegroundWindow() == result.Foreground)
            {
                overlay.ShowIndicator(result.Caret.Value);
                shownFor = result.Foreground;
                lastSuccess = Environment.TickCount64;
            }
            else overlay.HideIndicator();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!disposed) overlay.HideIndicator();
        }
        finally { busy = false; }
    }

    private async Task<Detection> Probe()
    {
        if (!Native.TryGetContext(out nint foreground, out nint focus, out uint thread, out var info)) return default;
        if (CaretRules.ReadMode(focus, thread) != InputMode.English) return default;
        Rectangle? caret = Native.SystemCaret(foreground, info);
        caret ??= await worker.Query(foreground, focus);
        // A slow provider must not revive a result after focus, IME state or the insertion point changes.
        if (!Native.TryGetContext(out nint after, out nint afterFocus, out uint afterThread, out var afterInfo) ||
            after != foreground || afterFocus != focus || CaretRules.ReadMode(afterFocus, afterThread) != InputMode.English)
            return default;
        var newest = Native.SystemCaret(after, afterInfo);
        if (newest.HasValue) caret = newest;
        return new Detection(foreground, caret);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; enabled = false; generation++;
        timer.Stop(); timer.Dispose(); worker.Dispose();
    }

    private readonly record struct Detection(nint Foreground, Rectangle? Caret);
}

internal sealed class CaretClient : IDisposable
{
    private readonly object gate = new();
    private Process? process;
    private long retryAt, nextProbeAt;
    private nint lastForeground;
    private int failureCount;
    private bool disposed;
    private bool active = true;

    internal async Task<Rectangle?> Query(nint foreground, nint focus)
    {
        long now = Environment.TickCount64;
        if (lastForeground != foreground)
        {
            lastForeground = foreground; retryAt = 0; failureCount = 0;
        }
        if (disposed || now < retryAt) return null;
        // Web inputs can share an HWND. Never reuse geometry based on that HWND.
        if (now < nextProbeAt) await Task.Delay((int)(nextProbeAt - now));
        nextProbeAt = Environment.TickCount64 + 160;
        Process? active = null;
        try
        {
            lock (gate)
            {
                if (disposed || !this.active) return null;
                if (process == null || process.HasExited)
                {
                    process?.Dispose();
                    var start = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true,
                        WorkingDirectory = AppContext.BaseDirectory
                    };
                    start.ArgumentList.Add("--caret-worker");
                    process = Process.Start(start) ?? throw new InvalidOperationException();
                }
                active = process;
            }
            await active.StandardInput.WriteLineAsync(foreground.ToInt64().ToString(CultureInfo.InvariantCulture));
            await active.StandardInput.FlushAsync();
            string? line = await active.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(700));
            if (line == null) throw new InvalidOperationException();
            failureCount = 0;
            if (Environment.TickCount64 - now > 300) return null;
            lock (gate) { if (disposed || !this.active) return null; }
            return Parse(line);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failureCount = Math.Min(5, failureCount + 1);
            retryAt = Environment.TickCount64 + (2000L << (failureCount - 1));
            Stop();
            return null;
        }
    }

    internal static Rectangle? Parse(string line)
    {
        string[] fields = line.Split(',');
        if (fields.Length != 4) return null;
        int[] values = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return null;
        var rect = new Rectangle(values[0], values[1], values[2], values[3]);
        return CaretRules.Plausible(rect) ? rect : null;
    }

    internal void Stop()
    {
        lock (gate)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            process.Dispose(); process = null;
        }
    }
    internal void SetActive(bool value)
    {
        lock (gate)
        {
            active = value;
            if (!value) Stop();
        }
    }
    public void Dispose() { disposed = true; Stop(); }
}
