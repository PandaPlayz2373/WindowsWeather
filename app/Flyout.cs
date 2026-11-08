using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using DxgiAlpha = Vortice.DXGI.AlphaMode;

namespace WindowsWeather;

/// <summary>
/// The tray flyout ("a fluid modern UX and UI aesthetic menu", not a standard Win32 menu).
/// A Windows 11 style panel: system acrylic backdrop, rounded corners, light/dark theme and accent from the
/// system, Segoe UI Variable, animated chips / segments / switches, hover fades, slide-up open, inline city field.
/// Drawn with Direct2D + DirectWrite on the shared GPU device; frames are rendered only while something animates.
/// </summary>
public sealed unsafe partial class Flyout : IDisposable
{
    public static Flyout? Instance;
    const float W = 360, Pad = 16;

    readonly Gpu _gpu;
    nint _hwnd;
    IDXGISwapChain1? _swap;
    ID2D1Bitmap1? _target;
    IDCompositionTarget? _dt;
    IDCompositionVisual? _vis;
    ID2D1SolidColorBrush _brush;
    static IDWriteFactory? _dw;
    IDWriteTextFormat _title, _body, _bodyRight, _small, _smallCenter, _label, _icon, _iconCenter, _emoji, _emojiBig;
    float _s = 1, _h = 700;
    int _pxW, _pxH;
    bool _visible, _closing, _timer, _acrylic;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last, _openT, _closeT;
    bool _light;
    Color4 _accent, _accentText;

    // Interaction state
    enum Id { None, Outside, Arc, Amount, Gear, WeatherBtn, Now, Back, Chip0 = 100, Seg0 = 200, Slider0 = 500, Pause = 300, Fps60, Startup, Saver, Capture, Battery, Online, Location = 400, ClearSnow, Refresh, Privacy, Quit }
    readonly Dictionary<Id, Rect> _hit = new();
    readonly Dictionary<Id, float> _hover = new(), _anim = new();
    Id _hot, _pressed, _drag;
    static readonly string[] SliderNames = { "Snow", "Rain", "Leaves", "Clouds" };
    readonly Dictionary<Id, string> _sliderKeys = new(); // effect-picker sliders (Slider0 + 10 ..) -> setting key

    /// <summary>One 0-200% slider row: label, track, Windows 11 thumb, percentage. key = the setting it changes.</summary>
    void DrawSlider(Id id, Rect r, string key, string label)
    {
        _hit[id] = r;
        if (id >= Id.Slider0 + 10) _sliderKeys[id] = key;
        float v = App.AmountOf(key);              // 0..2
        Text(label, _body, new Rect(r.Left, r.Top, 84, r.Height), Fg(0.88f));
        var tr = SliderTrack(r);
        float cy = r.Top + r.Height / 2, x = tr.Left + tr.Width * (Math.Clamp(v, 0, 2) / 2f);
        Fill(new Rect(tr.Left, cy - 2, tr.Width, 4), 2, Fg(_light ? 0.18f : 0.22f));
        Fill(new Rect(tr.Left, cy - 2, x - tr.Left, 4), 2, Acc());
        // Windows 11 style thumb: a ring with an accent centre that grows on hover / while dragging.
        float hv = MathF.Max(H(id), _drag == id ? 1 : 0);
        _brush.Color = _light ? C(1, 1, 1, 1) : C(0.27f, 0.27f, 0.28f, 1);
        _gpu.Ctx.FillEllipse(new Ellipse(new Vector2(x, cy), 10, 10), _brush);
        _brush.Color = Fg(0.12f);
        _gpu.Ctx.DrawEllipse(new Ellipse(new Vector2(x, cy), 10, 10), _brush, 1);
        _brush.Color = Acc();
        float ir = 5 + 1.5f * hv - (_drag == id ? 1 : 0);
        _gpu.Ctx.FillEllipse(new Ellipse(new Vector2(x, cy), ir, ir), _brush);
        Text($"{MathF.Round(v * 100)}%", _bodyRight, new Rect(tr.Right + 8, r.Top, r.Right - tr.Right - 8, r.Height), Fg(0.6f));
    }

    static Rect SliderTrack(Rect row) => new(row.Left + 92, row.Top, row.Width - 92 - 52, row.Height);

    void DragTo(int px)
    {
        var row = _hit[_drag];
        var tr = SliderTrack(row);
        float v = Math.Clamp((px / _s - tr.Left) / tr.Width, 0, 1) * 2f;
        v = MathF.Round(v * 20) / 20f; // 5% steps
        string key = _sliderKeys.TryGetValue(_drag, out var k) ? k : SliderNames[_drag - Id.Slider0];
        App.FlyoutCommand("amount", $"{key}={v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Render();
    }
    bool _editing;
    string _edit = "";
    double _caretT;

    public bool Visible => _visible;

    public Flyout(Gpu gpu)
    {
        _gpu = gpu;
        Instance = this;
        _dw ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        string text = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        string disp = HasFont("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        string icons = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        _title = Fmt(disp, 20, FontWeight.SemiBold);
        _body = Fmt(text, 14, FontWeight.Normal);
        _bodyRight = Fmt(text, 13, FontWeight.Normal, TextAlignment.Trailing);
        _small = Fmt(text, 12, FontWeight.Normal);
        _smallCenter = Fmt(text, 11, FontWeight.Normal, TextAlignment.Center);
        _label = Fmt(text, 12, FontWeight.SemiBold);
        _icon = Fmt(icons, 16, FontWeight.Normal);
        _iconCenter = Fmt(icons, 20, FontWeight.Normal, TextAlignment.Center);
        _emoji = Fmt("Segoe UI Emoji", 22, FontWeight.Normal, TextAlignment.Center);
        _emojiBig = Fmt("Segoe UI Emoji", 34, FontWeight.Normal, TextAlignment.Center);
        _brush = gpu.Ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
    }

    static bool HasFont(string family)
    {
        try { using var c = _dw!.GetSystemFontCollection(false); return c.FindFamilyName(family, out _); }
        catch { return false; }
    }

    IDWriteTextFormat Fmt(string family, float size, FontWeight weight, TextAlignment align = TextAlignment.Leading)
    {
        var f = _dw!.CreateTextFormat(family, null, weight, FontStyle.Normal, FontStretch.Normal, size, "en-us");
        f.TextAlignment = align;
        f.ParagraphAlignment = ParagraphAlignment.Center;
        f.WordWrapping = WordWrapping.NoWrap;
        using var sign = _dw.CreateEllipsisTrimmingSign(f);
        f.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        return f;
    }

    // ------------------------------------------------------------------ show / hide

    public void Toggle()
    {
        if (_visible && !_closing) Close(); else Open();
    }

    public void Open()
    {
        ReadTheme();
        _view = View.Dial;
        _arcSky = null;
        PlaceWindow();
        Native.SetForegroundWindow(_hwnd);
        _visible = true; _closing = false; _editing = false;
        _openT = _clock.Elapsed.TotalSeconds;
        _hover.Clear();
        SyncAnimTargets(snap: true);
        Kick();
    }

    public void Refresh() { if (_visible) { SyncAnimTargets(snap: false); Kick(); } }

    /// <summary>Size and place the window for the current view, on the monitor with the cursor.</summary>
    void PlaceWindow()
    {
        POINT p; Native.GetCursorPos(&p);
        nint mon = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        Native.GetMonitorInfoW(mon, &mi);
        _s = Native.DpiScaleForMonitor(mon);
        float w = _view == View.Dial ? DialSize : W;
        _h = _view == View.Dial ? DialSize : _view == View.Effects ? EffectsLayout(false) : Layout(measureOnly: true);
        float avail = (mi.rcWork.Bottom - mi.rcWork.Top) - 24 * _s;
        if (_h * _s > avail) _s = avail / _h; // small screens: scale the panel down rather than cut it off
        int pw = (int)MathF.Ceiling(w * _s), ph = (int)MathF.Ceiling(_h * _s);
        // The dial sits flush in the corner, on the taskbar and against the screen edge, as in the sketch.
        int gap = _view == View.Dial ? 0 : (int)(12 * _s);
        int x = mi.rcWork.Right - pw - gap, y = mi.rcWork.Bottom - ph - gap;
        EnsureWindow(pw, ph);
        if (_view == View.Dial)
        {
            // A round window: clicks outside the quarter disc go to whatever is underneath.
            int rr = (int)MathF.Ceiling((DialR + 2) * _s);
            Native.SetWindowRgn(_hwnd, Native.CreateEllipticRgn(pw - rr, ph - rr, pw + rr, ph + rr), 1);
            int none = 1; Native.DwmSetWindowAttribute(_hwnd, 33, &none, 4);
            // No system backdrop for the dial: it fills the whole square; the dial draws its own round surface.
            int nob = 1; Native.DwmSetWindowAttribute(_hwnd, 38, &nob, 4);
        }
        else
        {
            Native.SetWindowRgn(_hwnd, 0, 1);
            int round = 2; Native.DwmSetWindowAttribute(_hwnd, 33, &round, 4);
            int acr = 3; Native.DwmSetWindowAttribute(_hwnd, 38, &acr, 4);
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, pw, ph, Native.SWP_SHOWWINDOW);
    }

    /// <summary>Keep the panel above the weather overlays (they re-assert topmost every few seconds).</summary>
    public void RaiseAbove()
    {
        if (_visible) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void Close()
    {
        if (!_visible || _closing) return;
        App.FlyoutClosedNow();
        App.SetPreview(null); // a time preview ends with the menu
        _closing = true; _editing = false;
        _closeT = _clock.Elapsed.TotalSeconds;
        Kick();
    }

    void FinishClose()
    {
        _visible = false; _closing = false;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        StopTimer();
        // The emoji font and DirectWrite caches are only needed while the panel is open.
        Native.K32EmptyWorkingSet(Native.GetCurrentProcess());
    }

    void EnsureWindow(int w, int h)
    {
        if (_hwnd == 0)
        {
            fixed (char* cls = "WindowsWeatherFlyout")
            fixed (char* title = "Windows Weather")
                _hwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOREDIRECTIONBITMAP,
                    cls, title, Native.WS_POPUP, 0, 0, w, h, 0, 0, App.HInstance, 0);
            // System backdrop (acrylic for transient windows), rounded corners, dark frame when in dark mode.
            int round = 2; Native.DwmSetWindowAttribute(_hwnd, 33, &round, 4);
            int dark = _light ? 0 : 1; Native.DwmSetWindowAttribute(_hwnd, 20, &dark, 4);
            int backdrop = 3; _acrylic = Native.DwmSetWindowAttribute(_hwnd, 38, &backdrop, 4) == 0;
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(_hwnd, &m);
            App.TrackOwn(_hwnd);
        }
        else { int dark = _light ? 0 : 1; Native.DwmSetWindowAttribute(_hwnd, 20, &dark, 4); }
        if (_swap == null || w != _pxW || h != _pxH)
        {
            _target?.Dispose(); _vis?.Dispose(); _dt?.Dispose(); _swap?.Dispose();
            _pxW = w; _pxH = h;
            var desc = new SwapChainDescription1
            {
                Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = DxgiAlpha.Premultiplied,
            };
            _swap = _gpu.Factory.CreateSwapChainForComposition(_gpu.D3D, desc, null);
            using (var surface = _swap.GetBuffer<IDXGISurface>(0))
                _target = _gpu.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            _gpu.DComp.CreateTargetForHwnd(_hwnd, true, out _dt!).CheckError();
            _gpu.DComp.CreateVisual(out _vis!).CheckError();
            _vis.SetContent(_swap);
            _dt.SetRoot(_vis);
            _gpu.DComp.Commit();
        }
    }

    void ReadTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _light = k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { _light = false; }
        uint abgr = 0xFFD47800; // Windows default blue
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int a) abgr = unchecked((uint)a);
        }
        catch { }
        float r = (abgr & 0xFF) / 255f, g = ((abgr >> 8) & 0xFF) / 255f, b = ((abgr >> 16) & 0xFF) / 255f;
        // Windows 11 draws its own controls with shades from the accent palette: Light 2 on dark surfaces,
        // Dark 1 on light ones. Use exactly those so the flyout matches the system's switches.
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] pal && pal.Length >= 32)
            {
                int i = _light ? 4 * 4 : 1 * 4;
                r = pal[i] / 255f; g = pal[i + 1] / 255f; b = pal[i + 2] / 255f;
            }
            else if (!_light) { r += (1 - r) * 0.35f; g += (1 - g) * 0.35f; b += (1 - b) * 0.35f; }
        }
        catch { }
        _accent = new Color4(r, g, b, 1);
        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        _accentText = lum > 0.55f ? new Color4(0, 0, 0, 0.9f) : new Color4(1, 1, 1, 1);
    }

    // ------------------------------------------------------------------ animation clock

    void Kick()
    {
        if (!_timer && _hwnd != 0) { Native.SetTimer(_hwnd, 1, 15, 0); _timer = true; _last = _clock.Elapsed.TotalSeconds; }
        Render();
    }

    void StopTimer()
    {
        if (_timer) { Native.KillTimer(_hwnd, 1); _timer = false; }
    }

    void OnTimer()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        bool moving = StepAnims(dt);
        Render();
        if (_closing && now - _closeT > 0.12) { FinishClose(); return; }
        bool opening = now - _openT < 0.25;
        if (!moving && !opening && !_closing && !_editing) StopTimer();
        if (_editing && !moving && !opening) { StopTimer(); Native.SetTimer(_hwnd, 2, 530, 0); } // caret blink only
    }

    static float Approach(float v, float target, float rate, float dt)
    {
        float k = 1 - MathF.Exp(-rate * dt);
        float n = v + (target - v) * k;
        return MathF.Abs(n - target) < 0.002f ? target : n;
    }

    bool StepAnims(float dt)
    {
        bool moving = false;
        foreach (var id in _hover.Keys.ToArray())
        {
            float t = id == _hot ? 1 : 0;
            float v = Approach(_hover[id], t, 18, dt);
            _hover[id] = v; if (v != t) moving = true;
        }
        if (_hot != Id.None && !_hover.ContainsKey(_hot)) { _hover[_hot] = 0; moving = true; }
        foreach (var (id, target) in AnimTargets())
        {
            float v = _anim.TryGetValue(id, out var cur) ? cur : target;
            float n = Approach(v, target, 16, dt);
            _anim[id] = n; if (n != target) moving = true;
        }
        return moving;
    }

    IEnumerable<(Id, float)> AnimTargets()
    {
        var s = App.CurrentSettings;
        int eff = Array.IndexOf(App.EffectNames, s.Effect);
        for (int i = 0; i < App.EffectNames.Length; i++) yield return (Id.Chip0 + i, i == eff ? 1 : 0);
        yield return (Id.Pause, s.Paused ? 1 : 0);
        yield return (Id.Fps60, s.Fps == 60 ? 1 : 0);
        yield return (Id.Startup, s.StartWithWindows ? 1 : 0);
        yield return (Id.Saver, s.RestOnBatterySaver ? 1 : 0);
        yield return (Id.Battery, s.LighterOnBattery ? 1 : 0);
        yield return (Id.Online, s.Online ? 1 : 0);
        yield return (Id.Capture, s.HideFromCapture ? 1 : 0);
    }

    void SyncAnimTargets(bool snap)
    {
        foreach (var (id, t) in AnimTargets()) if (snap || !_anim.ContainsKey(id)) _anim[id] = t;
    }

    float A(Id id) => _anim.TryGetValue(id, out var v) ? v : 0;
    float H(Id id) => _hover.TryGetValue(id, out var v) ? v : 0;

    // ------------------------------------------------------------------ drawing

    float _op = 1; // global fade for open/close

    Color4 C(float r, float g, float b, float a) => new(r, g, b, a * _op);
    Color4 Fg(float a) => _light ? C(0, 0, 0, a) : C(1, 1, 1, a);
    Color4 Acc(float a = 1) => C(_accent.R, _accent.G, _accent.B, a);

    void Fill(Rect r, float radius, Color4 c) { _brush.Color = c; _gpu.Ctx.FillRoundedRectangle(new RoundedRectangle(RF(r), radius, radius), _brush); }
    void Stroke(Rect r, float radius, Color4 c, float w = 1) { _brush.Color = c; _gpu.Ctx.DrawRoundedRectangle(new RoundedRectangle(RF(r), radius, radius), _brush, w); }
    void Text(string s, IDWriteTextFormat f, Rect r, Color4 c) { _brush.Color = c; _gpu.Ctx.DrawText(s, f, r, _brush, DrawTextOptions.EnableColorFont | DrawTextOptions.Clip); }

    static System.Drawing.RectangleF RF(Rect r) => new(r.Left, r.Top, r.Width, r.Height);

    static float Ease(float t) { t = Math.Clamp(t, 0, 1); return 1 - MathF.Pow(1 - t, 3); }

    void Render()
    {
        if (!_visible || _target == null) return;
        var ctx = _gpu.Ctx;
        double now = _clock.Elapsed.TotalSeconds;
        float open = Ease((float)((now - _openT) / 0.22));
        float close = _closing ? Ease((float)((now - _closeT) / 0.12)) : 0;
        _op = open * (1 - close);
        float slide = (1 - open) * 14 + close * 8;

        ctx.Target = _target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        var oldAa = ctx.AntialiasMode;
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        ctx.Transform = Matrix3x2.CreateScale(_s);
        if (_view == View.Dial)
        {
            try { DrawDial(slide * 0.6f); }
            finally { ctx.Transform = Matrix3x2.Identity; ctx.AntialiasMode = oldAa; ctx.EndDraw(); ctx.Target = null; }
            _swap!.Present(1, PresentFlags.None);
            return;
        }

        // Surface: a tint over the system acrylic (or an almost opaque panel if acrylic is unavailable).
        var full = new Rect(0.5f, 0.5f, W - 1, _h - 1);
        float tintA = _acrylic ? 0.62f : 0.96f;
        Fill(full, 8, _light ? C(0.976f, 0.976f, 0.976f, tintA) : C(0.125f, 0.125f, 0.13f, tintA));
        Stroke(full, 8, Fg(_light ? 0.10f : 0.09f));

        ctx.Transform = Matrix3x2.CreateTranslation(0, slide) * Matrix3x2.CreateScale(_s);
        try
        {
            if (_view == View.Effects) { _hit.Clear(); _sliderKeys.Clear(); EffectsLayout(true); } else Layout(measureOnly: false);
        }
        finally
        {
            // Always end the draw: the device context is shared with the weather overlay, and a draw left open
            // stops the weather rendering too (D2DERR_WRONG_STATE).
            ctx.Transform = Matrix3x2.Identity;
            ctx.AntialiasMode = oldAa;
            ctx.EndDraw();
            ctx.Target = null;
        }
        _swap!.Present(1, PresentFlags.None);
    }

    /// <summary>Lays out and (unless measuring) draws everything. Returns the total height in DIPs.</summary>
    float Layout(bool measureOnly)
    {
        var s = App.CurrentSettings;
        var st = App.CurrentState;
        bool draw = !measureOnly;
        if (draw) _hit.Clear();
        float y = Pad;
        float iw = W - Pad * 2;

        // Header: what it is doing outside right now.
        var wx = st.Weather;
        string desc = wx != null ? WeatherService.Describe(wx.Code) : "Waiting for the weather";
        string temp = App.TempText();
        string place = st.Place.Length > 0 ? st.Place : "Finding your location";
        string sub = string.Join("  ·  ", new[] { temp, place }.Where(t => t.Length > 0));
        string status = App.StatusText();
        if (draw)
        {
            Fill(new Rect(Pad, y, 52, 52), 10, Fg(_light ? 0.05f : 0.06f));
            Text(App.ConditionEmoji(), _emojiBig, new Rect(Pad, y + 1, 52, 52), Fg(1));
            Text(desc, _title, new Rect(Pad + 64, y - 1, iw - 64 - 34, 26), Fg(0.95f));
            BackButton(new Rect(W - Pad - 28, y - 4, 30, 30));
            Text(sub, _body, new Rect(Pad + 64, y + 24, iw - 64, 18), Fg(0.72f));
            Text(status, _small, new Rect(Pad + 64, y + 40, iw - 64, 16), status.StartsWith("Live") ? Fg(0.5f) : Acc());
            string details = App.DetailsText();
            if (details.Length > 0) Text(details, _small, new Rect(Pad, y + 58, iw, 16), Fg(0.5f));
        }
        y += 52 + 18 + 18;

        // Effect: chips.
        if (draw) Text("Effect", _label, new Rect(Pad, y, iw, 16), Fg(0.62f));
        y += 22;
        const int cols = 7;
        float gap = 6, cw = (iw - gap * (cols - 1)) / cols, ch = 54;
        for (int i = 0; i < App.EffectNames.Length; i++)
        {
            int col = i % cols, row = i / cols;
            var r = new Rect(Pad + col * (cw + gap), y + row * (ch + gap), cw, ch);
            if (!draw) continue;
            var id = Id.Chip0 + i;
            _hit[id] = r;
            float sel = A(id), hov = H(id);
            Fill(r, 6, Fg((_light ? 0.035f : 0.045f) + hov * 0.05f));
            if (sel > 0.01f) Fill(r, 6, Acc(sel));
            if (_pressed == id) Fill(r, 6, Fg(0.04f));
            var tc = sel > 0.5f ? C(_accentText.R, _accentText.G, _accentText.B, _accentText.A) : Fg(0.88f);
            if (App.GlyphFor(App.EffectNames[i]).Length > 0) Text(App.GlyphFor(App.EffectNames[i]), _emoji, new Rect(r.Left, r.Top + 5, r.Width, 28), Fg(1));
            else Text("", _iconCenter, new Rect(r.Left, r.Top + 5, r.Width, 28), tc);
            Text(App.EffectNames[i], _smallCenter, new Rect(r.Left, r.Top + 34, r.Width, 18), tc);
        }
        int rows = (App.EffectNames.Length + cols - 1) / cols;
        y += ch * rows + gap * (rows - 1) + 16;

        // Amounts ("let me adjust the leaf fall amount, snowfall, rainfall"): one slider each, 0-200%.
        if (draw) Text("Amount", _label, new Rect(Pad, y, iw, 16), Fg(0.62f));
        y += 22;
        string[] names = { "Snow", "Rain", "Leaves", "Clouds" };
        for (int i = 0; i < names.Length; i++)
        {
            var r = new Rect(Pad, y, iw, 28);
            if (draw) DrawSlider(Id.Slider0 + i, r, names[i], names[i]);
            y += 28 + 2;
        }
        y += 10;

        // Switches.
        (Id id, string label)[] toggles =
        {
            (Id.Pause, "Pause"), (Id.Fps60, "Smoother motion (60 fps)"), (Id.Startup, "Start with Windows"),
            (Id.Online, "Use online weather"), (Id.Battery, "Lighter on battery (20 fps)"), (Id.Saver, "Rest on battery saver"), (Id.Capture, "Hide from screen capture"),
        };
        float rowH = 36;
        var card = new Rect(Pad, y, iw, rowH * toggles.Length);
        if (draw) { Fill(card, 7, Fg(_light ? 0.035f : 0.04f)); Stroke(card, 7, Fg(0.05f)); }
        foreach (var (id, label) in toggles)
        {
            var r = new Rect(Pad, y, iw, rowH);
            if (draw)
            {
                _hit[id] = r;
                float hv = H(id);
                if (hv > 0.01f) Fill(new Rect(r.Left + 3, r.Top + 3, r.Width - 6, r.Height - 6), 5, Fg(hv * 0.045f));
                Text(label, _body, new Rect(r.Left + 14, r.Top, r.Width - 80, r.Height), Fg(0.9f));
                DrawSwitch(r.Right - 14 - 40, r.Top + (rowH - 20) / 2, A(id), hv);
                if (id != Id.Capture) Divider(r.Left + 14, r.Right - 14, r.Bottom);
            }
            y += rowH;
        }
        y += 12;

        // Actions.
        string where = s.City.Length > 0 ? (st.CityNotFound ? $"{s.City}: not found" : st.LocSource == "city" && st.Place.Length > 0 ? st.Place : s.City)
                                         : $"Automatic{(st.Place.Length > 0 ? " · " + st.Place : "")}";
        (Id id, string icon, string label, string value)[] actions =
        {
            (Id.Location, "", "Location", where),
            (Id.ClearSnow, "", "Clear all snow", ""),
            (Id.Refresh, "", "Refresh weather", ""),
            (Id.Privacy, "\uE72E", "Privacy", "What it sends, and to whom"),
        };
        var card2 = new Rect(Pad, y, iw, rowH * actions.Length);
        if (draw) { Fill(card2, 7, Fg(_light ? 0.035f : 0.04f)); Stroke(card2, 7, Fg(0.05f)); }
        foreach (var (id, icon, label, value) in actions)
        {
            var r = new Rect(Pad, y, iw, rowH);
            if (draw)
            {
                _hit[id] = r;
                float hv = H(id);
                if (hv > 0.01f && !(_editing && id == Id.Location)) Fill(new Rect(r.Left + 3, r.Top + 3, r.Width - 6, r.Height - 6), 5, Fg(hv * 0.045f));
                Text(icon, _icon, new Rect(r.Left + 14, r.Top, 22, r.Height), Fg(0.85f));
                if (_editing && id == Id.Location) DrawEditField(new Rect(r.Left + 44, r.Top + 5, r.Width - 54, r.Height - 10));
                else
                {
                    Text(label, _body, new Rect(r.Left + 44, r.Top, 140, r.Height), Fg(0.9f));
                    if (value.Length > 0) Text(value, _bodyRight, new Rect(r.Left + 150, r.Top, r.Width - 150 - 34, r.Height), Fg(0.6f));
                    if (id == Id.Location) Text("", _icon, new Rect(r.Right - 30, r.Top, 16, r.Height), Fg(0.55f));
                }
                if (id != Id.Privacy) Divider(r.Left + 14, r.Right - 14, r.Bottom);
            }
            y += rowH;
        }
        y += 10;

        // What the weather costs right now ("put the stats... how much the current weather is taxing on the laptop").
        if (draw) Text("Cost to this laptop", _label, new Rect(Pad, y, iw, 16), Fg(0.62f));
        y += 22;
        if (draw)
        {
            bool paused = App.StatsLine().StartsWith("Paused");
            (string label, string value)[] tiles =
            {
                ("CPU", paused ? "0%" : $"{App.StatCpu:0.0#}%"),
                ("Memory", $"{App.StatMemMb:0} MB"),
                ("Frames", paused ? "0 / s" : $"{App.StatFps:0} / s"),
            };
            float tw = (iw - 16) / 3;
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect(Pad + i * (tw + 8), y, tw, 42);
                Fill(r, 6, Fg(_light ? 0.035f : 0.045f));
                Text(tiles[i].value, _body, new Rect(r.Left + 10, r.Top + 3, r.Width - 14, 20), Fg(0.92f));
                Text(tiles[i].label, _small, new Rect(r.Left + 10, r.Top + 22, r.Width - 14, 16), Fg(0.55f));
            }
        }
        y += 42 + 4;
        if (draw)
        {
            float cpu = App.StatCpu;
            string note = App.StatsLine();
            string verdict = note.Length > 0 ? note : cpu < 1 ? "Light: under 1% of your CPU" : cpu < 3 ? "Moderate" : "Heavy: try lower amounts or 30 fps";
            Text(verdict, _small, new Rect(Pad + 2, y, iw, 16), Fg(0.5f));
        }
        y += 18 + 8;

        // Footer.
        var q = new Rect(Pad - 6, y, 110, 32);
        if (draw)
        {
            _hit[Id.Quit] = q;
            float hv = H(Id.Quit);
            if (hv > 0.01f) Fill(q, 5, Fg(hv * 0.06f));
            Text("", _icon, new Rect(q.Left + 10, q.Top, 20, q.Height), Fg(0.8f));
            Text("Quit", _body, new Rect(q.Left + 36, q.Top, 70, q.Height), Fg(0.85f));
            Text("Windows Weather", _bodyRight, new Rect(Pad, y, iw, 32), Fg(0.45f));
        }
        y += 32 + Pad - 4;
        return y;
    }

    void Divider(float x0, float x1, float y)
    {
        _brush.Color = Fg(_light ? 0.07f : 0.07f);
        _gpu.Ctx.FillRectangle(new Rect(x0, y - 0.5f, x1 - x0, 1), _brush);
    }

    void DrawSwitch(float x, float y, float on, float hover)
    {
        var track = new Rect(x, y, 40, 20);
        if (on < 0.999f)
        {
            Fill(track, 10, Fg((_light ? 0.02f : 0.03f) + hover * 0.04f));
            Stroke(new Rect(x + 0.5f, y + 0.5f, 39, 19), 9.5f, Fg((_light ? 0.55f : 0.6f) * (1 - on)));
        }
        if (on > 0.001f) Fill(track, 10, Acc(on));
        float r = 6 + 1 * hover; // knob grows a touch on hover, like Windows 11
        float cx = x + 10 + on * 20, cy = y + 10;
        var knobOff = Fg(_light ? 0.62f : 0.8f);
        var knobOn = C(_accentText.R, _accentText.G, _accentText.B, 1);
        var kc = new Color4(knobOff.R + (knobOn.R - knobOff.R) * on, knobOff.G + (knobOn.G - knobOff.G) * on,
            knobOff.B + (knobOn.B - knobOff.B) * on, (knobOff.A + (knobOn.A - knobOff.A) * on));
        _brush.Color = new Color4(kc.R, kc.G, kc.B, kc.A * _op);
        _gpu.Ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), _brush);
    }

    void DrawEditField(Rect r)
    {
        Fill(r, 4, _light ? C(1, 1, 1, 0.9f) : C(1, 1, 1, 0.07f));
        _brush.Color = Acc();
        _gpu.Ctx.FillRectangle(new Rect(r.Left + 1, r.Bottom - 2, r.Width - 2, 2), _brush);
        var tr = new Rect(r.Left + 10, r.Top, r.Width - 20, r.Height);
        if (_edit.Length == 0) Text("City, like Denver CO (empty = automatic)", _small, tr, Fg(0.45f));
        else Text(_edit, _body, tr, Fg(0.92f));
        bool caretOn = ((_clock.Elapsed.TotalSeconds - _caretT) % 1.06) < 0.53;
        if (caretOn)
        {
            float tw = 0;
            if (_edit.Length > 0) using (var l = _dw!.CreateTextLayout(_edit, _body, 1000, 40)) tw = l.Metrics.WidthIncludingTrailingWhitespace;
            _brush.Color = Fg(0.9f);
            _gpu.Ctx.FillRectangle(new Rect(tr.Left + MathF.Min(tw, tr.Width) + 1, r.Top + 8, 1.2f, r.Height - 16), _brush);
        }
    }

    // ------------------------------------------------------------------ input

    Id HitTest(int px, int py)
    {
        float x = px / _s, y = py / _s;
        if (_view == View.Dial)
        {
            var d = HitDial(x, y);
            if (d != Id.None) return d == Id.Outside ? Id.None : d;
        }
        foreach (var (id, r) in _hit)
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return id;
        return Id.None;
    }

    void Activate(Id id)
    {
        var s = App.CurrentSettings;
        if (id >= Id.Chip0 && id < Id.Chip0 + App.EffectNames.Length)
        {
            App.FlyoutCommand("effect", App.EffectNames[id - Id.Chip0]);
            if (_view == View.Effects) PlaceWindow(); // the sliders below the chips change with the effect
        }
        else switch (id)
        {
            case Id.Pause: App.FlyoutCommand("pause", ""); break;
            case Id.Fps60: App.FlyoutCommand("fps", s.Fps == 60 ? "30" : "60"); break;
            case Id.Startup: App.FlyoutCommand("startup", ""); break;
            case Id.Saver: App.FlyoutCommand("saver", ""); break;
            case Id.Battery: App.FlyoutCommand("battery", ""); break;
            case Id.Online: App.FlyoutCommand("online", ""); break;
            case Id.Privacy: App.FlyoutCommand("privacy", ""); break;
            case Id.Capture: App.FlyoutCommand("capture", ""); break;
            case Id.Location: _editing = true; _edit = s.City; _caretT = _clock.Elapsed.TotalSeconds; break;
            case Id.ClearSnow: App.FlyoutCommand("clear", ""); break;
            case Id.Refresh: App.FlyoutCommand("refresh", ""); break;
            case Id.Quit: Close(); App.FlyoutCommand("quit", ""); return;
            case Id.Gear: SetView(View.Settings); return;
            case Id.WeatherBtn: SetView(View.Effects); return;
            case Id.Back: SetView(View.Dial); return;
            case Id.Now: App.SetPreview(null); break;
        }
        Kick();
    }

    void CommitEdit()
    {
        _editing = false;
        Native.KillTimer(_hwnd, 2);
        App.FlyoutCommand("city", _edit.Trim());
        Kick();
    }

    public nint Proc(nint h, uint m, nuint w, nint l)
    {
        switch (m)
        {
            case Native.WM_TIMER:
                if (w == 1) OnTimer();
                else if (w == 2) Render(); // caret blink
                return 0;
            case 0x0200: // WM_MOUSEMOVE
            {
                if (_drag == Id.Arc) { ArcTo((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)); return 0; }
                if (_drag == Id.Amount) { AmountTo((short)((l >> 16) & 0xFFFF)); return 0; }
                if (_drag != Id.None) { DragTo((short)(l & 0xFFFF)); return 0; }
                var id = HitTest((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                if (id != _hot) { _hot = id; Kick(); }
                var tme = new Native.TRACKMOUSEEVENT { cbSize = (uint)sizeof(Native.TRACKMOUSEEVENT), dwFlags = 2 /*TME_LEAVE*/, hwndTrack = h };
                Native.TrackMouseEvent(&tme);
                return 0;
            }
            case 0x02A3: // WM_MOUSELEAVE
                if (_drag != Id.None) return 0;
                _hot = Id.None; _pressed = Id.None; Kick();
                return 0;
            case 0x0201: // WM_LBUTTONDOWN
                _pressed = HitTest((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                if (_editing && _pressed != Id.Location) CommitEdit();
                if (_pressed >= Id.Slider0 && _pressed < Id.Slider0 + 20) { _drag = _pressed; Native.SetCapture(h); DragTo((short)(l & 0xFFFF)); return 0; }
                if (_pressed == Id.Arc) { _drag = Id.Arc; Native.SetCapture(h); ArcTo((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)); return 0; }
                if (_pressed == Id.Amount) { _drag = Id.Amount; Native.SetCapture(h); AmountTo((short)((l >> 16) & 0xFFFF)); return 0; }
                Kick();
                return 0;
            case 0x0202: // WM_LBUTTONUP
            {
                if (_drag != Id.None) { _drag = Id.None; _pressed = Id.None; Native.ReleaseCapture(); App.FlyoutCommand("amount-done", ""); Kick(); return 0; }
                var id = HitTest((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                var was = _pressed; _pressed = Id.None;
                if (id != Id.None && id == was && !(_editing && id == Id.Location)) Activate(id);
                else Kick();
                return 0;
            }
            case Native.WM_KEYDOWN:
                if (w == 0x1B) { if (_editing) { _editing = false; Native.KillTimer(_hwnd, 2); Kick(); } else if (_view != View.Dial) SetView(View.Dial); else Close(); }
                else if (w == 0x0D && _editing) CommitEdit();
                return 0;
            case 0x0102: // WM_CHAR
                if (_editing)
                {
                    char c = (char)w;
                    if (c == '\b') { if (_edit.Length > 0) _edit = _edit[..^1]; }
                    else if (c >= ' ' && _edit.Length < 60) _edit += c;
                    _caretT = _clock.Elapsed.TotalSeconds;
                    Render();
                }
                return 0;
            case 0x0006: // WM_ACTIVATE
                if ((w & 0xFFFF) == 0 && _visible) Close(); // clicked somewhere else
                return 0;
            case Native.WM_NCHITTEST:
                return 1; // HTCLIENT: the whole panel is client area
            case Native.WM_MOUSEACTIVATE:
                return 1; // MA_ACTIVATE
            case Native.WM_ERASEBKGND:
                return 1;
        }
        return Native.DefWindowProcW(h, m, w, l);
    }

    public void Dispose()
    {
        StopTimer();
        _target?.Dispose(); _vis?.Dispose(); _dt?.Dispose(); _swap?.Dispose(); _brush?.Dispose(); _round?.Dispose();
        foreach (var f in new[] { _title, _body, _bodyRight, _small, _smallCenter, _label, _icon, _iconCenter, _emoji, _emojiBig }) f?.Dispose();
        if (_hwnd != 0) Native.DestroyWindow(_hwnd);
        _hwnd = 0;
        if (Instance == this) Instance = null;
    }
}
