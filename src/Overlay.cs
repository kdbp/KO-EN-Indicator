using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using static KoEngIndicator.Native;

namespace KoEngIndicator;

/// <summary>
/// 커서 아래에 'A'를 그리는 레이어드(per-pixel alpha) 오버레이 창.
/// 클릭을 통과시키고(WS_EX_TRANSPARENT) 활성화되지 않으며(WS_EX_NOACTIVATE)
/// 항상 최상위(WS_EX_TOPMOST)로 유지되어 전체화면(테두리 없는) 게임 위에도 표시된다.
/// </summary>
internal sealed class Overlay : IDisposable
{
    private const string ClassName = "KoEngOverlayWindowClass";
    private static bool _classRegistered;

    // WndProc 델리게이트를 GC로부터 보호하기 위해 정적 필드로 보관
    private static readonly WndProcDelegate _wndProc = StaticWndProc;

    private readonly IntPtr _hwnd;
    private Bitmap _glyph;   // 현재 크기로 그려둔 'A' 비트맵
    private bool _visible;
    private bool _disposed;

    public Overlay()
    {
        _glyph = BadgeRenderer.Render(BadgeRenderer.DefaultSize);
        EnsureClassRegistered();

        int exStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST
                    | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;

        _hwnd = CreateWindowEx(exStyle, ClassName, string.Empty,
            unchecked((uint)WS_POPUP), 0, 0, _glyph.Width, _glyph.Height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("오버레이 창 생성에 실패했습니다. Win32 오류: " + Marshal.GetLastWin32Error());
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            // 문자열 필드는 마샬러가 임시 복사본을 만들고 자동 해제한다.
            // (RegisterClassEx가 클래스 이름을 내부 아톰으로 복사하므로 안전)
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => DefWindowProc(hWnd, msg, wParam, lParam);

    /// <summary>caret 좌하단(x,bottom) 아래에 'A'를 표시한다.</summary>
    public void ShowAt(int caretX, int caretBottom)
    {
        if (_disposed) return;

        // caret 바로 아래, 살짝(2px) 띄워 배치
        int x = caretX;
        int y = caretBottom + 2;

        if (!_visible)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _visible = true;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            hBmp = _glyph.GetHbitmap(Color.FromArgb(0));
            oldBmp = SelectObject(memDc, hBmp);

            var size = new SIZE(_glyph.Width, _glyph.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(x, y);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);

            // 매번 최상위로 끌어올려 전체화면 앱 위에도 유지
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        finally
        {
            // 예외 발생 여부와 무관하게 GDI 개체를 100% 반환한다.
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void HideOverlay()
    {
        if (_disposed || !_visible) return;
        ShowWindow(_hwnd, SW_HIDE);
        _visible = false;
    }

    /// <summary>배지 글자 크기(px)를 바꾼다. 다음 표시부터 새 크기로 그려진다.</summary>
    public void SetSize(int fontPx)
    {
        if (_disposed) return;
        var old = _glyph;
        _glyph = BadgeRenderer.Render(fontPx);
        old.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _glyph.Dispose();
    }
}
