using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KorEng;

internal static class SelfTests
{
    private static readonly List<string> Results = new();
    private static int failures;
    private static int skipped;
    private static void Check(string name, bool result)
    {
        Results.Add((result ? "PASS " : "FAIL ") + name);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "test-results", "progress.txt"), Results[^1] + Environment.NewLine);
        if (!result) failures++;
    }

    internal static int Run()
    {
        // All files, text and windows here are synthetic and belong to this app.
        // No registry, startup entry, external app, document or browsing history is accessed.
        string root = Path.Combine(AppContext.BaseDirectory, "test-results");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "progress.txt"), "");
        try
        {
            Check("English layout", CaretRules.Classify(0x0409, false, 0, false, 0) == InputMode.English);
            Check("UK English layout", CaretRules.Classify(0x0809, false, 0, false, 0) == InputMode.English);
            Check("Korean native mode hidden", CaretRules.Classify(0x0412, true, 1, true, 1) == InputMode.Korean);
            Check("Korean IME Latin mode", CaretRules.Classify(0x0412, true, 1, true, 0) == InputMode.English);
            Check("Closed Korean IME", CaretRules.Classify(0x0412, true, 0, false, 0) == InputMode.English);
            Check("Failed IME status is unknown", CaretRules.Classify(0x0412, false, 0, true, 0) == InputMode.Unknown);
            Check("Failed conversion is unknown", CaretRules.Classify(0x0412, true, 1, false, 0) == InputMode.Unknown);
            Check("Other languages hidden", CaretRules.Classify(0x0411, true, 0, true, 0) == InputMode.Other);
            Check("Modifier conversion bits respected", CaretRules.Classify(0x0412, true, 1, true, 9) == InputMode.Korean);
            Check("Malformed worker response rejected", CaretClient.Parse("garbage") == null);
            Check("Broad selection rejected", CaretClient.Parse("20,40,600,20") == null);
            Check("Zero height rejected", CaretClient.Parse("20,40,1,0") == null);
            Check("Overflow coordinate rejected", CaretClient.Parse("21474836470,40,1,20") == null);
            Check("Negative monitor coordinate accepted", CaretClient.Parse("-1900,40,1,20") == new Rectangle(-1900, 40, 1, 20));
            TestExplorerGeometry();
            Check("Quoted startup path", StartupRegistration.Command(@"C:\Example Folder\KorEng.exe") == "\"C:\\Example Folder\\KorEng.exe\" --startup");

            string settingsPath = Path.Combine(root, "synthetic.settings.json");
            var settings = new Settings { Enabled = false, StartWithWindows = false, HasWindowBounds = true, X = -1900, Y = 40, Width = 600, Height = 430 };
            Check("Atomic settings save", settings.Save(settingsPath));
            var restored = Settings.Load(settingsPath);
            Check("Settings roundtrip", !restored.Enabled && !restored.StartWithWindows && restored.X == -1900 && restored.Width == 600);
            Check("No leftover temporary settings", !File.Exists(settingsPath + ".tmp"));
            File.WriteAllText(settingsPath, "{broken");
            Check("Damaged settings recover", Settings.Load(settingsPath).Enabled);
            File.Delete(settingsPath);
            using (var fakeIme = new SyntheticIme())
            {
                nint handle = fakeIme.Handle;
                fakeIme.Open = 1; fakeIme.Conversion = 1;
                var mode = CaretRules.ReadImeWindow(0x0412, handle);
                Check("Real message: Korean native hides (" + mode + ", requests " + fakeIme.Requests + ")", mode == InputMode.Korean);
                fakeIme.Conversion = 0;
                Check("Real message: successful zero means English", CaretRules.ReadImeWindow(0x0412, handle) == InputMode.English);
                fakeIme.Open = 0;
                Check("Real message: closed means English", CaretRules.ReadImeWindow(0x0412, handle) == InputMode.English);
                fakeIme.Destroy();
                Check("Real message: invalid handle is unknown", CaretRules.ReadImeWindow(0x0412, handle) == InputMode.Unknown);
            }

            using var main = new MainForm(true, false);
            main.CreateControl();
            Check("Version in title", main.Text.EndsWith("v1.0.1", StringComparison.Ordinal));
            Check("Dark background", main.BackColor.GetBrightness() < 0.2f);
            main.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(main.Width, main.Height))
            {
                main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, main.Size));
                bitmap.Save(Path.Combine(root, "settings-preview.png"), ImageFormat.Png);
            }
            main.Close(); Application.DoEvents();
            Check("Close hides without exit", !main.IsDisposed && !main.Visible);
            main.Show(); main.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Check("Minimize hides", !main.Visible && !main.IsDisposed);
            main.ShowSettingsWindow();
            Application.DoEvents();
            Check("Restore after minimize", main.Visible && main.WindowState == FormWindowState.Normal);
            main.Hide();

            RunOwnFixture(root);
            TestWorkerProtocol();
            main.AllowExit = true; main.Close();
            Check("Explicit exit closes", main.IsDisposed);
            MeasureStartup(root);
        }
        catch (Exception ex)
        {
            Results.Add("FAIL exception: " + ex.GetType().Name + " " + ex.Message);
            failures++;
        }
        Results.Add($"RESULT {Results.Count - failures - skipped} passed, {failures} failed, {skipped} unavailable");
        File.WriteAllLines(Path.Combine(root, "verification.txt"), Results);
        return failures == 0 ? 0 : 1;
    }

    private static void TestExplorerGeometry()
    {
        var bounds = new System.Windows.Rect(100, 200, 300, 32);
        var empty = ExplorerCaretGeometry.Empty(bounds, 96, 10.5, 0);
        Check("Empty Explorer field places marker at text inset", !empty.IsEmpty && empty.X == 111 && empty.Top > bounds.Top && empty.Bottom < bounds.Bottom);
        var scaled = ExplorerCaretGeometry.Empty(new System.Windows.Rect(175, 350, 525, 56), 168, 10.5, 0);
        Check("Empty Explorer field scales at 175 percent", !scaled.IsEmpty && Math.Abs(scaled.X - empty.X * 1.75) < 0.01 && Math.Abs(scaled.Height - empty.Height * 1.75) < 0.01);
        var negative = ExplorerCaretGeometry.Empty(new System.Windows.Rect(-1800, 200, 300, 32), 96, 10.5, 0);
        Check("Empty Explorer field supports negative monitor coordinates", !negative.IsEmpty && negative.X == -1789);
        Check("Empty Explorer fallback rejects multiline surfaces", ExplorerCaretGeometry.Empty(new System.Windows.Rect(100, 200, 300, 200), 96, 10.5, 0).IsEmpty);
        Check("Empty Explorer fallback rejects unknown DPI", ExplorerCaretGeometry.Empty(bounds, 0, 10.5, 0).IsEmpty);
        Check("Empty Explorer fallback rejects invalid typography", ExplorerCaretGeometry.Empty(bounds, 96, double.NaN, 0).IsEmpty);
    }

    private static void RunOwnFixture(string root)
    {
        using var fixture = new Form
        {
            Text = "KorEng synthetic fixture", ClientSize = new Size(480, 180),
            StartPosition = FormStartPosition.CenterScreen, TopMost = true,
            BackColor = Color.FromArgb(24, 30, 43)
        };
        using var box = new TextBox { Text = "Synthetic input only", Bounds = new Rectangle(30, 40, 400, 35), Font = new Font("Segoe UI", 14f) };
        fixture.Controls.Add(box);
        fixture.Show(); fixture.Activate(); box.Focus(); box.Select(box.TextLength, 0);
        Native.SetForegroundWindow(fixture.Handle);
        Application.DoEvents();
        if (Native.GetForegroundWindow() != fixture.Handle)
        {
            Results.Add("UNAVAILABLE Foreground activation is denied by this execution desktop; live caret tracking and overlay focus checks were not run.");
            skipped++;
            return;
        }
        var info = new Native.GUITHREADINFO { Size = (uint)Marshal.SizeOf<Native.GUITHREADINFO>() };
        uint thread = Native.GetWindowThreadProcessId(fixture.Handle, out _);
        bool hasInfo = Native.GetGUIThreadInfo(thread, ref info);
        var caret = hasInfo ? Native.SystemCaret(fixture.Handle, info) : null;
        Check("Owned Win32 caret detected", caret.HasValue);
        if (caret.HasValue)
        {
            using var overlay = new OverlayForm();
            bool focusedBefore = box.Focused && Native.GetForegroundWindow() == fixture.Handle;
            overlay.ShowIndicator(caret.Value); Application.DoEvents();
            Check("Overlay visible", overlay.Visible);
            Check("Overlay preserves keyboard focus (before=" + focusedBefore + ", fixture=" + (Native.GetForegroundWindow() == fixture.Handle) + ", overlay=" + (Native.GetForegroundWindow() == overlay.Handle) + ", input=" + box.Focused + ")", Native.GetForegroundWindow() == fixture.Handle && box.Focused);
            long styles = GetWindowLongPtr(overlay.Handle, -20).ToInt64();
            Check("Overlay nonactivating and click-through", (styles & 0x080800a0) == 0x080800a0);
            Check("Overlay remains topmost without managed activation", (styles & 8) != 0);
            Check("Overlay below caret", overlay.Top >= caret.Value.Bottom);
            overlay.HideIndicator();
            Check("Overlay hides", !overlay.Visible);
            overlay.ShowIndicator(caret.Value); Application.DoEvents();
            Check("Repeated overlay display preserves keyboard focus", overlay.Visible && Native.GetForegroundWindow() == fixture.Handle && box.Focused);
            overlay.HideIndicator();
        }

        TestIndependentCaretFixture();
        fixture.Close();
    }

    internal static int RunCaretFixture()
    {
        using var fixture = new Form
        {
            Text = "KorEng synthetic caret fixture", ClientSize = new Size(480, 180),
            StartPosition = FormStartPosition.CenterScreen, TopMost = true,
            BackColor = Color.FromArgb(24, 30, 43)
        };
        using var box = new TextBox { Text = "Synthetic input only", Bounds = new Rectangle(30, 40, 400, 35), Font = new Font("Segoe UI", 14f) };
        fixture.Controls.Add(box);
        fixture.Shown += (_, _) =>
        {
            fixture.Activate(); box.Focus(); box.Select(box.TextLength, 0);
            Native.SetForegroundWindow(fixture.Handle);
            Console.WriteLine(fixture.Handle.ToInt64() + "," + box.Handle.ToInt64()); Console.Out.Flush();
            _ = Task.Run(() =>
            {
                Console.ReadLine();
                try { fixture.BeginInvoke(new Action(fixture.Close)); } catch (InvalidOperationException) { }
            });
        };
        using var timeout = new System.Windows.Forms.Timer { Interval = 15000 };
        timeout.Tick += (_, _) => fixture.Close(); timeout.Start();
        Application.Run(fixture);
        return 0;
    }

    private static void TestIndependentCaretFixture()
    {
        // A separate UI process keeps its normal message loop running while UIA
        // serves provider requests. Only the fixture's two handles cross the pipe.
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--caret-fixture");
        using var fixture = Process.Start(start)!;
        try
        {
            var handles = Pump(fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!.Split(',');
            nint window = new(long.Parse(handles[0]));
            nint focus = new(long.Parse(handles[1]));
            Native.SetForegroundWindow(window);
            Application.DoEvents();
            if (Native.GetForegroundWindow() != window)
            {
                Results.Add("UNAVAILABLE Independent fixture foreground activation denied; worker caret integration was not run.");
                skipped++;
                return;
            }
        using var worker = new CaretClient();
        var task = worker.Query(window, focus);
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.ElapsedMilliseconds < 2500)
        {
            Application.DoEvents(); System.Threading.Thread.Sleep(10);
        }
        Check("Isolated worker responds within limit", task.IsCompleted);
        if (task.IsCompletedSuccessfully && !task.Result.HasValue)
        {
            // A cold helper may exceed the freshness budget; only a fresh second
            // reply is accepted for display, just as in normal operation.
            task = worker.Query(window, focus);
            elapsed.Restart();
            while (!task.IsCompleted && elapsed.ElapsedMilliseconds < 2500)
            {
                Application.DoEvents(); System.Threading.Thread.Sleep(10);
            }
        }
        Check("Worker detects owned caret", task.IsCompletedSuccessfully && task.Result.HasValue);
        worker.SetActive(false);
        Check("Disabled worker rejects requests", Pump(worker.Query(window, focus)) == null);
        worker.Stop();
        }
        finally
        {
            fixture.StandardInput.Close();
            if (!fixture.WaitForExit(2000)) fixture.Kill();
        }
    }

    private static T Pump<T>(Task<T> task)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!task.IsCompleted && stopwatch.ElapsedMilliseconds < 3000)
        {
            Application.DoEvents(); System.Threading.Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic test timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static void TestWorkerProtocol()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--caret-worker");
        using var child = Process.Start(start)!;
        try
        {
            // Zero is rejected before any desktop or accessibility access.
            child.StandardInput.WriteLine("0"); child.StandardInput.Flush();
            string? response = Pump(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            Check("Bundled helper protocol and invalid-target rejection", response == "-");
            child.StandardInput.Close();
            Check("Bundled helper exits cleanly on EOF", child.WaitForExit(2000) && child.ExitCode == 0);
        }
        finally { if (!child.HasExited) child.Kill(); }
        using var disabled = new CaretClient();
        disabled.SetActive(false);
        Check("OFF prevents helper startup", Pump(disabled.Query(0, 0)) == null);
    }

    private static void MeasureStartup(string root)
    {
        string marker = Path.Combine(root, "startup-ready.txt");
        string extraction = Path.Combine(root, "cold-runtime-cache");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--measure-ui"); info.ArgumentList.Add(marker);
        info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extraction;
        long start = Environment.TickCount64;
        using var process = Process.Start(info)!;
        bool completed = process.WaitForExit(15000);
        if (!completed) process.Kill();
        long elapsed = File.Exists(marker) && long.TryParse(File.ReadAllText(marker), out long ready) ? ready - start : long.MaxValue;
        Check("Cold single-file UI ready under 3 seconds (" + elapsed + " ms)", completed && elapsed < 3000);
        if (File.Exists(marker)) File.Delete(marker);
        if (Directory.Exists(extraction)) Directory.Delete(extraction, true);
    }

    private sealed class SyntheticIme : NativeWindow, IDisposable
    {
        internal int Open, Conversion, Requests;
        internal SyntheticIme() { CreateHandle(new CreateParams { Caption = "KorEng owned IME fixture" }); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0283)
            {
                Requests++;
                m.Result = m.WParam.ToInt64() == 5 ? new nint(Open) : new nint(Conversion); return;
            }
            base.WndProc(ref m);
        }
        internal void Destroy() => DestroyHandle();
        public void Dispose() { if (Handle != 0) DestroyHandle(); }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
