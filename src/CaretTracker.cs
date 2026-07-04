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

        IntPtr imeWnd = ImmGetDefaultIMEWnd(foreground);
        if (imeWnd == IntPtr.Zero)
            return true; // IME 자체가 없는 레이아웃(순수 영문 등) → 영어로 간주

        // 다른 프로세스여도 IME 창에 변환 모드를 물어볼 수 있다.
        IntPtr ok = SendMessageTimeout(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE,
            IntPtr.Zero, SMTO_ABORTIFHUNG, 120, out IntPtr result);

        if (ok == IntPtr.Zero)
            return true; // 응답 실패(정지/권한) → 영어로 간주

        int mode = result.ToInt32();
        bool korean = (mode & IME_CMODE_NATIVE) != 0;
        return !korean;
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

            var range = selection[0];
            var rects = range.GetBoundingRectangles();

            // 커서만 있고 선택이 없으면(0폭) 경계 사각형이 비어 있을 수 있다.
            // 이때는 캐럿 위치의 한 글자만큼 확장해 사각형을 얻는다.
            if (rects.Length == 0)
            {
                var expanded = range.Clone();
                expanded.ExpandToEnclosingUnit(TextUnit.Character);
                rects = expanded.GetBoundingRectangles();
                if (rects.Length == 0) return default;
            }

            System.Windows.Rect r = rects[0];
            if (double.IsInfinity(r.X) || double.IsNaN(r.X) || r.Height <= 0) return default;

            return new CaretInfo(true, (int)Math.Round(r.Left), (int)Math.Round(r.Bottom));
        }
        catch
        {
            // UIA 호출 실패(권한/타임아웃/COM 예외 등)는 조용히 무시하고 표시하지 않는다.
            return default;
        }
    }

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
