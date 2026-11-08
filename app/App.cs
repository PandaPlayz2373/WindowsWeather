using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsWeather;

public static unsafe class App
{
    public static nint HInstance;
    static nint _msgHwnd, _cityHwnd, _cityEdit, _timer, _icon, _font;
    static uint _taskbarCreatedMsg;
    static Gpu? _gpu;
    static readonly World W = new();
    static readonly Tracker T = new();
    static Settings S = new();
    static CachedState St = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double _lastFrame;
    static bool _timerArmed, _running = true;
    static bool _pFull, _pLocked, _pDisplayOff, _pSaver;
    static bool _onBattery;

    /// <summary>The frame-rate ceiling right now: 20 on battery when "Lighter on battery" is on, else the setting.</summary>
    internal static int MaxFps() => S.LighterOnBattery && _onBattery ? 20 : S.Fps;
    internal static bool OnBatteryLight => S.LighterOnBattery && _onBattery;

    static void ReadAcLine()
    {
        if (_forceBattery) return;
        Native.SYSTEM_POWER_STATUS ps;
        if (Native.GetSystemPowerStatus(&ps) != 0) _onBattery = ps.ACLineStatus == 0;
    }
    static bool _fetching;
    static double _lastWeatherFetch = -1e9;
    static int _deviceFailures;
    static long _frames;
    static int _loopErrors;
    static bool _forceBattery;

    const uint WM_APPBAR = Native.WM_APP + 7, WM_TRAY = Native.WM_APP + 1, WM_WEATHER = Native.WM_APP + 2, WM_KICK = Native.WM_APP + 3, WM_DUMP = Native.WM_APP + 4,
        WM_SETPAUSE = Native.WM_APP + 5, WM_SETEFFECT = Native.WM_APP + 6, WM_MENU = Native.WM_APP + 8;
    const nuint TIMER_WEATHER = 1, TIMER_TOPMOST = 2, TIMER_FIRSTRUN = 3;
    static bool _firstRun;
    const string ClassMsg = "WindowsWeatherHost";

    public static string ExePath => Environment.ProcessPath ?? "";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsWeather");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--quit")) { QuitRunning(); return 0; }
        if (args.Contains("--dump")) { PostToRunning(WM_DUMP); return 0; }
        if (args.Contains("--strike")) { PostToRunning(WM_DUMP, 7); return 0; } // a lightning strike now (testing)
        if (args.Contains("--tornado")) { PostToRunning(WM_DUMP, 8); return 0; } // a tornado now (testing)
        if (args.Contains("--tornado-test")) { PostToRunning(WM_DUMP, 9); return 0; } // a tornado that only moves windows titled WWTest
        if (args.Contains("--pause")) { PostToRunning(WM_SETPAUSE, 1); return 0; }
        if (args.Contains("--resume")) { PostToRunning(WM_SETPAUSE, 0); return 0; }
        foreach (var a in args)
            if (a.StartsWith("--set-effect=")) { int i = Array.IndexOf(Effects, a[13..]); if (i >= 0) PostToRunning(WM_SETEFFECT, (nuint)i); return 0; }
        if (args.Contains("--menu")) { PostToRunning(WM_MENU); return 0; } // open the tray flyout (testing)
        foreach (var a in args)
            if (a.StartsWith("--menu=")) { PostToRunning(WM_MENU, (nuint)(a[7..] switch { "effects" => 1, "settings" => 2, _ => 0 }) + 1); return 0; }

        nint mutex;
        fixed (char* n = "Local\\WindowsWeather.SingleInstance") mutex = Native.CreateMutexW(0, 1, n);
        if (Marshal.GetLastWin32Error() == 183 /*ERROR_ALREADY_EXISTS*/) { Log.Write("already running; exiting"); return 0; }

        HInstance = Native.GetModuleHandleW(null);
        ReadAcLine();
        if (args.Contains("--force-battery")) { _onBattery = true; _forceBattery = true; } // testing
        // Quality of service (measured 2026-11-07, see CONTEXT.md): efficiency mode for the process, with the main loop's
        // thread kept at normal QoS so its frame timer is not coalesced (that dropped snow from 30 to ~27 fps).
        string qos = args.FirstOrDefault(a => a.StartsWith("--qos="))?[6..] ?? "main-high";
        if (qos != "off")
        {
            var throttle = stackalloc uint[3] { 1 /*version*/, 1 /*EXECUTION_SPEED*/, 1 /*on*/ };
            Native.SetProcessInformation(Native.GetCurrentProcess(), 4 /*ProcessPowerThrottling*/, throttle, 12);
            if (qos == "main-high")
            {
                var high = stackalloc uint[3] { 1, 1 /*EXECUTION_SPEED*/, 0 /*not throttled*/ };
                Native.SetThreadInformation(Native.GetCurrentThread(), 3 /*ThreadPowerThrottling*/, high, 12);
            }
        }
        Log.Write($"qos={qos}");
        // Leave a trace if anything ever takes the process down.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("CRASH: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("task error: " + e.Exception.Message); e.SetObserved(); };
        _firstRun = !Store.HasSettings;
        S = Store.LoadSettings();
        St = Store.LoadState();
        foreach (var a in args)
            if (a.StartsWith("--effect=")) S.Effect = a[9..];
            else if (a == "--debug-toasts") Tracker.DebugToasts = true;
            else if (a == "--no-saver-pause") S.RestOnBatterySaver = false; // testing only; not saved unless a menu item is used
        Log.Write($"start {ExePath} effect={S.Effect} fps={S.Fps}");

        RegisterClasses();
        CreateHostWindow();
        CreateTray();

        T.OnRemoved = (t, drop) => { if (drop) W.DropPile(t); };
        T.OnMoved = (t, old, ms) => W.OnWindowMoved(t, old, ms);
        T.OnForeground = () => { CheckFullscreen(); RaiseOverlays(); };
        T.OnCursor = OnCursorEvent;
        T.OnBecameEligible = t => W.OnNewSurface(t);
        W.Tracker = T;

        _timer = Native.CreateWaitableTimerExW(0, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);
        if (_timer == 0) _timer = Native.CreateWaitableTimerExW(0, null, 0, 0x1F0003);

        BuildGraphics();
        T.Hook();
        T.Refresh(W.Monitors);
        T.ComputeSpans();

        ApplyWeather(initial: true);
        if (W.Target.Snow > 0.3f) W.SeedPiles(0.45f);
        W.Populate();
        CheckFullscreen();
        UpdatePauseState();
        EnsureRunKey();
        StartWeatherFetch();
        Native.SetTimer(_msgHwnd, TIMER_WEATHER, 15 * 60 * 1000, 0);
        // Re-assert topmost now and then (the taskbar and some apps re-raise themselves).
        Native.SetTimer(_msgHwnd, TIMER_TOPMOST, 5000, 0);

        // Pages touched only during start-up (JSON, HTTP, atlas painting) are not needed again.
        Native.K32EmptyWorkingSet(Native.GetCurrentProcess());

        if (_firstRun) { Store.Save(S); Native.SetTimer(_msgHwnd, TIMER_FIRSTRUN, 3000, 0); }
        Loop();

        Shutdown();
        Native.CloseHandle(mutex);
        return 0;
    }

    static void PostToRunning(uint msg, nuint w = 0)
    {
        nint h;
        fixed (char* c = ClassMsg) h = Native.FindWindowW(c, null);
        if (h != 0) Native.PostMessageW(h, msg, w, 0);
    }

    static void QuitRunning()
    {
        nint h;
        fixed (char* c = ClassMsg) h = Native.FindWindowW(c, null);
        if (h != 0) Native.PostMessageW(h, Native.WM_CLOSE, 0, 0);
        for (int i = 0; i < 50 && h != 0 && Native.IsWindow(h) != 0; i++) Thread.Sleep(100);
    }

    // ------------------------------------------------------------------ main loop

    static void Loop()
    {
        MSG msg;
        nint timer = _timer;
        while (_running)
        {
            uint r = Native.MsgWaitForMultipleObjectsEx(1, &timer, Native.INFINITE, Native.QS_ALLINPUT, Native.MWMO_INPUTAVAILABLE);
            while (Native.PeekMessageW(&msg, 0, 0, 0, Native.PM_REMOVE) != 0)
            {
                if (msg.message == Native.WM_QUIT) { _running = false; break; }
                if (_cityHwnd != 0 && Native.IsDialogMessageW(_cityHwnd, &msg) != 0) continue;
                Native.TranslateMessage(&msg);
                Native.DispatchMessageW(&msg);
            }
            if (!_running) break;
            if (r == Native.WAIT_OBJECT_0) _timerArmed = false;
            try { AfterEvents(); }
            catch (Exception e)
            {
                // One bad frame must never take the app down: log it, re-sync window state, carry on.
                Log.Write("frame error: " + e);
                T.Dirty = true;
                if (++_loopErrors > 50) { Log.Write("too many errors; pausing"); S.Paused = true; UpdatePauseState(); }
            }
        }
    }

    public static double Seconds => Clock.Elapsed.TotalSeconds;
    static double _lastRefresh = -1;
    static bool _needFinalFrame = true;

    /// <summary>Runs after every wake-up: apply window changes, render if a frame is due, then re-arm the timer or sleep.</summary>
    static void AfterEvents()
    {
        if (Paused || _gpu == null) { DisarmTimer(); return; }
        double now = Seconds;
        // Fast path: already armed for the next frame and nothing new happened. Return without touching anything.
        if (_timerArmed && now < _timerDueAt - 0.0008 && !T.Dirty && !T.SpansDirty && !W.Kicked && T.Dragging == 0) return;
        W.Kicked = false;
        T.Poll(now, animating: W.DesiredFps(MaxFps()) > 0, needPositions: W.NeedsPositions());
        if (T.Dirty)
        {
            // Show/hide/foreground events can come in bursts: re-enumerate at most 10 times a second.
            if (now - _lastRefresh >= 0.1) { _lastRefresh = now; T.Refresh(W.Monitors); }
            else ArmTimer(0.1 - (now - _lastRefresh));
        }
        if (T.ToastProbePending(out double probeAt))
        {
            if (now >= probeAt) T.ProbeToasts(now);
            else ArmTimer(probeAt - now);
        }
        if (T.SpansDirty) { T.ComputeSpans(); W.Kick(0.15); }

        int fps = W.DesiredFps(MaxFps());
        if (fps == 0) { IdleCursorGlance(); fps = W.DesiredFps(MaxFps()); }
        if (T.Dragging != 0 && fps == 0) fps = MaxFps(); // follow a drag smoothly
        if (fps == 0 && _needFinalFrame) { Frame(now); fps = W.DesiredFps(MaxFps()); }
        if (fps == 0) { ScheduleIdle(); return; }
        double interval = 1.0 / fps;
        if (now - _lastFrame >= interval * 0.9) { Frame(now); fps = Math.Max(1, W.DesiredFps(MaxFps())); interval = 1.0 / fps; }
        ArmTimer(Math.Max(0.001, _lastFrame + interval - Seconds));
    }

    static void ScheduleIdle()
    {
        // Nothing moving: no frames. Wake only for a scheduled sky event (twinkle/lightning) or real events.
        double next = W.NextEventIn();
        if (T.Dirty) next = Math.Min(next, 0.1);
        bool needPos = W.NeedsPositions();
        next = Math.Min(next, T.NextPollIn(Seconds, needPos));
        if (W.HasInteractive()) next = Math.Min(next, 0.125); // glance at the cursor 8 times a second, nothing more
        if (T.ToastProbePending(out double probeAt)) next = Math.Min(next, Math.Max(0.01, probeAt - Seconds));
        if (next < 1e8) ArmTimer(Math.Max(0.05, next));
        else DisarmTimer();
    }

    static void Frame(double now)
    {
        double gap = now - _lastFrame;
        if (gap > 0.25) W.ResetCursor(); // coming back from idle: don't brush along a stale cursor path
        float dt = (float)Math.Clamp(gap, 0.0005, 0.1);
        _lastFrame = now;
        long p0 = Stopwatch.GetTimestamp();
        W.Step(dt);
        W.ProfStep += Stopwatch.GetTimestamp() - p0;
        try
        {
            foreach (var m in W.Monitors) if (m.Ov is { Visible: true }) W.Render(_gpu!, m);
            _needFinalFrame = false;
            _deviceFailures = 0;
        }
        catch (Exception e)
        {
            Log.Write($"render failed ({e.GetType().Name}: {e.Message}); recreating the GPU device");
            if (++_deviceFailures > 5) { Log.Write("too many GPU failures; pausing"); S.Paused = true; UpdatePauseState(); return; }
            BuildGraphics();
        }
        _frames++;
        W.ProfFrames++;
    }
    static double _timerDueAt;
    static POINT _lastGlance;

    /// <summary>Idle: is the cursor moving near something it could brush? (Replaces the cursor WinEvent.)</summary>
    static void IdleCursorGlance()
    {
        if (!W.HasInteractive()) return;
        POINT p; Native.GetCursorPos(&p);
        if (p.X == _lastGlance.X && p.Y == _lastGlance.Y) return;
        _lastGlance = p;
        if (W.CursorNearSomething(p.X, p.Y)) W.Kick(0.5);
    }

    static void ArmTimer(double seconds)
    {
        _timerDueAt = Seconds + seconds;
        long due = -(long)(seconds * 10_000_000);
        Native.SetWaitableTimer(_timer, &due, 0, 0, 0, 0);
        _timerArmed = true;
    }

    static void DisarmTimer()
    {
        if (_timerArmed) Native.CancelWaitableTimer(_timer);
        _timerArmed = false;
    }

    static void OnCursorEvent()
    {
        if (Paused || _timerArmed) return;
        POINT p; Native.GetCursorPos(&p);
        if (W.CursorNearSomething(p.X, p.Y)) { W.Kick(0.5); }
    }

    // ------------------------------------------------------------------ graphics / monitors

    static readonly List<(nint h, RECT r)> _mons = new();

    [UnmanagedCallersOnly]
    static int MonProc(nint mon, nint hdc, RECT* r, nint l) { _mons.Add((mon, *r)); return 1; }

    static void BuildGraphics()
    {
        foreach (var m in W.Monitors) { if (m.Ov != null) T.RemoveOwn(m.Ov.Hwnd); m.Ov?.Dispose(); m.DisposeFrost(); }
        W.Monitors.Clear();
        _flyout?.Dispose(); _flyout = null; // it holds resources of the old device
        _gpu?.Dispose();
        _gpu = null;
        try { _gpu = new Gpu(); }
        catch (Exception e) { Log.Write("GPU init failed: " + e); return; }
        Log.Write($"GPU ready, sprite batch: {_gpu.SpriteBatchOk}");
        _mons.Clear();
        Native.EnumDisplayMonitors(0, null, &MonProc, 0);
        foreach (var (h, r) in _mons)
        {
            var m = new Monitor { Handle = h, Bounds = r, Scale = Native.DpiScaleForMonitor(h) };
            try
            {
                m.Ov = new Overlay(_gpu, r, m.Scale, S.HideFromCapture);
                T.AddOwn(m.Ov.Hwnd);
            }
            catch (Exception e) { Log.Write($"overlay for {r} failed: {e.Message}"); continue; }
            W.Monitors.Add(m);
            Log.Write($"monitor {r} scale {m.Scale:0.00}");
        }
        _needFinalFrame = true;
        T.Dirty = true;
        UpdatePauseState();
    }

    static void RaiseOverlays()
    {
        foreach (var m in W.Monitors) m.Ov?.RaiseTopmost();
        _flyout?.RaiseAbove(); // the menu stays above the weather
    }

    // ------------------------------------------------------------------ pausing

    static bool Paused => S.Paused || S.Effect == "Off" || _pFull || _pLocked || _pDisplayOff || (_pSaver && S.RestOnBatterySaver);

    static void UpdatePauseState()
    {
        bool p = Paused;
        foreach (var m in W.Monitors)
        {
            if (m.Ov == null) continue;
            if (p) m.Ov.Hide(); else m.Ov.Show();
        }
        if (p)
        {
            W.RestoreWindows();
            DisarmTimer();
            T.SetQuiet(true);
            Native.KillTimer(_msgHwnd, TIMER_TOPMOST);
            Native.KillTimer(_msgHwnd, TIMER_WEATHER); // paused: no timers at all
        }
        else
        {
            T.SetQuiet(false);
            Native.SetTimer(_msgHwnd, TIMER_TOPMOST, 5000, 0);
            Native.SetTimer(_msgHwnd, TIMER_WEATHER, 15 * 60 * 1000, 0);
            _needFinalFrame = true;
            _lastFrame = Clock.Elapsed.TotalSeconds;
            if (Clock.Elapsed.TotalSeconds - _lastWeatherFetch > 15 * 60) StartWeatherFetch();
            Native.PostMessageW(_msgHwnd, WM_KICK, 0, 0);
        }
        UpdateTrayTip();
    }

    static void CheckFullscreen()
    {
        int q = 0;
        bool full = false;
        string why = "";
        if (Native.SHQueryUserNotificationState(&q) == 0)
            full = q == 2 /*QUNS_BUSY*/ || q == 3 /*QUNS_RUNNING_D3D_FULL_SCREEN*/ || q == 4 /*QUNS_PRESENTATION_MODE*/;
        if (full) why = $"QUNS={q}";
        if (!full)
        {
            // Borderless full-screen games and videos: the foreground window covers its whole monitor.
            nint fg = Native.GetForegroundWindow();
            if (fg != 0 && fg != Native.GetShellWindow() && !Native.ClassIs(fg, "WorkerW") && !Native.ClassIs(fg, "Progman") &&
                !Native.ClassIs(fg, "Shell_TrayWnd") && !Native.ClassIs(fg, "XamlExplorerHostIslandWindow") &&
                !Native.ClassIs(fg, "Windows.UI.Core.CoreWindow") && !Native.ClassIs(fg, "ForegroundStaging") && !Native.ClassIs(fg, "MultitaskingViewFrame"))
            {
                RECT r; Native.GetWindowRect(fg, &r);
                var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
                if (Native.GetMonitorInfoW(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST), &mi) != 0)
                {
                    var mr = mi.rcMonitor;
                    long style = Native.GetWindowLongPtrW(fg, Native.GWL_STYLE);
                    bool noCaption = (style & Native.WS_CAPTION) != Native.WS_CAPTION;
                    full = noCaption && Native.IsZoomed(fg) == 0 && r.Left <= mr.Left && r.Top <= mr.Top && r.Right >= mr.Right && r.Bottom >= mr.Bottom;
                    if (full) why = $"borderless {Native.ClassName(fg)} '{Native.WindowText(fg)}' {r}";
                }
            }
        }
        if (full != _pFull)
        {
            _pFull = full;
            Log.Write(full ? $"full-screen app in front ({why}): pausing" : "full-screen app gone: resuming");
            UpdatePauseState();
        }
    }

    // ------------------------------------------------------------------ host window, tray, menus

    static void RegisterClasses()
    {
        var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &HostProc, hInstance = HInstance };
        fixed (char* n = ClassMsg) { wc.lpszClassName = n; Native.RegisterClassExW(&wc); }
        var oc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &OverlayProc, hInstance = HInstance };
        fixed (char* n = "WindowsWeatherOverlay") { oc.lpszClassName = n; Native.RegisterClassExW(&oc); }
        var cc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &CityProc, hInstance = HInstance, hbrBackground = 16 /*COLOR_BTNFACE+1*/ };
        cc.hCursor = Native.LoadCursorW(0, 32512);
        fixed (char* n = "WindowsWeatherCity") { cc.lpszClassName = n; Native.RegisterClassExW(&cc); }
        var fc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &FlyoutProc, hInstance = HInstance };
        fc.hCursor = Native.LoadCursorW(0, 32512);
        fixed (char* n = "WindowsWeatherFlyout") { fc.lpszClassName = n; Native.RegisterClassExW(&fc); }
    }

    [UnmanagedCallersOnly]
    static nint FlyoutProc(nint h, uint m, nuint w, nint l)
    {
        try { if (Flyout.Instance != null) return Flyout.Instance.Proc(h, m, w, l); }
        catch (Exception e) { Log.Write("flyout: " + e); }
        return Native.DefWindowProcW(h, m, w, l);
    }

    // ------------------------------------------------------------------ what the flyout reads and does

    static Flyout? _flyout;
    static double _flyoutClosedAt = -1;
    internal static Settings CurrentSettings => S;
    internal static CachedState CurrentState => St;
    internal static string[] EffectNames => Effects;
    internal static void TrackOwn(nint h) => T.AddOwn(h);
    internal static void FlyoutClosedNow() => _flyoutClosedAt = Seconds;

    internal static string TempText()
    {
        var w = St.Weather;
        if (w == null) return "";
        bool useF = St.Country is "" or "US" or "LR" or "MM" or "BS" or "KY" or "PW" or "FM" or "MH";
        return useF ? $"{Math.Round(w.TempF)}°F" : $"{Math.Round((w.TempF - 32) * 5 / 9)}°C";
    }

    internal static string StatusText()
    {
        if (S.Paused) return "Paused";
        if (!S.Online && S.Effect == "Live") return "Online weather is off: pick an effect";
        if (S.Effect == "Off") return "Turned off";
        if (_pFull) return "Resting while a full-screen app is open";
        if (_pSaver && S.RestOnBatterySaver) return "Resting on battery saver";
        if (S.Effect != "Live") return $"Showing {S.Effect.ToLowerInvariant()}, not the real weather";
        var w = St.Weather;
        if (w == null) return "Live weather";
        int min = (int)Math.Max(0, (DateTime.UtcNow - w.At).TotalMinutes);
        return min < 1 ? "Live weather · updated just now" : $"Live weather · updated {min} min ago";
    }

    /// <summary>The small print under the header: the extra things the weather data says.</summary>
    internal static string DetailsText()
    {
        var w = St.Weather;
        var parts = new List<string>();
        if (w != null)
        {
            if (w.Humidity >= 0) parts.Add($"Humidity {w.Humidity:0}%");
            if (w.WindGust > 0) parts.Add($"Gusts {Math.Round(w.WindGust * 0.621)} mph");
            if (w.Aqi >= 0) parts.Add($"AQI {w.Aqi:0}");
            if (w.Kp >= 0) parts.Add($"Kp {w.Kp:0.#}");
        }
        if (W.MoonPhase >= 0) parts.Add(WeatherService.MoonName(W.MoonPhase));
        return string.Join("  ·  ", parts);
    }

    internal static string ConditionEmoji()
    {
        var w = St.Weather;
        if (w == null) return "🌤️";
        return w.Code switch
        {
            0 or 1 => w.IsDay ? "☀️" : "🌙",
            2 => w.IsDay ? "⛅" : "☁️",
            3 => "☁️",
            45 or 48 => "🌫️",
            >= 51 and <= 67 or >= 80 and <= 82 => "🌧️",
            >= 71 and <= 77 or 85 or 86 => "🌨️",
            >= 95 => "⛈️",
            _ => "🌤️",
        };
    }

    // ------------------------------------------------------------------ what the weather costs (shown in the flyout)

    internal static float StatCpu, StatMemMb, StatFps;
    static double _statT = -1, _statCpuMs;
    static long _statFrames;

    /// <summary>Own CPU time, working set and frame rate, sampled on the 5 s timer the app already runs.</summary>
    static void SampleStats()
    {
        double now = Seconds;
        double cpuMs;
        float mem;
        using (var p = Process.GetCurrentProcess()) { cpuMs = p.TotalProcessorTime.TotalMilliseconds; mem = p.WorkingSet64 / (1024f * 1024f); }
        // Report what the weather costs, not the open menu: keep the figures from before the panel opened.
        bool menuOpen = _flyout is { Visible: true };
        if (menuOpen && _statT > 0) { _statT = now; _statCpuMs = cpuMs; _statFrames = _frames; return; }
        StatMemMb = mem;
        if (_statT > 0 && now - _statT > 0.5)
        {
            double wall = (now - _statT) * 1000;
            StatCpu = (float)((cpuMs - _statCpuMs) / wall * 100 / Environment.ProcessorCount); // % of the whole laptop
            StatFps = (float)((_frames - _statFrames) / (now - _statT));
        }
        _statT = now; _statCpuMs = cpuMs; _statFrames = _frames;
        _flyout?.Refresh(); // keep the open panel's numbers current
    }

    internal static string StatsLine() =>
        Paused ? "Paused: no frames, no timers" : _statT < 0 ? "Measuring…" : "";

    // ------------------------------------------------------------------ time-of-day preview (the dial's knob)

    internal static DateTime? PreviewTime { get; private set; }

    /// <summary>Show the sky at another time today (sun, sunset colours, moon, day or night); null = live again.</summary>
    internal static void SetPreview(DateTime? local)
    {
        if (PreviewTime == local) return;
        PreviewTime = local;
        ApplyWeather(false);
        W.Kick(0.5);
        Native.PostMessageW(_msgHwnd, WM_KICK, 0, 0);
    }

    /// <summary>What the dial's amount slider controls: whatever is falling now (else the clouds).</summary>
    internal static string AmountTargetName() =>
        W.Target.Snow > 0.05f ? "Snow" : W.Target.Rain > 0.05f || W.Target.Storm > 0.05f || W.Target.Hail > 0.05f ? "Rain" :
        W.Target.Leaves > 0.05f ? "Leaves" : "Clouds";

    internal static float AmountOf(string name) => name switch
    {
        "Snow" => S.SnowAmount, "Rain" => S.RainAmount, "Leaves" => S.LeafAmount, "Clouds" => S.CloudAmount, _ => 1,
    };

    static void ApplyAmounts()
    {
        W.SnowAmt = Math.Clamp(S.SnowAmount, 0, 2); W.RainAmt = Math.Clamp(S.RainAmount, 0, 2);
        W.LeafAmt = Math.Clamp(S.LeafAmount, 0, 2); W.CloudAmt = Math.Clamp(S.CloudAmount, 0, 2);
    }

    internal static void FlyoutCommand(string kind, string value)
    {
        switch (kind)
        {
            case "effect": { int i = Array.IndexOf(Effects, value); if (i >= 0) OnCommand(Cmd.EffLive + (uint)i); break; }
            case "amount":
            {
                // "Snow=1.25": live while dragging; saved when the drag ends.
                int eq = value.IndexOf('=');
                if (eq > 0 && float.TryParse(value[(eq + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                {
                    v = Math.Clamp(v, 0, 2);
                    switch (value[..eq]) { case "Snow": S.SnowAmount = v; break; case "Rain": S.RainAmount = v; break; case "Leaves": S.LeafAmount = v; break; case "Clouds": S.CloudAmount = v; break; }
                    ApplyAmounts();
                    W.Kick(0.5);
                    Native.PostMessageW(_msgHwnd, WM_KICK, 0, 0);
                }
                break;
            }
            case "amount-done": Store.Save(S); break;
            case "pause": OnCommand(Cmd.Pause); break;
            case "fps": OnCommand(value == "60" ? Cmd.Fps60 : Cmd.Fps30); break;
            case "startup": OnCommand(Cmd.Startup); break;
            case "saver": OnCommand(Cmd.Saver); break;
            case "battery": S.LighterOnBattery = !S.LighterOnBattery; Store.Save(S); W.Kick(0.2); break;
            case "online":
                S.Online = !S.Online; Store.Save(S);
                Log.Write(S.Online ? "online weather on" : "online weather off: no network lookups");
                if (S.Online) StartWeatherFetch(force: true);
                ApplyWeather(false);
                break;
            case "privacy":
                try { Process.Start(new ProcessStartInfo(PrivacyUrl) { UseShellExecute = true }); } catch (Exception e) { Log.Write("privacy link: " + e.Message); }
                break;
            case "capture": OnCommand(Cmd.Capture); break;
            case "clear": OnCommand(Cmd.ClearSnow); break;
            case "refresh": OnCommand(Cmd.Refresh); break;
            case "quit": OnCommand(Cmd.Quit); break;
            case "city":
                S.City = value; St.LocCity = ""; St.LocSource = ""; St.LocAt = default;
                Store.Save(S);
                StartWeatherFetch(force: true);
                break;
        }
    }

    static void ToggleFlyout()
    {
        if (_gpu == null) return;
        // A click on the tray icon first deactivates (closes) an open flyout; don't reopen it on the same click.
        if (_flyout is { Visible: false } && Seconds - _flyoutClosedAt < 0.35) return;
        _flyout ??= new Flyout(_gpu);
        _flyout.Toggle();
    }

    static void CreateHostWindow()
    {
        // A hidden top-level window (not message-only: it must receive TaskbarCreated and power broadcasts).
        fixed (char* c = ClassMsg)
        fixed (char* t = "Windows Weather")
            _msgHwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, c, t, Native.WS_POPUP, 0, 0, 0, 0, 0, 0, HInstance, 0);
        T.AddOwn(_msgHwnd);
        fixed (char* m = "TaskbarCreated") _taskbarCreatedMsg = Native.RegisterWindowMessageW(m);
        Native.WTSRegisterSessionNotification(_msgHwnd, 0);
        var g1 = Native.GUID_CONSOLE_DISPLAY_STATE; Native.RegisterPowerSettingNotification(_msgHwnd, &g1, 0);
        var g2 = Native.GUID_POWER_SAVING_STATUS; Native.RegisterPowerSettingNotification(_msgHwnd, &g2, 0);
        // Registering as an app bar (without taking any screen space) makes the shell tell us when a
        // full-screen app opens or closes (ABN_FULLSCREENAPP): no polling needed to notice either.
        var ab = new Native.APPBARDATA { cbSize = (uint)sizeof(Native.APPBARDATA), hWnd = _msgHwnd, uCallbackMessage = WM_APPBAR };
        Native.SHAppBarMessage(Native.ABM_NEW, &ab);
    }

    static void CreateTray()
    {
        if (_icon == 0)
        {
            nint small = 0;
            fixed (char* p = ExePath) Native.ExtractIconExW(p, 0, null, &small, 1);
            _icon = small;
        }
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _msgHwnd, uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP,
            uCallbackMessage = WM_TRAY, hIcon = _icon, uVersion = 4,
        };
        SetTip(ref d, TipText());
        Native.Shell_NotifyIconW(Native.NIM_ADD, &d);
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, &d);
    }

    static void SetTip(ref NOTIFYICONDATAW d, string s)
    {
        if (s.Length > 127) s = s[..127];
        fixed (char* tip = d.szTip) { for (int i = 0; i < s.Length; i++) tip[i] = s[i]; tip[s.Length] = '\0'; }
    }

    static string Conditions()
    {
        var w = St.Weather;
        string place = St.Place.Length > 0 ? St.Place : "locating…";
        if (w == null) return $"Waiting for the weather · {place}";
        bool useF = St.Country is "" or "US" or "LR" or "MM" or "BS" or "KY" or "PW" or "FM" or "MH";
        string temp = useF ? $"{Math.Round(w.TempF)}°F" : $"{Math.Round((w.TempF - 32) * 5 / 9)}°C";
        return $"{WeatherService.Describe(w.Code)}, {temp}, {place}";
    }

    static string TipText()
    {
        string state = S.Paused ? " (paused)" : S.Effect == "Off" ? " (off)" : _pFull ? " (resting: full-screen app)" :
            _pSaver && S.RestOnBatterySaver ? " (resting: battery saver)" : _pLocked || _pDisplayOff ? " (resting)" :
            S.Effect != "Live" ? $" (showing {S.Effect})" : "";
        return "Windows Weather" + state + "\n" + Conditions();
    }

    static void UpdateTrayTip()
    {
        if (_msgHwnd == 0) return;
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _msgHwnd, uID = 1, uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP };
        SetTip(ref d, TipText());
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, &d);
    }

    static void RemoveTray()
    {
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _msgHwnd, uID = 1 };
        Native.Shell_NotifyIconW(Native.NIM_DELETE, &d);
    }

    enum Cmd : uint
    {
        Pause = 100, EffLive = 110, EffSnow, EffRain, EffClouds, EffSun, EffNight, EffStorm, EffOff,
        IntLight = 130, IntMed, IntHeavy, Fps30 = 140, Fps60, LocAuto = 150, LocCity, Capture = 160, Startup, ClearSnow, Refresh, Saver, Quit = 199,
    }

    static readonly string[] Effects = { "Live", "Snow", "Rain", "Clouds", "Fog", "Sun", "Sunset", "Leaves", "Night", "Storm", "Hail", "Tornado", "Frost", "Aurora", "Off" };

    static void Append(nint menu, uint flags, Cmd id, string text) { fixed (char* t = text) Native.AppendMenuW(menu, flags, (nuint)id, t); }
    static void AppendSub(nint menu, nint sub, string text) { fixed (char* t = text) Native.AppendMenuW(menu, Native.MF_POPUP, (nuint)sub, t); }

    static void ShowMenu()
    {
        nint m = Native.CreatePopupMenu();
        Append(m, Native.MF_GRAYED, 0, Conditions());
        Native.AppendMenuW(m, Native.MF_SEPARATOR, 0, null);
        Append(m, 0, Cmd.Pause, S.Paused ? "Resume" : "Pause");

        nint eff = Native.CreatePopupMenu();
        for (int i = 0; i < Effects.Length; i++)
        {
            string label = Effects[i] switch { "Live" => "Live (the weather outside)", "Clouds" => "Clouds (haze)", "Sun" => "Sun (glow)", "Night" => "Clear night", var e => e };
            Append(eff, Native.MFT_RADIOCHECK | (S.Effect == Effects[i] ? Native.MF_CHECKED : 0), Cmd.EffLive + (uint)i, label);
        }
        AppendSub(m, eff, "Effect");

        nint inten = Native.CreatePopupMenu();
        Append(inten, Native.MFT_RADIOCHECK | (S.Intensity == "Light" ? Native.MF_CHECKED : 0), Cmd.IntLight, "Light");
        Append(inten, Native.MFT_RADIOCHECK | (S.Intensity == "Medium" ? Native.MF_CHECKED : 0), Cmd.IntMed, "Medium");
        Append(inten, Native.MFT_RADIOCHECK | (S.Intensity == "Heavy" ? Native.MF_CHECKED : 0), Cmd.IntHeavy, "Heavy");
        AppendSub(m, inten, "Intensity");

        nint loc = Native.CreatePopupMenu();
        string autoLabel = S.City.Length == 0 ? $"Automatic ({(St.Place.Length > 0 ? St.Place : "locating")})" : "Automatic";
        Append(loc, Native.MFT_RADIOCHECK | (S.City.Length == 0 ? Native.MF_CHECKED : 0), Cmd.LocAuto, autoLabel);
        Append(loc, Native.MFT_RADIOCHECK | (S.City.Length > 0 ? Native.MF_CHECKED : 0), Cmd.LocCity, S.City.Length > 0 ? $"City: {S.City}…" : "Set a city…");
        AppendSub(m, loc, "Location");

        nint fps = Native.CreatePopupMenu();
        Append(fps, Native.MFT_RADIOCHECK | (S.Fps != 60 ? Native.MF_CHECKED : 0), Cmd.Fps30, "30 fps (lightest)");
        Append(fps, Native.MFT_RADIOCHECK | (S.Fps == 60 ? Native.MF_CHECKED : 0), Cmd.Fps60, "60 fps (smoother)");
        AppendSub(m, fps, "Frame rate");

        Native.AppendMenuW(m, Native.MF_SEPARATOR, 0, null);
        Append(m, S.HideFromCapture ? Native.MF_CHECKED : 0, Cmd.Capture, "Hide from screen capture");
        Append(m, S.RestOnBatterySaver ? Native.MF_CHECKED : 0, Cmd.Saver, "Rest on battery saver");
        Append(m, S.StartWithWindows ? Native.MF_CHECKED : 0, Cmd.Startup, "Start with Windows");
        Append(m, 0, Cmd.ClearSnow, "Clear all snow");
        Append(m, 0, Cmd.Refresh, "Refresh weather now");
        Native.AppendMenuW(m, Native.MF_SEPARATOR, 0, null);
        Append(m, 0, Cmd.Quit, "Quit");

        POINT p; Native.GetCursorPos(&p);
        Native.SetForegroundWindow(_msgHwnd);
        uint cmd = (uint)Native.TrackPopupMenuEx(m, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_BOTTOMALIGN, p.X, p.Y, _msgHwnd, 0);
        Native.PostMessageW(_msgHwnd, Native.WM_NULL, 0, 0);
        Native.DestroyMenu(m); // destroys submenus too
        if (cmd != 0) OnCommand((Cmd)cmd);
    }

    static void OnCommand(Cmd c)
    {
        switch (c)
        {
            case Cmd.Pause: S.Paused = !S.Paused; break;
            case >= Cmd.EffLive and <= (Cmd)123:
                S.Effect = Effects[c - Cmd.EffLive];
                ApplyWeather(initial: false);
                break;
            case Cmd.IntLight: S.SnowAmount = S.RainAmount = S.LeafAmount = 0.6f; ApplyWeather(false); break;
            case Cmd.IntMed: S.SnowAmount = S.RainAmount = S.LeafAmount = 1f; ApplyWeather(false); break;
            case Cmd.IntHeavy: S.SnowAmount = S.RainAmount = S.LeafAmount = 1.6f; ApplyWeather(false); break;
            case Cmd.Fps30: S.Fps = 30; break;
            case Cmd.Fps60: S.Fps = 60; break;
            case Cmd.LocAuto: S.City = ""; St.LocSource = ""; St.LocAt = default; StartWeatherFetch(force: true); break;
            case Cmd.LocCity: ShowCityBox(); break;
            case Cmd.Capture:
                S.HideFromCapture = !S.HideFromCapture;
                foreach (var m in W.Monitors) m.Ov?.SetCaptureExclusion(S.HideFromCapture);
                break;
            case Cmd.Startup: S.StartWithWindows = !S.StartWithWindows; EnsureRunKey(); break;
            case Cmd.ClearSnow: W.ClearAll(); break;
            case Cmd.Saver: S.RestOnBatterySaver = !S.RestOnBatterySaver; break;
            case Cmd.Refresh: StartWeatherFetch(force: true); break;
            case Cmd.Quit: _running = false; Native.PostQuitMessage(0); break;
        }
        Store.Save(S);
        UpdatePauseState();
        Native.PostMessageW(_msgHwnd, WM_KICK, 0, 0);
    }

    [UnmanagedCallersOnly]
    static nint HostProc(nint h, uint m, nuint w, nint l)
    {
        try
        {
            if (m == _taskbarCreatedMsg && m != 0)
            {
                Log.Write("Explorer restarted: re-adding the tray icon and re-finding the taskbar");
                CreateTray(); T.Dirty = true; RaiseOverlays();
                return 0;
            }
            switch (m)
            {
                case WM_TRAY:
                    uint ev = (uint)(l & 0xFFFF);
                    if (ev == Native.WM_CONTEXTMENU || ev == Native.WM_LBUTTONUP) ToggleFlyout();
                    return 0;
                case WM_WEATHER:
                    _fetching = false;
                    ApplyWeather(false);
                    UpdateTrayTip();
                    _flyout?.Refresh();
                    return 0;
                case WM_KICK:
                    W.Kick(0.3);
                    return 0;
                case WM_APPBAR:
                    if ((uint)w == Native.ABN_FULLSCREENAPP) CheckFullscreen();
                    return 0;
                case WM_SETPAUSE:
                    if ((w != 0) != S.Paused) OnCommand(Cmd.Pause);
                    return 0;
                case WM_MENU:
                    if (w == 0) ToggleFlyout();
                    else { if (_gpu == null) return 0; _flyout ??= new Flyout(_gpu); _flyout.ShowView((int)w - 1); }
                    return 0;
                case WM_SETEFFECT:
                    if ((int)w < Effects.Length) OnCommand(Cmd.EffLive + (uint)w);
                    return 0;
                case WM_DUMP:
                    if (w == 7) { W.StrikeNow(); return 0; }
                    if (w == 8) { W.GrabOnlyTitle = null; W.TornadoNow(); return 0; }
                    if (w == 9) { W.GrabOnlyTitle = "WWTest"; W.TornadoNow(); return 0; }
                    Log.Write($"DUMP paused={Paused} (user={S.Paused} full={_pFull} locked={_pLocked} displayOff={_pDisplayOff} saver={_pSaver}) effect={S.Effect} frames={_frames} fps={W.DesiredFps(MaxFps())} events={Tracker.EventCount} cursorEvents={Tracker.CursorEvents}");
                    Log.Write("DUMP " + W.Describe());
                    Log.Write("DUMP prof " + W.ProfileText());
                    Log.Write("DUMP events " + Tracker.EventBreakdown());
                    foreach (var t in T.Z)
                        if (t.Eligible || t.PileTotal > 0)
                            Log.Write($"DUMP   {(t.IsTaskbar ? "TASKBAR" : Native.ClassName(t.Hwnd))} '{Native.WindowText(t.Hwnd)}' {t.R} spans={t.Spans.Count} [{string.Join(" ", t.Spans.Select(s => $"{s.a}-{s.b}"))}] pile={t.PileTotal:0} max={(t.H.Length > 0 ? t.H.Max() : 0):0.0} beads={t.Beads.Count}");
                    return 0;
                case Native.WM_TIMER:
                    if (w == TIMER_WEATHER && !Paused) StartWeatherFetch();
                    if (w == TIMER_TOPMOST) { RaiseOverlays(); CheckFullscreen(); UpdateSun(); SampleStats(); }
                    if (w == TIMER_FIRSTRUN)
                    {
                        // First run: open the menu at its settings page, where the privacy notice and the online switch are.
                        Native.KillTimer(_msgHwnd, TIMER_FIRSTRUN);
                        if (_gpu != null) { _flyout ??= new Flyout(_gpu); _flyout.ShowView(2); }
                    }
                    return 0;
                case Native.WM_DISPLAYCHANGE:
                    Log.Write("display change: rebuilding overlays");
                    BuildGraphics();
                    return 0;
                case Native.WM_SETTINGCHANGE:
                    T.Dirty = true;
                    break;
                case Native.WM_WTSSESSION_CHANGE:
                    if ((uint)w == Native.WTS_SESSION_LOCK) { _pLocked = true; UpdatePauseState(); }
                    else if ((uint)w == Native.WTS_SESSION_UNLOCK) { _pLocked = false; UpdatePauseState(); }
                    return 0;
                case Native.WM_POWERBROADCAST:
                    if ((uint)w == 0x000A /*PBT_APMPOWERSTATUSCHANGE: plugged in or unplugged*/)
                    {
                        bool was = _onBattery; ReadAcLine();
                        if (was != _onBattery) { Log.Write(_onBattery ? "on battery" : "plugged in"); _flyout?.Refresh(); W.Kick(0.2); }
                        return 1;
                    }
                    if ((uint)w == Native.PBT_POWERSETTINGCHANGE && l != 0)
                    {
                        var ps = (POWERBROADCAST_SETTING*)l;
                        if (ps->PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE) _pDisplayOff = ps->Data == 0;
                        else if (ps->PowerSetting == Native.GUID_POWER_SAVING_STATUS) _pSaver = ps->Data != 0;
                        Log.Write($"power: displayOff={_pDisplayOff} saver={_pSaver}");
                        UpdatePauseState();
                    }
                    return 1;
                case Native.WM_CLOSE:
                    _running = false; Native.PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception e) { Log.Write("host: " + e); }
        return Native.DefWindowProcW(h, m, w, l);
    }

    [UnmanagedCallersOnly]
    static nint OverlayProc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case Native.WM_NCHITTEST: return Native.HTTRANSPARENT;
            case Native.WM_MOUSEACTIVATE: return Native.MA_NOACTIVATE;
            case Native.WM_ERASEBKGND: return 1;
            case Native.WM_DPICHANGED:
                Native.PostMessageW(_msgHwnd, Native.WM_DISPLAYCHANGE, 0, 0);
                return 0;
        }
        return Native.DefWindowProcW(h, m, w, l);
    }

    // ------------------------------------------------------------------ city box

    static void ShowCityBox()
    {
        if (_cityHwnd != 0) { Native.SetForegroundWindow(_cityHwnd); return; }
        uint dpi = Native.GetDpiForSystem();
        int D(int v) => v * (int)dpi / 96;
        int w = D(360), h = D(150);
        int sx = Native.GetSystemMetrics(0), sy = Native.GetSystemMetrics(1);
        fixed (char* cls = "WindowsWeatherCity")
        fixed (char* title = "Windows Weather: location")
            _cityHwnd = Native.CreateWindowExW(Native.WS_EX_DLGMODALFRAME | Native.WS_EX_TOPMOST, cls, title,
                Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_VISIBLE, (sx - w) / 2, (sy - h) / 2, w, h, 0, 0, HInstance, 0);
        T.AddOwn(_cityHwnd);
        if (_font == 0) fixed (char* f = "Segoe UI") _font = Native.CreateFontW(-D(12) * 9 / 9, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, f);
        nint label, ok, auto;
        fixed (char* s = "STATIC") fixed (char* t = "City (for example: Denver, CO). Leave empty for automatic.")
            label = Native.CreateWindowExW(0, s, t, Native.WS_CHILD | Native.WS_VISIBLE, D(12), D(10), D(330), D(20), _cityHwnd, 0, HInstance, 0);
        fixed (char* e = "EDIT") fixed (char* t = S.City)
            _cityEdit = Native.CreateWindowExW(Native.WS_EX_CLIENTEDGE, e, t, Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP | (int)Native.ES_AUTOHSCROLL,
                D(12), D(34), D(330), D(24), _cityHwnd, 100, HInstance, 0);
        fixed (char* b = "BUTTON") fixed (char* t = "OK")
            ok = Native.CreateWindowExW(0, b, t, Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP | (int)Native.BS_DEFPUSHBUTTON,
                D(172), D(70), D(80), D(28), _cityHwnd, Native.IDOK, HInstance, 0);
        fixed (char* b = "BUTTON") fixed (char* t = "Automatic")
            auto = Native.CreateWindowExW(0, b, t, Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP,
                D(262), D(70), D(80), D(28), _cityHwnd, 101, HInstance, 0);
        foreach (var c in new[] { label, _cityEdit, ok, auto }) Native.SendMessageW(c, Native.WM_SETFONT, (nuint)_font, 1);
        Native.SetForegroundWindow(_cityHwnd);
        Native.SetFocus(_cityEdit);
        Native.SendMessageW(_cityEdit, 0x00B1 /*EM_SETSEL*/, 0, -1);
    }

    [UnmanagedCallersOnly]
    static nint CityProc(nint h, uint m, nuint w, nint l)
    {
        try
        {
            if (m == Native.WM_COMMAND)
            {
                int id = (int)(w & 0xFFFF);
                if (id == Native.IDOK || id == 101)
                {
                    string city = id == 101 ? "" : Native.GetText(_cityEdit).Trim();
                    Native.DestroyWindow(h);
                    S.City = city; St.LocCity = ""; St.LocSource = ""; St.LocAt = default;
                    Store.Save(S);
                    StartWeatherFetch(force: true);
                    return 0;
                }
                if (id == Native.IDCANCEL) { Native.DestroyWindow(h); return 0; }
            }
            else if (m == Native.WM_CLOSE) { Native.DestroyWindow(h); return 0; }
            else if (m == Native.WM_DESTROY) { T.RemoveOwn(h); _cityHwnd = 0; _cityEdit = 0; return 0; }
        }
        catch (Exception e) { Log.Write("city: " + e); }
        return Native.DefWindowProcW(h, m, w, l);
    }

    // ------------------------------------------------------------------ weather

    internal const string PrivacyUrl = "https://github.com/PandaPlayz2373/WindowsWeather/blob/main/PRIVACY.md";

    static void StartWeatherFetch(bool force = false)
    {
        if (!S.Online) return; // the user switched online weather off: no network at all
        if (_fetching) return;
        if (!force && Clock.Elapsed.TotalSeconds - _lastWeatherFetch < 60) return;
        _fetching = true;
        _lastWeatherFetch = Clock.Elapsed.TotalSeconds;
        var set = S; var st = St; nint host = _msgHwnd;
        Task.Run(() => WeatherService.Refresh(set, st, () => Native.PostMessageW(host, WM_WEATHER, 0, 0)));
    }

    static void ApplyWeather(bool initial)
    {
        var w = St.Weather;
        if (w != null && PreviewTime is DateTime pv && St.HasLoc)
        {
            // Previewing another time of day: same weather, but day or night as it would be then.
            var (_, pel) = WeatherService.Sun(St.Lat, St.Lon, pv.ToUniversalTime());
            w = new WeatherNow { Code = w.Code, TempF = w.TempF, Cloud = w.Cloud, Precip = w.Precip, IsDay = pel > 0, WindKmh = w.WindKmh,
                WindDir = w.WindDir, WindGust = w.WindGust, CloudLow = w.CloudLow, CloudMid = w.CloudMid, CloudHigh = w.CloudHigh,
                Visibility = w.Visibility, Humidity = w.Humidity, SnowfallCm = w.SnowfallCm, SnowDepthM = w.SnowDepthM, Aqi = w.Aqi, Kp = w.Kp, At = w.At };
        }
        bool isDay = w?.IsDay ?? true;
        Mix target;
        if (S.Effect == "Live")
            target = w != null ? WeatherService.MixFor(w, St.Lat, St.Lon) : new Mix { Snow = 0.4f }; // never had weather: light snow
        else
            target = WeatherService.MixForOverride(S.Effect, isDay);
        W.Target = target;
        W.IsDay = isDay;
        ApplyAmounts();
        W.WindX = w != null && S.Effect == "Live" ? WeatherService.WindX(w) : (S.Effect is "Snow" or "Rain" or "Storm" or "Tornado" ? 10 : 0);
        if (St.HasLoc)
        {
            var (az, el) = WeatherService.Sun(St.Lat, St.Lon, DateTime.UtcNow);
            W.SunX = (float)-Math.Sin(az * Math.PI / 180); // facing south: east on the left
            W.SunElev = (float)el;
        }
        else { W.SunX = 0.5f; W.SunElev = 30; }
        W.GustKmh = w != null && S.Effect is "Live" or "Storm" or "Leaves" or "Tornado" ? (float)Math.Max(w.WindGust, S.Effect == "Storm" ? 45 : S.Effect == "Tornado" ? 60 : S.Effect == "Leaves" ? 30 : 0) : 0;
        W.WindKmh = (float)(w?.WindKmh ?? 0);
        // Deep snow on the ground: taller piles (0 cm: normal, 30 cm or more: 1.7x).
        W.PileScale = 1 + (float)Math.Clamp((w?.SnowDepthM ?? 0) / 0.3, 0, 1) * 0.7f;
        W.ForceMoon = S.Effect is "Night" or "Aurora";
        W.FastFrost = S.Effect == "Frost";
        UpdateSun();
        if (initial) W.Cur = target;
        W.Kick(1);
        UpdateTrayTip();
    }

    /// <summary>Where the sun is right now (sunset colours change by the minute). "Sunset" previews it on the horizon.</summary>
    static void UpdateSun()
    {
        if (S.Effect == "Sunset") { W.SunElev = 1f; W.SunX = 0.85f; return; }
        if (!St.HasLoc) return;
        var when = PreviewTime?.ToUniversalTime() ?? DateTime.UtcNow;
        var (maz, malt, mph) = WeatherService.Moon(St.Lat, St.Lon, when);
        W.MoonAz = (float)maz; W.MoonAlt = (float)malt; W.MoonPhase = (float)mph;
        var (az, el) = WeatherService.Sun(St.Lat, St.Lon, when);
        W.SunX = (float)-Math.Sin(az * Math.PI / 180); // facing south: east on the left
        W.SunElev = (float)el;
    }

    // ------------------------------------------------------------------ start with Windows

    static void EnsureRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (k == null) return;
            // Wherever the exe lives (a downloaded copy too), except a developer build.
            bool devBuild = ExePath.Contains(@"\.tmp\", StringComparison.OrdinalIgnoreCase) || ExePath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
            if (S.StartWithWindows && !devBuild) k.SetValue("WindowsWeather", $"\"{ExePath}\"");
            else if (!S.StartWithWindows) k.DeleteValue("WindowsWeather", false);
        }
        catch (Exception e) { Log.Write("run key: " + e.Message); }
    }

    static void Shutdown()
    {
        Log.Write("quit");
        W.RestoreWindows();
        T.Unhook();
        var ab = new Native.APPBARDATA { cbSize = (uint)sizeof(Native.APPBARDATA), hWnd = _msgHwnd };
        Native.SHAppBarMessage(Native.ABM_REMOVE, &ab);
        RemoveTray();
        foreach (var m in W.Monitors) m.Ov?.Dispose();
        _gpu?.Dispose();
        Store.Save(St);
    }
}
