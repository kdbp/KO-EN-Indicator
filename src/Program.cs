using System.Windows.Forms;

namespace KoEngIndicator;

internal static class Program
{
    private static Mutex? _mutex;
    private const string MutexName = "KoEngIndicator_SingleInstance_2A9F";
    public const string QuitEventName = "KoEngIndicator_Quit_2A9F";

    [STAThread]
    private static void Main(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew && !TakeOverFromExisting())
            return; // 기존 인스턴스 인수 실패 → 종료

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool startMinimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(startMinimized));

        GC.KeepAlive(_mutex);
    }

    /// <summary>
    /// 이미 실행 중인 인스턴스를 종료시키고 뮤텍스를 넘겨받는다(자동 교체).
    /// 1) 종료 신호(이벤트)로 정상 종료를 유도하고, 2) 그래도 안 되면 강제 종료한다.
    /// </summary>
    private static bool TakeOverFromExisting()
    {
        // 1) 기존 인스턴스에 종료 신호를 보낸다.
        try
        {
            using var ev = EventWaitHandle.OpenExisting(QuitEventName);
            ev.Set();
        }
        catch { /* 종료 신호 리스너가 없는(구) 버전 → 아래 강제 종료로 처리 */ }

        if (WaitForMutex(1500)) return true;

        // 2) 정상 종료가 안 되면(구 버전 등) 동일 이름 프로세스를 강제 종료한다.
        KillOtherInstances();
        return WaitForMutex(2500);
    }

    private static bool WaitForMutex(int milliseconds)
    {
        try
        {
            return _mutex!.WaitOne(milliseconds);
        }
        catch (AbandonedMutexException)
        {
            return true; // 소유 프로세스가 종료되어 버려진 뮤텍스 → 우리가 소유권 획득
        }
    }

    private static void KillOtherInstances()
    {
        var me = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id) continue;
            try { p.Kill(); p.WaitForExit(2000); } catch { /* 접근 불가 등 무시 */ }
        }
    }
}
