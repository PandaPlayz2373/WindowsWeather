using System.Runtime.InteropServices;

namespace WindowsWeather;

/// <summary>A top-level window (or the taskbar) and the weather sitting on its top edge.</summary>
public sealed class Tracked
{
    public nint Hwnd;
    public bool IsTaskbar;
    public bool IsToast;        // a Windows notification pop-up: snow piles fast, then all falls when it goes
    public bool Fresh = true;   // not yet seen eligible
    public double ProbeAt = -1; // toast: when to measure where its card really starts
    public int ToastTop = -1;   // toast: card top, in px below the window top (-1 = not measured)
    public RECT R;
    public bool Eligible;       // collects snow / beads / glow on its top edge
    public bool Maximized;
    public float Scale = 1;

    // Snow pile: one height per column along the top edge, columns relative to R.Left.
    public float ColW = 3;
    public float[] H = [];
    public float[] Jit = [];
    public float PileTotal;
    public List<(int a, int b)> Spans = new(); // visible column ranges [a, b)

    // Puddle: water depth per column (same columns as the pile).
    public float[] Wt = [];
    public float WaterTotal;

    // Rain beads sitting on the edge: x offsets from R.Left.
    public List<float> Beads = new();

    // Motion
    public float Vx, Vy;
    public uint LastEventMs;
    public bool Moving;
    public double GlintStart = -10, LastGlint = -10;
    public float GlintDir = 1;
    public bool Seen;

    public void EnsureColumns()
    {
        float cw = MathF.Max(2, MathF.Round(3 * Scale));
        int cols = Math.Max(1, (int)MathF.Ceiling(R.Width / cw));
        if (cols == H.Length && cw == ColW) return;
        ColW = cw;
        H = new float[cols];
        Wt = new float[cols];
        WaterTotal = 0;
        Jit = new float[cols];
        var rnd = new Random((int)Hwnd ^ cols);
        for (int i = 0; i < cols; i++) Jit[i] = (float)(rnd.NextDouble() * 2 - 1);
        PileTotal = 0;
    }
}

public sealed unsafe class Tracker
{
    public readonly List<Tracked> Z = new();        // occluders + pile holders, top of z-order first
    public readonly Dictionary<nint, Tracked> ByHwnd = new();
    public bool Dirty = true;      // needs a full re-enumeration
    public bool SpansDirty = true; // geometry or z-order changed
    readonly HashSet<nint> _own = new();
    readonly HashSet<nint> _watch = new(); // shown but not yet tracked (e.g. a toast still sliding in)
    public Action<Tracked>? OnBecameEligible;
    nint[] _hooks = [];

    public static Tracker? Instance;
    public Action<Tracked, bool /*drop*/>? OnRemoved;          // pile/beads should fall
    public Action<Tracked, RECT /*old*/, uint /*ms*/>? OnMoved; // geometry changed
    public Action? OnForeground;
    public Action? OnCursor;

    public void AddOwn(nint h) => _own.Add(h);
    public void RemoveOwn(nint h) => _own.Remove(h);

    public void Hook()
    {
        Instance = this;
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> cb = &WinEventProc;
        uint f = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        var list = new List<nint>
        {
            Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, cb, 0, 0, f),
            Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND, 0, cb, 0, 0, f),
            Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, 0, cb, 0, 0, f),
            Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_HIDE, 0, cb, 0, 0, f),
            Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED, 0, cb, 0, 0, f),
        };
        _hooks = list.Where(h => h != 0).ToArray();
        Log.Write($"WinEvent hooks installed: {_hooks.Length}/5 (window positions are polled, not hooked)");
    }

    public void Unhook()
    {
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks = [];
    }

    bool _quiet;
    /// <summary>While paused: keep only the foreground hook (to notice a full-screen app leaving), drop the rest so
    /// mouse moves and window moves no longer wake the process at all.</summary>
    public void SetQuiet(bool quiet)
    {
        if (quiet == _quiet || _hooks.Length == 0) return;
        _quiet = quiet;
        Unhook();
        if (quiet)
        {
            delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> cb = &WinEventProc;
            var h = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, cb, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            _hooks = h != 0 ? [h] : [];
        }
        else { Hook(); Dirty = true; }
    }

    // Counters for testing/diagnostics.
    public static long EventCount, CursorEvents;
    public static readonly long[] EvByType = new long[8]; // fg, movesize, minimize, destroy, show, hide, cloak, other
    public static string EventBreakdown() => $"fg={EvByType[0]} movesize={EvByType[1]} min={EvByType[2]} destroy={EvByType[3]} show={EvByType[4]} hide={EvByType[5]} cloak={EvByType[6]} other={EvByType[7]}";

    [UnmanagedCallersOnly]
    static void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint ms)
    {
        var t = Instance;
        if (t == null) return;
        try { t.Handle(ev, hwnd, idObject, idChild, ms); } catch (Exception e) { Log.Write("winevent: " + e.Message); }
    }

    public static bool DebugToasts;

    void Handle(uint ev, nint hwnd, int idObject, int idChild, uint ms)
    {
        EventCount++;
        EvByType[ev switch { Native.EVENT_SYSTEM_FOREGROUND => 0, Native.EVENT_SYSTEM_MOVESIZESTART or Native.EVENT_SYSTEM_MOVESIZEEND => 1,
            Native.EVENT_SYSTEM_MINIMIZESTART or Native.EVENT_SYSTEM_MINIMIZEEND => 2, Native.EVENT_OBJECT_DESTROY => 3, Native.EVENT_OBJECT_SHOW => 4,
            Native.EVENT_OBJECT_HIDE => 5, Native.EVENT_OBJECT_CLOAKED or Native.EVENT_OBJECT_UNCLOAKED => 6, _ => 7 }]++;
        ms = Stamp(); // a precise clock: window positions are now sampled on frames, not events
        if (DebugToasts && idObject == 0 && hwnd != 0 && ev != Native.EVENT_OBJECT_LOCATIONCHANGE && Native.ClassIs(hwnd, "Windows.UI.Core.CoreWindow"))
        {
            Native.FrameBounds(hwnd, out var dr);
            Log.Write($"toastdbg ev=0x{ev:X} h=0x{hwnd:X} '{Native.WindowText(hwnd)}' child={idChild} vis={Native.IsWindowVisible(hwnd)} cloaked={Native.IsCloaked(hwnd)} top={IsTopLevel(hwnd)} {dr} tracked={ByHwnd.ContainsKey(hwnd)} watch={_watch.Contains(hwnd)}");
        }
        if (ev == Native.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject == Native.OBJID_CURSOR) { CursorEvents++; OnCursor?.Invoke(); return; }
            if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == 0) return;
            if (ByHwnd.TryGetValue(hwnd, out var w)) UpdateRect(w, ms);
            else if (_watch.Contains(hwnd)) { if (IsToastWindow(hwnd)) AddToast(hwnd); else Dirty = true; }
            return;
        }
        if (idObject != Native.OBJID_WINDOW || idChild != 0) return;
        switch (ev)
        {
            case Native.EVENT_SYSTEM_FOREGROUND:
                Dirty = true; OnForeground?.Invoke(); break;
            case Native.EVENT_OBJECT_DESTROY:
                _watch.Remove(hwnd);
                if (ByHwnd.TryGetValue(hwnd, out var d)) { Remove(d, true); SpansDirty = true; }
                break;
            case Native.EVENT_OBJECT_HIDE:
            case Native.EVENT_OBJECT_CLOAKED:
            case Native.EVENT_SYSTEM_MINIMIZESTART:
                _watch.Remove(hwnd);
                if (ByHwnd.TryGetValue(hwnd, out var hdn)) { Remove(hdn, true); SpansDirty = true; }
                Dirty = true;
                break;
            case Native.EVENT_OBJECT_SHOW:
            case Native.EVENT_OBJECT_UNCLOAKED:
            case Native.EVENT_SYSTEM_MINIMIZEEND:
                if (IsTopLevel(hwnd))
                {
                    Dirty = true;
                    if (IsToastWindow(hwnd)) { AddToast(hwnd); break; }
                    if (!ByHwnd.ContainsKey(hwnd)) { if (_watch.Count > 64) _watch.Clear(); _watch.Add(hwnd); WatchUntil = App.Seconds + 3; }
                }
                break;
            case Native.EVENT_SYSTEM_MOVESIZESTART:
                Dragging = hwnd;
                if (ByHwnd.TryGetValue(hwnd, out var ms0)) { ms0.Moving = true; ms0.LastEventMs = ms; ms0.Vx = ms0.Vy = 0; }
                break;
            case Native.EVENT_SYSTEM_MOVESIZEEND:
                if (Dragging == hwnd) Dragging = 0;
                if (ByHwnd.TryGetValue(hwnd, out var ms1)) { ms1.Moving = false; UpdateRect(ms1, ms); }
                Dirty = true;
                break;
        }
    }

    // Notification pop-ups are not returned by EnumWindows on current Windows 11 builds, so they are tracked from
    // their own show / uncloak / location events instead.
    static bool IsToastWindow(nint h) =>
        Native.ClassIs(h, "Windows.UI.Core.CoreWindow") && Native.WindowText(h) == "New notification";

    IReadOnlyList<Monitor> _mons = Array.Empty<Monitor>();

    void AddToast(nint h)
    {
        if (Native.IsWindowVisible(h) == 0 || Native.IsCloaked(h)) return;
        // Still sliding in from zero height: wait for its next location change.
        if (!Native.FrameBounds(h, out var raw) || raw.Width < 50 || raw.Height < 40) { _watch.Add(h); WatchUntil = App.Seconds + 3; return; }
        var t = Get(h, false);
        t.IsToast = true;
        Bounds(t, out var r);
        t.R = r;
        t.Eligible = true;
        _watch.Remove(h);
        if (!Z.Contains(t))
        {
            // Hold off piling until the slide-in has finished and the card's real top has been measured.
            t.ToastTop = -1; t.ProbeAt = App.Seconds + 0.5; t.Eligible = false;
            int at = 0;
            while (at < Z.Count && Z[at].IsTaskbar) at++;
            Z.Insert(at, t);
            Log.Write($"notification appeared at {r}");
        }
        FinishEligibility(t, _mons);
        SpansDirty = true;
    }

    // ------------------------------------------------------------------ positions by polling
    // The location-change WinEvent fires for every caret, progress bar and child window of every app (~100 a second on
    // this laptop, measured 2026-11-07), and each one woke the process. Instead, positions are read where they matter:
    // every frame for the window being dragged and the foreground window, every second for the rest, and only while
    // something on screen depends on them.

    public nint Dragging;            // under a user move / resize (MOVESIZESTART .. MOVESIZEEND)
    public double WatchUntil = -1;   // toasts still sliding in are watched briefly
    double _nextAll, _nextFg;

    static uint Stamp() => (uint)(App.Seconds * 1000);

    public void Poll(double now, bool animating, bool needPositions)
    {
        uint ms = Stamp();
        if (Dragging != 0 && ByHwnd.TryGetValue(Dragging, out var d)) UpdateRect(d, ms);
        if (_watch.Count > 0 && now < WatchUntil)
            foreach (var h in _watch.ToArray()) if (IsToastWindow(h)) AddToast(h);
        if (!animating && !needPositions) return;
        if (animating || now >= _nextFg)
        {
            _nextFg = now + 0.25;
            nint fg = Native.GetForegroundWindow();
            if (fg != 0 && fg != Dragging && ByHwnd.TryGetValue(fg, out var f)) UpdateRect(f, ms);
        }
        if (now >= _nextAll)
        {
            _nextAll = now + 1.0;
            foreach (var t in Z.ToArray()) if (t.Hwnd != Dragging && ByHwnd.ContainsKey(t.Hwnd)) UpdateRect(t, ms);
        }
    }

    /// <summary>Seconds until the next position check is due (when the app would otherwise be idle).</summary>
    public double NextPollIn(double now, bool needPositions)
    {
        double t = double.MaxValue;
        if (Dragging != 0) t = 0.016;
        if (_watch.Count > 0 && now < WatchUntil) t = Math.Min(t, 0.05);
        if (needPositions) t = Math.Min(t, Math.Max(0.01, Math.Min(_nextFg, _nextAll) - now));
        return t;
    }

    public bool ToastProbePending(out double at)
    {
        at = double.MaxValue;
        foreach (var t in Z) if (t.IsToast && t.ProbeAt > 0) at = Math.Min(at, t.ProbeAt);
        return at < double.MaxValue;
    }

    /// <summary>Measure each new toast's card: its height follows its text, inside a fixed-size transparent window.</summary>
    public void ProbeToasts(double now)
    {
        foreach (var t in Z)
        {
            if (!t.IsToast || t.ProbeAt < 0 || now < t.ProbeAt) continue;
            t.ProbeAt = -1;
            if (!Native.FrameBounds(t.Hwnd, out var wr)) continue;
            float s = Native.DpiScaleForMonitor(Native.MonitorFromWindow(t.Hwnd, Native.MONITOR_DEFAULTTONEAREST));
            t.ToastTop = ScanCardTop(wr, s);
            Bounds(t, out var r);
            t.R = r;
            t.Eligible = true;
            FinishEligibility(t, _mons);
            SpansDirty = true;
            Log.Write($"notification card measured at {r} (window {wr}, top inset {t.ToastTop})");
        }
    }

    static int ScanCardTop(RECT wr, float s)
    {
        int fallback = (int)(12 * s);
        var px = Native.CaptureScreen(wr.Left, wr.Top, wr.Width, wr.Height);
        if (px == null) return fallback;
        int w = wr.Width, h = wr.Height;
        int cardL = (int)(16 * s), cardR = w - (int)(17 * s);
        int x1 = cardL + (int)(5 * s), x2 = cardR - (int)(6 * s);
        int refY = h - (int)(26 * s);
        if (x1 < 0 || x2 >= w || refY < 0) return fallback;
        uint refc = px[refY * w + x1];
        static bool Near(uint a, uint b)
        {
            int dr = (int)((a >> 16) & 255) - (int)((b >> 16) & 255), dg = (int)((a >> 8) & 255) - (int)((b >> 8) & 255), db = (int)(a & 255) - (int)(b & 255);
            return Math.Abs(dr) <= 7 && Math.Abs(dg) <= 7 && Math.Abs(db) <= 7; // the card background is very uniform
        }
        // First row (from the top) that starts a long run of card background in both padding columns.
        // A passing snowflake can break a run for a few rows, so allow small gaps.
        int need = (int)(14 * s);
        for (int y = 0; y < refY - need; y++)
        {
            if (!(Near(px[y * w + x1], refc) && Near(px[y * w + x2], refc))) continue;
            int ok = 0;
            for (int k = 0; k < need; k++) if (Near(px[(y + k) * w + x1], refc) || Near(px[(y + k) * w + x2], refc)) ok++;
            if (ok >= need - 3)
            {
                if (y >= 4) return y - 1;
                break; // matched right from the window top: whatever is behind looks like the card; try bottom-up
            }
        }
        // Bottom-up: walk up the padding columns from inside the card until neither looks like card for 3 rows.
        int miss = 0, lastCard = refY;
        for (int y = refY; y >= 0; y--)
        {
            if (Near(px[y * w + x1], refc) || Near(px[y * w + x2], refc)) { lastCard = y; miss = 0; }
            else if (++miss >= 3) break;
        }
        return lastCard >= 4 && lastCard < refY - need ? lastCard - 1 : fallback;
    }

    static bool IsTopLevel(nint h) => h != 0 && (Native.GetWindowLongPtrW(h, Native.GWL_STYLE) & Native.WS_CHILD) == 0;

    void Remove(Tracked t, bool drop)
    {
        OnRemoved?.Invoke(t, drop);
        ByHwnd.Remove(t.Hwnd);
        Z.Remove(t);
    }

    /// <summary>The visible edge: DWM frame bounds, the taskbar's window rect, or a toast's card inside its padding.</summary>
    static bool Bounds(Tracked w, out RECT r)
    {
        RECT rr;
        if (w.IsTaskbar) { Native.GetWindowRect(w.Hwnd, &rr); r = rr; return true; }
        if (!Native.FrameBounds(w.Hwnd, out r)) return false;
        if (w.IsToast && r.Height > 40)
        {
            // The toast card sits inside a transparent margin (measured at 100%: 16 left/right, 12 top, 14 bottom).
            float s = Native.DpiScaleForMonitor(Native.MonitorFromWindow(w.Hwnd, Native.MONITOR_DEFAULTTONEAREST));
            int top = w.ToastTop >= 0 ? w.ToastTop : (int)(12 * s);
            r = new RECT(r.Left + (int)(16 * s), r.Top + top, r.Right - (int)(17 * s), r.Bottom - (int)(14 * s));
        }
        return true;
    }

    public void UpdateRect(Tracked w, uint ms)
    {
        if (!Bounds(w, out var r)) return;
        var old = w.R;
        if (r.Left == old.Left && r.Top == old.Top && r.Right == old.Right && r.Bottom == old.Bottom) return;
        if (w.IsToast && old.Height != r.Height && w.ToastTop >= 0) { w.ProbeAt = App.Seconds + 0.4; }
        w.R = r;
        bool wasMax = w.Maximized;
        w.Maximized = !w.IsTaskbar && Native.IsZoomed(w.Hwnd) != 0;
        OnMoved?.Invoke(w, old, ms);
        w.LastEventMs = ms;
        if (wasMax != w.Maximized) Dirty = true;
        SpansDirty = true;
    }

    // ------------------------------------------------------------------ full enumeration

    static readonly List<nint> _enum = new();

    [UnmanagedCallersOnly]
    static int EnumProc(nint h, nint l) { _enum.Add(h); return 1; }

    public void Refresh(IReadOnlyList<Monitor> mons)
    {
        Dirty = false;
        SpansDirty = true;
        _mons = mons;
        _enum.Clear();
        Native.EnumWindows(&EnumProc, 0);
        foreach (var w in Z) w.Seen = false;
        var newZ = new List<Tracked>(64);

        // The taskbar(s) are topmost: treat them as the top of the stack.
        foreach (var h in _enum)
        {
            if (!(Native.ClassIs(h, "Shell_TrayWnd") || Native.ClassIs(h, "Shell_SecondaryTrayWnd"))) continue;
            if (Native.IsWindowVisible(h) == 0) continue;
            var t = Get(h, true);
            RECT r; Native.GetWindowRect(h, &r);
            t.R = r;
            newZ.Add(t);
        }

        // Toasts we are tracking from events stay on top while they are still showing.
        foreach (var old in Z)
            if (old.IsToast && Native.IsWindow(old.Hwnd) != 0 && Native.IsWindowVisible(old.Hwnd) != 0 && !Native.IsCloaked(old.Hwnd))
            { old.Seen = true; newZ.Add(old); }

        nint shell = Native.GetShellWindow();
        foreach (var h in _enum)
        {
            if (_own.Contains(h) || h == shell) continue;
            if (ByHwnd.TryGetValue(h, out var already) && already.IsToast) continue;
            if (Native.IsWindowVisible(h) == 0 || Native.IsIconic(h) != 0) continue;
            nint ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE);
            // Other click-through overlays (game bars, recorders) neither occlude nor hold snow.
            if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) continue;
            // Floating islands and overlays (layered + tool/no-activate, e.g. a top-centre pill or the GeForce overlay)
            // are mostly see-through: they must not hide the window edges under them (snow fell
            // straight through the middle of a window top under one of these).
            if ((ex & Native.WS_EX_LAYERED) != 0 && ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0)) continue;
            if (Native.ClassIs(h, "Shell_TrayWnd") || Native.ClassIs(h, "Shell_SecondaryTrayWnd") ||
                Native.ClassIs(h, "Progman") || Native.ClassIs(h, "WorkerW") ||
                Native.ClassIs(h, "Shell_SystemDim")) continue; // the translucent dim behind a security prompt hides nothing
            if (Native.IsCloaked(h)) continue;
            if (!Native.FrameBounds(h, out var r)) continue;
            if (r.Width < 50 || r.Height < 30) continue;
            bool known = ByHwnd.ContainsKey(h);
            var t = Get(h, false);
            if (!known) t.IsToast = Native.ClassIs(h, "Windows.UI.Core.CoreWindow") && Native.WindowText(h) == "New notification";
            if (t.IsToast) Bounds(t, out r);
            t.R = r;
            _watch.Remove(h);
            t.Maximized = Native.IsZoomed(h) != 0;
            bool tool = (ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0;
            bool titled = Native.WindowText(h).Length > 0;
            t.Eligible = (!tool && titled && !t.Maximized) || t.IsToast;
            newZ.Add(t);
        }

        // Windows that disappeared since the last pass drop their snow.
        foreach (var old in Z.ToArray())
            if (!old.Seen) Remove(old, true);
        Z.Clear();
        Z.AddRange(newZ);
        foreach (var t in Z) FinishEligibility(t, mons);
    }

    Tracked Get(nint h, bool taskbar)
    {
        if (!ByHwnd.TryGetValue(h, out var t))
        {
            t = new Tracked { Hwnd = h, IsTaskbar = taskbar };
            ByHwnd[h] = t;
        }
        t.Seen = true;
        return t;
    }

    public void FinishEligibility(Tracked t, IReadOnlyList<Monitor> mons)
    {
        var mon = MonitorAt(mons, (t.R.Left + t.R.Right) / 2, t.R.Top + 1);
        bool ok = mon != null;
        if (mon != null)
        {
            t.Scale = mon.Scale;
            if (t.IsTaskbar)
            {
                // Only a bottom-docked, not-hidden taskbar has a top edge to pile on.
                int visibleH = mon.Bounds.Bottom - t.R.Top;
                ok = t.R.Top > mon.Bounds.Top + mon.Bounds.Height / 2 && visibleH >= 20 * mon.Scale && t.R.Width > 200;
                t.Eligible = ok;
            }
            else
            {
                ok = t.Eligible && t.R.Top > mon.Bounds.Top + 2 && t.R.Top < mon.Bounds.Bottom - 30 &&
                     t.R.Width >= 120 * mon.Scale;
            }
        }
        if (t.IsToast && t.ToastTop < 0) ok = false;
        if (!ok && (t.PileTotal > 0 || t.WaterTotal > 0)) OnRemoved?.Invoke(t, true);
        if (!ok) { t.Beads.Clear(); }
        t.Eligible = ok;
        if (ok) t.EnsureColumns();
        if (ok && t.Fresh) { t.Fresh = false; OnBecameEligible?.Invoke(t); }
    }

    public static Monitor? MonitorAt(IReadOnlyList<Monitor> mons, int x, int y)
    {
        foreach (var m in mons)
            if (x >= m.Bounds.Left && x < m.Bounds.Right && y >= m.Bounds.Top && y < m.Bounds.Bottom) return m;
        return null;
    }

    /// <summary>Recompute which parts of each top edge are not covered by windows higher in the z-order.</summary>
    public void ComputeSpans()
    {
        SpansDirty = false;
        var tmp = new List<(int a, int b)>(8);
        for (int i = 0; i < Z.Count; i++)
        {
            var w = Z[i];
            w.Spans.Clear();
            if (!w.Eligible || w.H.Length == 0) continue;
            int y = w.R.Top - 1;
            tmp.Clear();
            tmp.Add((w.R.Left, w.R.Right));
            for (int j = 0; j < i && tmp.Count > 0; j++)
            {
                var o = Z[j].R;
                if (Z[j].IsTaskbar && !w.IsTaskbar && o.Top > y) continue;
                if (y < o.Top || y >= o.Bottom) continue;
                for (int k = tmp.Count - 1; k >= 0; k--)
                {
                    var (a, b) = tmp[k];
                    if (o.Right <= a || o.Left >= b) continue;
                    tmp.RemoveAt(k);
                    if (o.Left > a) tmp.Add((a, o.Left));
                    if (o.Right < b) tmp.Add((o.Right, b));
                }
            }
            foreach (var (a, b) in tmp)
            {
                int ca = (int)MathF.Floor((a - w.R.Left) / w.ColW);
                int cb = (int)MathF.Ceiling((b - w.R.Left) / w.ColW);
                ca = Math.Clamp(ca, 0, w.H.Length); cb = Math.Clamp(cb, 0, w.H.Length);
                if (cb - ca >= 2) w.Spans.Add((ca, cb));
            }
            w.Spans.Sort((p, q) => p.a.CompareTo(q.a));
        }
    }
}
