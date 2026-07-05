using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using static KoEngIndicator.Native;

namespace KoEngIndicator;

/// <summary>
/// 포그라운드 창의 (1) 한/영 입력 상태와 (2) 텍스트 커서(caret) 화면 좌표를 조회한다.
/// </summary>
internal static class CaretTracker
{
    public readonly record struct CaretInfo(bool Valid, int X, int Bottom);

    /// <summary>현재 포그라운드 입력이 "영어(알파벳)" 상태이면 true. 한글 모드면 false.</summary>
    public static bool IsEnglish(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return true;

        // 실제 입력 포커스를 가진 컨트롤 기준으로 IME를 조회한다.
        // 새 메모장(RichEditD2DPT)처럼 최상위 창의 IME 창이 변환 모드를
        // 항상 0으로 보고하는 경우가 있어, 포커스 컨트롤이 있으면 그쪽을 쓴다.
        IntPtr target = foreground;
        uint fgTid = GetWindowThreadProcessId(foreground, out _);
        if (fgTid != 0)
        {
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(fgTid, ref gti) && gti.hwndFocus != IntPtr.Zero)
                target = gti.hwndFocus;
        }

        IntPtr imeWnd = ImmGetDefaultIMEWnd(target);
        if (imeWnd == IntPtr.Zero)
            return true; // IME 자체가 없는 레이아웃(순수 영문 등) → 영어로 간주

        // 다른 프로세스여도 IME 창에 변환 모드를 물어볼 수 있다.
        // 주의: SMTO_ABORTIFHUNG를 쓰면, 우리 앱이 캐럿 조회(UI Automation)로 대상 창의
        // UI 스레드를 바쁘게 만든 사이 "응답 없음"으로 오인해 즉시 실패한다(새 메모장에서
        // 한글이 영어로 오판되는 원인). ABORTIFHUNG 없이 타임아웃까지 응답을 기다린다.
        IntPtr ok = SendMessageTimeout(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE,
            IntPtr.Zero, SMTO_NORMAL, 250, out IntPtr result);

        if (ok == IntPtr.Zero)
            return true; // 응답 실패(정지/권한) → 영어로 간주

        int mode = result.ToInt32();
        return (mode & IME_CMODE_NATIVE) == 0;
    }

    /// <summary>
    /// 캐럿 위치를 얻는다. 1차로 고전 시스템 캐럿(GetGUIThreadInfo),
    /// 실패 시 UI Automation(새 메모장·브라우저·Electron 등)으로 폴백한다.
    /// </summary>
    public static CaretInfo GetCaret(IntPtr foreground)
    {
        var byGgi = GetCaretByGgi(foreground);
        if (byGgi.Valid) return byGgi;
        return GetCaretByUia();
    }

    /// <summary>고전 Win32 시스템 캐럿(GetGUIThreadInfo) 기반.</summary>
    private static CaretInfo GetCaretByGgi(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return default;

        uint tid = GetWindowThreadProcessId(foreground, out _);
        if (tid == 0) return default;

        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(tid, ref gti)) return default;
        if (gti.hwndCaret == IntPtr.Zero) return default;

        RECT rc = gti.rcCaret;
        int height = rc.Bottom - rc.Top;

        // caret 사각형이 완전히 0이면(숨김/미지원) 표시하지 않는다.
        if (height <= 0 && rc.Left == 0 && rc.Top == 0) return default;
        if (height <= 0) height = 16; // 높이 정보가 없으면 기본값

        // caret 좌표는 hwndCaret 클라이언트 기준 → 화면 좌표로 변환
        var pt = new POINT(rc.Left, rc.Top + height);
        if (!ClientToScreen(gti.hwndCaret, ref pt)) return default;

        return new CaretInfo(true, pt.X, pt.Y);
    }

    /// <summary>UI Automation의 TextPattern으로 포커스된 편집기의 캐럿 위치를 얻는다.</summary>
    private static CaretInfo GetCaretByUia()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return default;

            // 편집 가능한 텍스트 입력에만 배지를 표시한다.
            // (읽기 전용 웹 본문/정적 텍스트를 클릭했을 때 줄 끝에 배지가 뜨는 문제 방지)
            if (!IsEditable(focused)) return default;

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
                return default;
            if (patternObj is not TextPattern textPattern) return default;

            var selection = textPattern.GetSelection();
            if (selection is null || selection.Length == 0) return default;

            var caretRange = selection[0];

            // 1) 선택/캐럿 범위의 사각형. 표준 Edit·브라우저 리치 에디터(ProseMirror 등)에서 동작.
            var rects = caretRange.GetBoundingRectangles();
            if (rects.Length > 0 && IsValidRect(rects[0]))
            {
                var r = rects[0];
                return new CaretInfo(true, (int)Math.Round(r.Left), (int)Math.Round(r.Bottom));
            }

            // 2) XAML TextBox(윈도우 탐색기 주소창/검색창 등)는 collapsed 캐럿의 사각형을
            //    빈 배열로 준다. [문서 시작 → 캐럿] 범위의 오른쪽 끝이 곧 캐럿 위치다.
            var run = textPattern.DocumentRange.Clone();
            run.MoveEndpointByRange(TextPatternRangeEndpoint.End, caretRange, TextPatternRangeEndpoint.Start);
            var runRects = run.GetBoundingRectangles();
            if (runRects.Length > 0 && IsValidRect(runRects[^1]))
            {
                var last = runRects[^1]; // 여러 줄이면 캐럿이 있는 마지막 줄
                return new CaretInfo(true, (int)Math.Round(last.Right), (int)Math.Round(last.Bottom));
            }

            // 3) 캐럿이 맨 앞이라 위 범위가 비면, 문서 전체 사각형의 왼쪽을 캐럿으로 본다.
            var docRects = textPattern.DocumentRange.GetBoundingRectangles();
            if (docRects.Length > 0 && IsValidRect(docRects[0]))
            {
                var d = docRects[0];
                return new CaretInfo(true, (int)Math.Round(d.Left), (int)Math.Round(d.Bottom));
            }

            // 4) 내용이 완전히 빈 단일 줄 입력창: 요소 자체의 왼쪽 아래 근처에 표시.
            var box = focused.Current.BoundingRectangle;
            if (!box.IsEmpty && box.Width > 0 && box.Height > 0 && box.Height < 80)
                return new CaretInfo(true, (int)Math.Round(box.Left) + 3, (int)Math.Round(box.Bottom) - 6);

            return default;
        }
        catch
        {
            // UIA 호출 실패(권한/타임아웃/COM 예외 등)는 조용히 무시하고 표시하지 않는다.
            return default;
        }
    }

    private static bool IsValidRect(System.Windows.Rect r)
        => !double.IsNaN(r.X) && !double.IsInfinity(r.X) && !double.IsNaN(r.Width) && r.Height > 0;

    /// <summary>
    /// 포커스된 요소가 "직접 글자를 입력할 수 있는" 편집 컨트롤인지 판별한다.
    /// 읽기 전용 문서(웹 본문)나 정적 텍스트는 제외한다.
    /// </summary>
    private static bool IsEditable(AutomationElement el)
    {
        try
        {
            // 1) ValuePattern이 있으면 읽기 전용 여부로 확정 판단
            //    (일반 <input>/<textarea>, 편집창 대부분)
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj)
                && vpObj is ValuePattern vp)
            {
                return !vp.Current.IsReadOnly;
            }

            // 2) ValuePattern이 없는 경우 컨트롤 형식으로 판단
            var ct = el.Current.ControlType;

            // 읽기 전용 웹 본문/문서, 정적 텍스트, 목록/표 등은 배지 대상 아님
            if (ct == ControlType.Document) return false;
            if (ct == ControlType.Text) return false;
            if (ct == ControlType.List || ct == ControlType.ListItem) return false;
            if (ct == ControlType.Table || ct == ControlType.DataItem) return false;
            if (ct == ControlType.Pane) return false;

            // Edit, Group(contenteditable/ProseMirror 등 리치 에디터) 등은 편집 가능으로 간주
            return true;
        }
        catch
        {
            return false;
        }
    }
}
