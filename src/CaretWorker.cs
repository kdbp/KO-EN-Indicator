using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using UiaRect = System.Windows.Rect;

namespace KorEng;

/// <summary>
/// Runs only in the isolated --caret-worker process. The parent owns the timeout.
/// The protocol contains geometry only: one HWND request and one x,y,width,height
/// response per line, or '-' when no trustworthy insertion point is available.
/// </summary>
internal static class CaretWorker
{
    private static INativeAutomation? _nativeAutomation;

    internal static string Diagnose(nint foreground)
    {
        try
        {
            if (WorkerNative.GetForegroundWindow() != foreground) return "changed";
            uint thread = WorkerNative.GetWindowThreadProcessId(foreground, out uint pid);
            var element = AutomationElement.FocusedElement;
            if (element == null || element.Current.ProcessId != pid) return "uia-focus-unavailable";
            if (element.Current.IsPassword) return "protected";
            string rangeState = "no-text-pattern";
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? raw) && raw is TextPattern text)
            {
                var selections = text.GetSelection();
                rangeState = "selection-count=" + selections.Length;
                if (selections.Length == 1)
                {
                    var selection = selections[0];
                    rangeState += ";collapsed=" + (selection.CompareEndpoints(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End) == 0) + ";range-rects=" + selection.GetBoundingRectangles().Length;
                    foreach (var unit in new[] { TextUnit.Character, TextUnit.Line, TextUnit.Paragraph })
                    {
                        var expanded = selection.Clone(); expanded.ExpandToEnclosingUnit(unit);
                        var rects = expanded.GetBoundingRectangles();
                        rangeState += ";" + unit + "-rects=" + rects.Length;
                        if (rects.Length == 1) rangeState += ":" + Geometry(rects[0]);
                    }
                    var prev = selection.Clone(); var next = selection.Clone();
                    rangeState += ";move-prev=" + prev.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) + ";move-next=" + next.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
                    object size = selection.GetAttributeValue(TextPattern.FontSizeAttribute);
                    rangeState += ";font-size=" + (size is double d ? d.ToString(CultureInfo.InvariantCulture) : "unknown");
                }
            }
            bool actual = TryNativeCaret(pid, element.GetRuntimeId(), out _);
            bool msaa = TryGetAccessibleCaret(thread, pid, out _);
            bool resolved = TryGetCaret(foreground, out var found);
            return rangeState + ";element=" + Geometry(element.Current.BoundingRectangle) + $";text-pattern2-caret={actual};accessible-caret={msaa};active-end={NativeSelectionEnd(element.GetRuntimeId())};worker={resolved}" + (resolved ? ";found=" + Geometry(found) : "");
        }
        catch (Exception ex) { return "diagnostic-error=" + ex.GetType().Name; }
    }
    private static string Geometry(UiaRect r) => string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.Width},{r.Height}");

    public static int Run()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            string response = "-";
            try
            {
                bool diagnostic = line.StartsWith("D:", StringComparison.Ordinal);
                if (diagnostic) line = line[2..];
                if (line.Length <= 24 &&
                    long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw) &&
                    raw != 0)
                {
                    nint foreground = new(raw);
                    if (diagnostic) response = Diagnose(foreground);
                    else if (TryGetCaret(foreground, out UiaRect rect) &&
                        WorkerNative.GetForegroundWindow() == foreground)
                    {
                        int x = checked((int)Math.Round(rect.X));
                        int y = checked((int)Math.Floor(rect.Y));
                        int width = Math.Max(1, checked((int)Math.Ceiling(rect.Width)));
                        int height = Math.Max(1, checked((int)Math.Ceiling(rect.Height)));
                        response = string.Create(CultureInfo.InvariantCulture, $"{x},{y},{width},{height}");
                    }
                }
            }
            catch (Exception)
            {
                // Providers may disappear, throw, or hang. Never send provider text
                // to the parent; an unresponsive provider is killed by its watchdog.
            }

            Console.WriteLine(response);
            Console.Out.Flush();
        }

        ReleaseNative(_nativeAutomation);
        _nativeAutomation = null;
        return 0;
    }

    private static bool TryGetCaret(nint foreground, out UiaRect result)
    {
        result = UiaRect.Empty;
        if (WorkerNative.GetForegroundWindow() != foreground ||
            !WorkerNative.GetWindowRect(foreground, out WorkerNative.NativeRect window))
            return false;

        uint thread = WorkerNative.GetWindowThreadProcessId(foreground, out uint process);
        if (thread == 0 || process == 0)
            return false;

        AutomationElement? focused = AutomationElement.FocusedElement;
        if (focused is null)
            return false;

        // Only these state/geometry properties are queried. In particular, never
        // query Name, Value, text content, or descendants. Document ranges below
        // are compared by endpoint only; their contents are never read.
        AutomationElement.AutomationElementInformation state = focused.Current;
        if (state.ProcessId != process || state.IsPassword ||
            !state.HasKeyboardFocus || state.IsOffscreen || !state.IsEnabled)
            return false;

        UiaRect elementBounds = state.BoundingRectangle;
        // TextPattern2 can expose the actual caret even in an empty input field,
        // where extending a legacy TextPattern range has no character to use.
        if (TryNativeCaret(process, focused.GetRuntimeId(), out UiaRect actualCaret) &&
            IsInside(actualCaret, window, elementBounds))
        {
            result = actualCaret;
            return FocusStillMatches(foreground, focused, process, null);
        }

        // OBJID_CARET denotes the actual insertion bar, never the input control.
        if (TryGetAccessibleCaret(thread, process, out UiaRect accessible) &&
            IsInside(accessible, window, elementBounds))
        {
            result = accessible;
            return FocusStillMatches(foreground, focused, process, null) &&
                TryGetAccessibleCaret(thread, process, out UiaRect currentAccessible) &&
                SameRectangle(accessible, currentAccessible);
        }

        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObject) || patternObject is not TextPattern pattern)
            return false;
        var selection = pattern.GetSelection();
        if (selection.Length != 1) return false;
        TextPatternRange originalSelection = selection[0];
        TextPatternRange insertion = originalSelection;
        object isReadOnly = insertion.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
        bool readOnlyRange = isReadOnly is bool readOnly && readOnly;

        // A real caret can belong to a terminal's read-only text surface. The
        // less certain range-edge fallback is limited to writable text surfaces.
        if (readOnlyRange)
            return false;

        // A selected address still has an insertion end. Do not reject it before
        // checking the actual caret. For range geometry use the provider's active
        // end, never the complete selected-text rectangle as a caret.
        int activeEnd = 0;
        if (insertion.CompareEndpoints(TextPatternRangeEndpoint.Start, insertion, TextPatternRangeEndpoint.End) != 0)
        {
            activeEnd = NativeSelectionEnd(focused.GetRuntimeId());
            if (activeEnd is not (1 or 2)) return false;
            insertion = insertion.Clone();
            if (activeEnd == 1) insertion.MoveEndpointByRange(TextPatternRangeEndpoint.End, insertion, TextPatternRangeEndpoint.Start);
            else insertion.MoveEndpointByRange(TextPatternRangeEndpoint.Start, insertion, TextPatternRangeEndpoint.End);
        }

        // Explorer's empty WinUI edits report no system, MSAA, character or line
        // caret geometry. Only for a confirmed empty, writable Explorer edit,
        // derive the insertion box from the control's typography and DPI.
        if (activeEnd == 0 && TryEmptyExplorerCaret(foreground, focused, pattern, insertion, out UiaRect empty) &&
            IsInside(empty, window, elementBounds))
        {
            result = empty;
            return FocusStillMatches(foreground, focused, process, originalSelection) &&
                IsEmpty(pattern.DocumentRange);
        }

        // A few providers expose a rectangle even for the degenerate range.
        if (TrySingleRectangle(insertion, out UiaRect direct) &&
            direct.Width <= Math.Max(3, direct.Height * 0.25) &&
            IsInside(direct, window, elementBounds))
        {
            result = new UiaRect(direct.X, direct.Y, Math.Max(1, direct.Width), direct.Height);
            return SelectionStillMatches(foreground, focused, process, originalSelection, activeEnd);
        }

        // Standard UIA returns no rectangle for a degenerate range. Move only a
        // clone's adjacent endpoint by one character; never modify the selection.
        bool hasNext = TryAdjacentCharacter(insertion, true, out UiaRect next);
        bool hasPrevious = TryAdjacentCharacter(insertion, false, out UiaRect previous);
        UiaRect inferred;
        if (hasNext && hasPrevious && SameLine(next, previous))
        {
            double tolerance = Math.Max(3, Math.Min(next.Height, previous.Height) * 0.25);
            double leftToRightGap = Math.Abs(previous.Right - next.Left);
            double rightToLeftGap = Math.Abs(previous.Left - next.Right);
            if (Math.Min(leftToRightGap, rightToLeftGap) > tolerance)
                return false;

            // Adjacent edges also handle an ordinary right-to-left run without
            // reading its characters or guessing from the input language.
            double x = leftToRightGap <= rightToLeftGap
                ? (previous.Right + next.Left) / 2
                : (previous.Left + next.Right) / 2;
            inferred = new UiaRect(x, next.Y, 1, next.Height);
        }
        else if (hasNext && hasPrevious)
        {
            // Line-wrap affinity is ambiguous without a provider caret API.
            // Suppress the marker instead of placing it at the wrong line.
            return false;
        }
        else if (hasNext)
        {
            inferred = new UiaRect(next.Left, next.Top, 1, next.Height);
        }
        else if (hasPrevious)
        {
            inferred = new UiaRect(previous.Right, previous.Top, 1, previous.Height);
        }
        else
        {
            return false;
        }

        if (!IsInside(inferred, window, elementBounds))
            return false;

        result = inferred;
        return SelectionStillMatches(foreground, focused, process, originalSelection, activeEnd);
    }

    private static bool IsEmpty(TextPatternRange range) =>
        range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0;

    private static bool IsExplorerEdit(nint foreground, AutomationElement focused)
    {
        var className = new StringBuilder(64);
        WorkerNative.GetClassName(foreground, className, className.Capacity);
        var state = focused.Current;
        return (className.ToString() == "CabinetWClass" || className.ToString() == "ExploreWClass") &&
            state.ControlType == ControlType.Edit && !state.IsPassword && state.HasKeyboardFocus && !state.IsOffscreen && state.IsEnabled;
    }

    private static bool TryEmptyExplorerCaret(nint foreground, AutomationElement focused, TextPattern pattern,
        TextPatternRange insertion, out UiaRect result)
    {
        result = UiaRect.Empty;
        var state = focused.Current;
        if (!IsExplorerEdit(foreground, focused) || !IsEmpty(pattern.DocumentRange)) return false;
        if (insertion.GetAttributeValue(TextPattern.FontSizeAttribute) is not double points) return false;
        object align = insertion.GetAttributeValue(TextPattern.HorizontalTextAlignmentAttribute);
        int alignment = align is int number ? number : align is HorizontalTextAlignment value ? (int)value : 0;
        result = ExplorerCaretGeometry.Empty(state.BoundingRectangle, WorkerNative.GetDpiForWindow(foreground), points, alignment);
        return !result.IsEmpty;
    }

    private static bool SelectionStillMatches(nint foreground, AutomationElement focused, uint process, TextPatternRange selection, int activeEnd) =>
        FocusStillMatches(foreground, focused, process, selection, activeEnd != 0) &&
        (activeEnd == 0 || NativeSelectionEnd(focused.GetRuntimeId()) == activeEnd);

    private static int NativeSelectionEnd(int[] expectedRuntimeId)
    {
        INativeElement? focused = null;
        INativeTextPattern? pattern = null;
        INativeTextRangeArray? ranges = null;
        INativeTextRange? range = null;
        INativeTextPattern2? caretPattern = null;
        INativeTextRange? caret = null;
        try
        {
            if (_nativeAutomation == null || _nativeAutomation.GetFocusedElement(out focused) < 0 || focused == null ||
                focused.GetRuntimeId(out int[] id) < 0 || !SameRuntimeId(id, expectedRuntimeId)) return 0;
            pattern = NativePattern<INativeTextPattern>(focused, 10014);
            if (pattern == null || pattern.GetSelection(out ranges) < 0 || ranges == null ||
                ranges.GetLength(out int length) < 0 || length != 1 || ranges.GetElement(0, out range) < 0 || range == null) return 0;
            if (range.GetAttributeValue(40037, out object value) >= 0 && value is int endpoint && endpoint is 1 or 2)
                return endpoint;
            // Some providers omit SelectionActiveEnd and the caret rectangle,
            // but still expose a usable caret endpoint. No text is requested.
            caretPattern = NativePattern<INativeTextPattern2>(focused, 10024);
            if (caretPattern == null || caretPattern.GetCaretRange(out bool active, out caret) < 0 || !active || caret == null ||
                caret.CompareEndpoints(0, caret, 1, out int collapsed) < 0 || collapsed != 0) return 0;
            if (caret.CompareEndpoints(0, range, 0, out int start) >= 0 && start == 0) return 1;
            return caret.CompareEndpoints(0, range, 1, out int end) >= 0 && end == 0 ? 2 : 0;
        }
        catch (COMException) { return 0; }
        finally { ReleaseNative(caret); ReleaseNative(caretPattern); ReleaseNative(range); ReleaseNative(ranges); ReleaseNative(pattern); ReleaseNative(focused); }
    }

    private static T? NativePattern<T>(INativeElement focused, int patternId) where T : class
    {
        Guid iid = typeof(T).GUID;
        if (focused.GetCurrentPatternAs(patternId, ref iid, out nint raw) < 0 || raw == 0) return null;
        try { return (T)Marshal.GetObjectForIUnknown(raw); }
        finally { Marshal.Release(raw); }
    }

    private static bool TryNativeCaret(uint process, int[] expectedRuntimeId, out UiaRect result)
    {
        result = UiaRect.Empty;
        INativeElement? focused = null;
        INativeTextPattern2? pattern = null;
        INativeTextRange? range = null;
        INativeTextRange? currentRange = null;
        try
        {
            if (_nativeAutomation is null)
            {
                Guid clsid = new("ff48dba4-60ef-4201-aa87-54103eef594e");
                Guid iid = typeof(INativeAutomation).GUID;
                if (WorkerNative.CoCreateInstance(ref clsid, 0, 1, ref iid, out _nativeAutomation) < 0 ||
                    _nativeAutomation is null)
                    return false;
            }

            if (_nativeAutomation.GetFocusedElement(out focused) < 0 || focused is null ||
                focused.GetRuntimeId(out int[] runtimeId) < 0 ||
                runtimeId is null || !SameRuntimeId(runtimeId, expectedRuntimeId) ||
                focused.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty.Id, out object owner) < 0 ||
                owner is not int pid || pid != process ||
                focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty.Id, out object password) < 0 ||
                password is not bool protectedField || protectedField ||
                focused.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty.Id, out object focus) < 0 ||
                focus is not bool hasFocus || !hasFocus)
                return false;

            pattern = NativePattern<INativeTextPattern2>(focused, 10024);
            if (pattern is null ||
                pattern.GetCaretRange(out bool active, out range) < 0 || !active || range is null ||
                range.CompareEndpoints(0, range, 1, out int lengthComparison) < 0 || lengthComparison != 0 ||
                range.GetBoundingRectangles(out double[] coordinates) < 0 || coordinates is null ||
                coordinates.Length != 4 || coordinates[2] < 0 || coordinates[3] <= 0)
                return false;

            UiaRect rectangle = new(coordinates[0], coordinates[1], coordinates[2], coordinates[3]);
            if (!Plausible(rectangle) || rectangle.Width > Math.Max(3, rectangle.Height * 0.25))
                return false;

            // Recheck the endpoint and geometry after provider calls. A caret can
            // move inside one focused element without any focus/window change.
            if (pattern.GetCaretRange(out bool stillActive, out currentRange) < 0 || !stillActive || currentRange is null ||
                range.CompareEndpoints(0, currentRange, 0, out int startComparison) < 0 || startComparison != 0 ||
                range.CompareEndpoints(1, currentRange, 1, out int endComparison) < 0 || endComparison != 0 ||
                currentRange.GetBoundingRectangles(out double[] latest) < 0 || latest is null || latest.Length != 4 ||
                latest[2] < 0 || latest[3] <= 0 ||
                !SameRectangle(rectangle, new UiaRect(latest[0], latest[1], latest[2], latest[3])))
                return false;

            result = new UiaRect(rectangle.X, rectangle.Y, Math.Max(1, rectangle.Width), rectangle.Height);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ReleaseNative(currentRange);
            ReleaseNative(range);
            ReleaseNative(pattern);
            ReleaseNative(focused);
        }
    }

    private static bool SameRuntimeId(int[] first, int[] second)
    {
        if (first.Length == 0 || first.Length != second.Length)
            return false;
        for (int i = 0; i < first.Length; i++)
            if (first[i] != second[i])
                return false;
        return true;
    }

    private static void ReleaseNative(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);
    }

    private static bool TryAdjacentCharacter(TextPatternRange insertion, bool next, out UiaRect result)
    {
        result = UiaRect.Empty;
        TextPatternRange adjacent = insertion.Clone();
        TextPatternRangeEndpoint endpoint = next
            ? TextPatternRangeEndpoint.End : TextPatternRangeEndpoint.Start;
        int moved = adjacent.MoveEndpointByUnit(endpoint, TextUnit.Character, next ? 1 : -1);
        if (moved != (next ? 1 : -1))
            return false;

        TextPatternRangeEndpoint fixedEndpoint = next
            ? TextPatternRangeEndpoint.Start : TextPatternRangeEndpoint.End;
        if (adjacent.CompareEndpoints(fixedEndpoint, insertion, fixedEndpoint) != 0 ||
            adjacent.CompareEndpoints(TextPatternRangeEndpoint.Start,
                adjacent, TextPatternRangeEndpoint.End) >= 0 ||
            !TrySingleRectangle(adjacent, out UiaRect rectangle))
            return false;

        // Some providers silently promote Character to Word/Line. Reject broad
        // bounds so an entire input box or selected line cannot become a caret.
        if (rectangle.Width > Math.Min(256, Math.Max(8, rectangle.Height * 2.5)))
            return false;

        result = rectangle;
        return true;
    }

    private static bool TrySingleRectangle(TextPatternRange range, out UiaRect result)
    {
        result = UiaRect.Empty;
        UiaRect[] rectangles = range.GetBoundingRectangles();
        if (rectangles.Length != 1 || !Plausible(rectangles[0]))
            return false;
        result = rectangles[0];
        return true;
    }

    private static bool TryGetAccessibleCaret(uint thread, uint process, out UiaRect result)
    {
        result = UiaRect.Empty;
        WorkerNative.GuiThreadInfo info = new() { Size = Marshal.SizeOf<WorkerNative.GuiThreadInfo>() };
        if (!WorkerNative.GetGUIThreadInfo(thread, ref info) || info.Focus == 0)
            return false;

        WorkerNative.GetWindowThreadProcessId(info.Focus, out uint focusProcess);
        if (focusProcess != process)
            return false;

        Guid iid = new("618736e0-3c3d-11cf-810c-00aa00389b71");
        Accessibility.IAccessible? caret = null;
        try
        {
            if (WorkerNative.AccessibleObjectFromWindow(info.Focus, 0xfffffff8,
                    ref iid, out caret) != 0 || caret is null)
                return false;

            object state = caret.get_accState(0);
            if (state is int flags && (flags & (0x00008000 | 0x00010000 | 0x00000001)) != 0)
                return false;

            caret.accLocation(out int left, out int top, out int width, out int height, 0);
            if (width < 0 || height <= 0)
                return false;

            UiaRect rectangle = new(left, top, width, height);
            if (!Plausible(rectangle) || rectangle.Width > Math.Max(12, rectangle.Height * 1.5))
                return false;
            result = new UiaRect(rectangle.X, rectangle.Y, Math.Max(1, rectangle.Width), rectangle.Height);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (caret is not null && Marshal.IsComObject(caret))
                Marshal.ReleaseComObject(caret);
        }
    }

    private static bool FocusStillMatches(nint foreground, AutomationElement original, uint process,
        TextPatternRange? insertion, bool allowSelection = false)
    {
        if (WorkerNative.GetForegroundWindow() != foreground)
            return false;
        AutomationElement? current = AutomationElement.FocusedElement;
        if (current is null || current.Current.ProcessId != process ||
            current.Current.IsPassword || !Automation.Compare(original, current))
            return false;

        if (insertion is not null)
        {
            if (!current.TryGetCurrentPattern(TextPattern.Pattern, out object? currentPattern) ||
                currentPattern is not TextPattern textPattern)
                return false;
            TextPatternRange[] latest = textPattern.GetSelection();
            if (latest.Length != 1 ||
                (!allowSelection && latest[0].CompareEndpoints(TextPatternRangeEndpoint.Start, latest[0], TextPatternRangeEndpoint.End) != 0) ||
                insertion.CompareEndpoints(TextPatternRangeEndpoint.Start, latest[0], TextPatternRangeEndpoint.Start) != 0 ||
                insertion.CompareEndpoints(TextPatternRangeEndpoint.End, latest[0], TextPatternRangeEndpoint.End) != 0)
                return false;
        }

        return WorkerNative.GetForegroundWindow() == foreground;
    }

    private static bool SameRectangle(UiaRect first, UiaRect second) =>
        Math.Abs(first.X - second.X) < 0.5 && Math.Abs(first.Y - second.Y) < 0.5 &&
        Math.Abs(first.Width - second.Width) < 0.5 && Math.Abs(first.Height - second.Height) < 0.5;

    private static bool SameLine(UiaRect a, UiaRect b) =>
        Math.Abs(a.Top - b.Top) <= Math.Max(3, Math.Min(a.Height, b.Height) * 0.25);

    private static bool Plausible(UiaRect rectangle) =>
        !rectangle.IsEmpty && double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) &&
        double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) &&
        rectangle.Width >= 0 && rectangle.Width <= 512 && rectangle.Height >= 4 && rectangle.Height <= 256;

    private static bool IsInside(UiaRect caret, WorkerNative.NativeRect window, UiaRect element)
    {
        if (!Plausible(caret) || caret.Left < window.Left - 2 || caret.Right > window.Right + 2 ||
            caret.Top < window.Top - 2 || caret.Bottom > window.Bottom + 2)
            return false;

        // A provider may omit element bounds. When supplied, use them as another
        // geometry-only stale/offscreen check, never as a placement fallback.
        return element.IsEmpty || element.Width <= 0 || element.Height <= 0 ||
            (caret.Left >= element.Left - 2 && caret.Right <= element.Right + 2 &&
             caret.Top >= element.Top - 2 && caret.Bottom <= element.Bottom + 2);
    }

    // These COM declarations follow the exact method order and native types in
    // Microsoft's Windows SDK UIAutomationClient.h, through the last method used.
    // The earlier, unused declarations preserve the official ABI; they are never
    // called to enumerate the desktop, obtain document content, or search text.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/UIAutomationClient.h
    // https://learn.microsoft.com/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextpattern2-getcaretrange
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeAutomation
    {
        [PreserveSig] int CompareElements(INativeElement first, INativeElement second,
            [MarshalAs(UnmanagedType.Bool)] out bool same);
        [PreserveSig] int CompareRuntimeIds(nint firstSafeArray, nint secondSafeArray,
            [MarshalAs(UnmanagedType.Bool)] out bool same);
        [PreserveSig] int GetRootElement(out nint root);
        [PreserveSig] int ElementFromHandle(nint window, out nint element);
        [PreserveSig] int ElementFromPoint(WorkerNative.NativePoint point, out nint element);
        [PreserveSig] int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out INativeElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] runtimeId);
        [PreserveSig] int FindFirst(int scope, nint condition, out nint found);
        [PreserveSig] int FindAll(int scope, nint condition, out nint foundArray);
        [PreserveSig] int FindFirstBuildCache(int scope, nint condition, nint cacheRequest, out nint found);
        [PreserveSig] int FindAllBuildCache(int scope, nint condition, nint cacheRequest, out nint foundArray);
        [PreserveSig] int BuildUpdatedCache(nint cacheRequest, out nint updatedElement);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault,
            [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault,
            [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid iid,
            out nint pattern);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeTextPattern
    {
        [PreserveSig] int RangeFromPoint(WorkerNative.NativePoint point, out nint range);
        [PreserveSig] int RangeFromChild(INativeElement child, out nint range);
        [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Interface)] out INativeTextRangeArray? ranges);
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeTextRangeArray
    {
        [PreserveSig] int GetLength(out int length);
        [PreserveSig] int GetElement(int index, [MarshalAs(UnmanagedType.Interface)] out INativeTextRange? range);
    }

    [ComImport, Guid("506a921a-fcc9-409f-b23b-37eb74106872"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeTextPattern2
    {
        [PreserveSig] int RangeFromPoint(WorkerNative.NativePoint point, out nint range);
        [PreserveSig] int RangeFromChild(INativeElement child, out nint range);
        [PreserveSig] int GetSelection(out nint ranges);
        [PreserveSig] int GetVisibleRanges(out nint ranges);
        [PreserveSig] int GetDocumentRange(out nint range);
        [PreserveSig] int GetSupportedTextSelection(out int selection);
        [PreserveSig] int RangeFromAnnotation(INativeElement annotation, out nint range);
        [PreserveSig] int GetCaretRange([MarshalAs(UnmanagedType.Bool)] out bool active,
            [MarshalAs(UnmanagedType.Interface)] out INativeTextRange? range);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeTextRange
    {
        [PreserveSig] int Clone(out nint clone);
        [PreserveSig] int Compare(INativeTextRange range, [MarshalAs(UnmanagedType.Bool)] out bool same);
        [PreserveSig] int CompareEndpoints(int firstEndpoint, INativeTextRange range, int secondEndpoint, out int comparison);
        [PreserveSig] int ExpandToEnclosingUnit(int unit);
        [PreserveSig] int FindAttribute(int attributeId, [MarshalAs(UnmanagedType.Struct)] object value,
            [MarshalAs(UnmanagedType.Bool)] bool backward, out nint range);
        [PreserveSig] int FindText([MarshalAs(UnmanagedType.BStr)] string text,
            [MarshalAs(UnmanagedType.Bool)] bool backward, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase, out nint range);
        [PreserveSig] int GetAttributeValue(int attributeId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetBoundingRectangles([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[] rectangles);
    }

    private static class WorkerNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public NativeRect CaretRectangle;
        }

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(nint window, StringBuilder name, int count);
        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out NativeRect rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("oleacc.dll")]
        internal static extern int AccessibleObjectFromWindow(nint window, uint objectId,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible);

        [DllImport("ole32.dll")]
        internal static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out INativeAutomation? instance);
    }
}
