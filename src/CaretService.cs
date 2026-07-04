namespace KoEngIndicator;

/// <summary>
/// 별도 백그라운드 스레드(MTA)에서 포그라운드 창의 한/영 상태와 캐럿 위치를 계속 계산해
/// 캐시한다. UI 스레드는 <see cref="TryGet"/>로 최신 결과만 읽으므로,
/// UI Automation 호출이 느려도 설정 창/오버레이가 끊기지 않는다.
/// </summary>
internal sealed class CaretService : IDisposable
{
    private readonly Func<bool> _isEnabled;
    private Thread? _thread;
    private volatile bool _running;

    private volatile bool _show;
    private int _x, _y;

    public CaretService(Func<bool> isEnabled) => _isEnabled = isEnabled;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CaretProbe" };
        // UI Automation 클라이언트는 MTA에서 호출하는 것이 안전하다.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool TryGet(out int x, out int y)
    {
        // int 읽기는 원자적이며, 표시 여부(_show)를 마지막에 확인한다.
        x = Volatile.Read(ref _x);
        y = Volatile.Read(ref _y);
        return _show;
    }

    private void Loop()
    {
        while (_running)
        {
            int sleep = 55;
            try
            {
                if (!_isEnabled())
                {
                    _show = false;
                    sleep = 120;
                }
                else
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    if (fg == IntPtr.Zero || !CaretTracker.IsEnglish(fg))
                    {
                        _show = false;
                    }
                    else
                    {
                        var caret = CaretTracker.GetCaret(fg);
                        if (caret.Valid)
                        {
                            Volatile.Write(ref _x, caret.X);
                            Volatile.Write(ref _y, caret.Bottom);
                            _show = true;
                        }
                        else
                        {
                            _show = false;
                        }
                    }
                }
            }
            catch
            {
                _show = false;
            }

            Thread.Sleep(sleep);
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(400); } catch { /* 무시 */ }
    }
}
