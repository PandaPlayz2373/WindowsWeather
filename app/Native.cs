using System.Runtime.InteropServices;

namespace WindowsWeather;

// Plain Win32 calls. Every signature is blittable so Native AOT needs no runtime marshalling.
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; public uint lPrivate; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFOEXW
{
    public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
    public unsafe fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
public struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public uint Data; }

public static unsafe partial class Native
{
    const string U = "user32.dll";

    public const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000,
        WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_TABSTOP = 0x00010000, WS_BORDER = 0x00800000,
        WS_MINIMIZEBOX = 0x00020000;
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000,
        WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOREDIRECTIONBITMAP = 0x00200000,
        WS_EX_APPWINDOW = 0x40000, WS_EX_CLIENTEDGE = 0x200, WS_EX_DLGMODALFRAME = 0x1;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_QUIT = 0x12, WM_TIMER = 0x113, WM_COMMAND = 0x111,
        WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0, WM_SETTINGCHANGE = 0x1A, WM_POWERBROADCAST = 0x218,
        WM_WTSSESSION_CHANGE = 0x2B1, WM_APP = 0x8000, WM_CONTEXTMENU = 0x7B, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONUP = 0x205, WM_LBUTTONUP = 0x202, WM_NULL = 0, WM_SETFONT = 0x30, WM_NCHITTEST = 0x84,
        WM_MOUSEACTIVATE = 0x21, WM_KEYDOWN = 0x100, WM_ERASEBKGND = 0x14, WM_SHOWWINDOW = 0x18;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40,
        SWP_NOOWNERZORDER = 0x200;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint PM_REMOVE = 1;
    public const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x4;
    public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258, INFINITE = 0xFFFFFFFF;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MOVESIZESTART = 0xA, EVENT_SYSTEM_MOVESIZEEND = 0xB,
        EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17, EVENT_OBJECT_DESTROY = 0x8001,
        EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B,
        EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const int OBJID_WINDOW = 0, OBJID_CURSOR = -9;
    public const uint GA_ROOT = 2, GW_OWNER = 4;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_POPUP = 0x10, MF_CHECKED = 0x8, MF_GRAYED = 0x1,
        MF_DISABLED = 0x2, MFT_RADIOCHECK = 0x200;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80, TPM_BOTTOMALIGN = 0x20;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint LWA_ALPHA = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;
    public const uint WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    public const int MA_NOACTIVATE = 3, HTTRANSPARENT = -1;
    public const uint ES_AUTOHSCROLL = 0x80, BS_DEFPUSHBUTTON = 1, BS_PUSHBUTTON = 0;
    public const int IDOK = 1, IDCANCEL = 2;

    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    public static readonly Guid GUID_POWER_SAVING_STATUS = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");

    [DllImport(U)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(U)] public static extern nint CreateWindowExW(int ex, char* cls, char* name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport(U)] public static extern nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [DllImport(U)] public static extern int DestroyWindow(nint h);
    [DllImport(U)] public static extern int ShowWindow(nint h, int cmd);
    [DllImport(U)] public static extern int SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport(U)] public static extern int PeekMessageW(MSG* m, nint h, uint min, uint max, uint remove);
    [DllImport(U)] public static extern int TranslateMessage(MSG* m);
    [DllImport(U)] public static extern nint DispatchMessageW(MSG* m);
    [DllImport(U)] public static extern int IsDialogMessageW(nint dlg, MSG* m);
    [DllImport(U)] public static extern int PostMessageW(nint h, uint m, nuint w, nint l);
    [DllImport(U)] public static extern nint SendMessageW(nint h, uint m, nuint w, nint l);
    [DllImport(U)] public static extern void PostQuitMessage(int code);
    [DllImport(U)] public static extern uint MsgWaitForMultipleObjectsEx(uint n, nint* handles, uint ms, uint wake, uint flags);
    [DllImport(U)] public static extern nint SetWinEventHook(uint min, uint max, nint mod, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc, uint pid, uint tid, uint flags);
    [DllImport(U)] public static extern int UnhookWinEvent(nint h);
    [DllImport(U)] public static extern int GetCursorPos(POINT* p);
    [DllImport(U)] public static extern int EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint l);
    [DllImport(U)] public static extern nint GetWindowLongPtrW(nint h, int idx);
    [DllImport(U)] public static extern int IsWindowVisible(nint h);
    [DllImport(U)] public static extern int IsWindow(nint h);
    [DllImport(U)] public static extern int IsIconic(nint h);
    [DllImport(U)] public static extern int IsZoomed(nint h);
    [DllImport(U)] public static extern nint GetWindow(nint h, uint cmd);
    [DllImport(U)] public static extern nint GetAncestor(nint h, uint flags);
    [DllImport(U)] public static extern int InternalGetWindowText(nint h, char* buf, int max);
    [DllImport(U)] public static extern int GetClassNameW(nint h, char* buf, int max);
    [DllImport(U)] public static extern nint FindWindowW(char* cls, char* name);
    [DllImport(U)] public static extern nint FindWindowExW(nint parent, nint after, char* cls, char* name);
    [DllImport(U)] public static extern int GetWindowRect(nint h, RECT* r);
    [DllImport(U)] public static extern int GetClientRect(nint h, RECT* r);
    [DllImport(U)] public static extern int EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> cb, nint l);
    [DllImport(U)] public static extern int GetMonitorInfoW(nint mon, MONITORINFOEXW* mi);
    [DllImport(U)] public static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport(U)] public static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport(U)] public static extern uint RegisterWindowMessageW(char* name);
    [DllImport(U)] public static extern nint CreatePopupMenu();
    [DllImport(U)] public static extern int AppendMenuW(nint menu, uint flags, nuint id, char* text);
    [DllImport(U)] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint h, nint tpm);
    [DllImport(U)] public static extern int DestroyMenu(nint menu);
    [DllImport(U)] public static extern int SetForegroundWindow(nint h);
    [DllImport(U)] public static extern nint GetForegroundWindow();
    [DllImport(U)] public static extern int SetWindowDisplayAffinity(nint h, uint aff);
    [DllImport(U)] public static extern int SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport(U)] public static extern nint LoadCursorW(nint inst, nint id);
    [DllImport(U)] public static extern nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [DllImport(U)] public static extern int KillTimer(nint h, nuint id);
    [DllImport(U)] public static extern uint GetWindowThreadProcessId(nint h, uint* pid);
    [DllImport(U)] public static extern nint GetShellWindow();
    [DllImport(U)] public static extern nint RegisterPowerSettingNotification(nint h, Guid* g, uint flags);
    [DllImport(U)] public static extern int SetWindowTextW(nint h, char* text);
    [DllImport(U)] public static extern int GetWindowTextW(nint h, char* buf, int max);
    [DllImport(U)] public static extern nint SetFocus(nint h);
    [DllImport(U)] public static extern uint GetDpiForWindow(nint h);
    [DllImport(U)] public static extern uint GetDpiForSystem();
    [DllImport(U)] public static extern int DestroyIcon(nint h);
    [DllImport(U)] public static extern nint GetDlgItem(nint h, int id);
    [DllImport(U)] public static extern int GetSystemMetrics(int i);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attr, void* val, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint h, int attr, void* val, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(nint h, MARGINS* m);
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [DllImport(U)] public static extern int TrackMouseEvent(TRACKMOUSEEVENT* t);
    [DllImport(U)] public static extern nint SetCapture(nint h);
    [DllImport(U)] public static extern int SetWindowRgn(nint h, nint rgn, int redraw);
    [DllImport("gdi32.dll")] public static extern nint CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport(U)] public static extern int ReleaseCapture();
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* d);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(int* state);
    [DllImport("shell32.dll")] public static extern nuint SHAppBarMessage(uint msg, APPBARDATA* data);
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABN_FULLSCREENAPP = 2;
    [DllImport("shell32.dll")] public static extern uint ExtractIconExW(char* file, int index, nint* large, nint* small, uint n);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint mon, int type, uint* x, uint* y);
    [DllImport("wtsapi32.dll")] public static extern int WTSRegisterSessionNotification(nint h, uint flags);
    [DllImport("gdi32.dll")] public static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, char* face);

    [DllImport("kernel32.dll")] public static extern nint CreateWaitableTimerExW(nint attrs, char* name, uint flags, uint access);
    [DllImport("kernel32.dll")] public static extern int SetWaitableTimer(nint h, long* due, int period, nint completion, nint arg, int resume);
    [DllImport("kernel32.dll")] public static extern int CancelWaitableTimer(nint h);
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32.dll")] public static extern nint CreateMutexW(nint attrs, int owner, char* name);
    [DllImport("kernel32.dll")] public static extern int CloseHandle(nint h);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern int K32EmptyWorkingSet(nint proc);
    [DllImport("kernel32.dll")] public static extern int SetProcessInformation(nint proc, int cls, void* info, uint size);
    [DllImport("kernel32.dll")] public static extern int SetThreadInformation(nint thread, int cls, void* info, uint size);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentThread();
    [StructLayout(LayoutKind.Sequential)] public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] public static extern int GetSystemPowerStatus(SYSTEM_POWER_STATUS* s);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [DllImport(U)] public static extern nint GetDC(nint h);
    [DllImport(U)] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] public static extern int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern int DeleteDC(nint dc);

    /// <summary>Copy a screen rectangle (as composed by DWM) into a top-down BGRA array. One BitBlt.</summary>
    public static uint[]? CaptureScreen(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        nint sdc = GetDC(0), mdc = CreateCompatibleDC(sdc);
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint bmp = CreateDIBSection(sdc, &bi, 0, &bits, 0, 0);
        uint[]? px = null;
        if (bmp != 0)
        {
            nint old = SelectObject(mdc, bmp);
            if (BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020 /*SRCCOPY*/) != 0)
            {
                px = new uint[w * h];
                new ReadOnlySpan<uint>(bits, w * h).CopyTo(px);
            }
            SelectObject(mdc, old);
            DeleteObject(bmp);
        }
        DeleteDC(mdc); ReleaseDC(0, sdc);
        return px;
    }

    public static string WindowText(nint h)
    {
        char* buf = stackalloc char[256];
        int n = InternalGetWindowText(h, buf, 256);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static string ClassName(nint h)
    {
        char* buf = stackalloc char[128];
        int n = GetClassNameW(h, buf, 128);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static bool ClassIs(nint h, string name)
    {
        char* buf = stackalloc char[64];
        int n = GetClassNameW(h, buf, 64);
        return n == name.Length && new ReadOnlySpan<char>(buf, n).SequenceEqual(name);
    }

    public static nint FindWindow(string cls)
    {
        fixed (char* c = cls) return FindWindowW(c, null);
    }

    public static nint FindWindowEx(nint after, string cls)
    {
        fixed (char* c = cls) return FindWindowExW(0, after, c, null);
    }

    public static bool FrameBounds(nint h, out RECT r)
    {
        RECT rr;
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, &rr, sizeof(RECT)) != 0 && GetWindowRect(h, &rr) == 0)
        {
            r = default; return false;
        }
        r = rr; return true;
    }

    public static bool IsCloaked(nint h)
    {
        int c = 0;
        return DwmGetWindowAttribute(h, DWMWA_CLOAKED, &c, 4) == 0 && c != 0;
    }

    public static float DpiScaleForMonitor(nint mon)
    {
        uint x = 96, y = 96;
        if (GetDpiForMonitor(mon, 0, &x, &y) != 0) x = 96;
        return x / 96f;
    }

    public static void SetText(nint h, string s)
    {
        fixed (char* p = s) SetWindowTextW(h, p);
    }

    public static string GetText(nint h)
    {
        char* buf = stackalloc char[256];
        int n = GetWindowTextW(h, buf, 256);
        return n > 0 ? new string(buf, 0, n) : "";
    }
}
