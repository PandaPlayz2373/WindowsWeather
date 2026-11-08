using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// Earthquake ("earth quakes that break things. like un fullscreen windows. or make icons fall off
// your task bar"). A quake is a short rumble (P-wave), a main shock and a decaying aftershake. While it shakes, every
// normal window jitters and walks a little (WorldGrabs), snow, puddles and resting leaves are shaken off, the taskbar
// icons jump off the taskbar and fall off the screen (WorldTaskbar), strong ones crack the screen, and maximized windows
// are knocked out of maximized. When it is over everything goes back: icons fly home, windows glide back and the
// un-maximized ones are maximized again (the one that was in front last, so it stays in front).
public sealed partial class World
{
    bool _qOn;
    double _qStart, _qNext, _remaxAt = -1;
    float _qMag, _qMain;
    bool _qKnocked, _qUnmaxed;
    readonly List<nint> _unmaxed = new();
    public float QuakeAmp { get; private set; }
    double _qShakeAt;

    struct CrackSeg { public Vector2 A, B; public float W; }
    readonly List<CrackSeg> _cracks = new();
    Monitor? _crackMon;
    double _crackT = -100;
    const double CrackLife = 11;

    public void QuakeNow() { _qNext = 0; if (!_qOn) StartQuake(); Kick(1); }

    void StartQuake()
    {
        _qOn = true; _qStart = Now; _qKnocked = _qUnmaxed = false;
        _qMag = Math.Clamp((0.5f + 0.5f * Rf()) * Mod("Earthquake.Magnitude"), 0.15f, 2f);
        _qMain = 2.5f + 3f * MathF.Min(_qMag, 1.3f);
    }

    void StepQuake(float dt)
    {
        if (!_qOn && Cur.Quake > 0.4f && Now >= _qNext && Monitors.Count > 0) StartQuake();
        if (_remaxAt > 0 && Now >= _remaxAt) Remaximize();
        if (!_qOn) { QuakeAmp = 0; return; }
        double t = Now - _qStart;
        float a;
        if (t < 1.5) a = (float)(0.2 * t / 1.5);                                   // the rumble before
        else if (t < 1.5 + _qMain) a = 0.75f + 0.25f * MathF.Sin((float)t * 7.1f) * MathF.Sin((float)t * 2.3f);
        else a = (float)Math.Exp(-(t - 1.5 - _qMain) * 1.3);                       // dying away
        a *= _qMag * Ss(Cur.Quake * 2);
        QuakeAmp = a;
        if (!_qKnocked && t > 1.9) { _qKnocked = true; KnockTaskbar(); if (_qMag > 0.65f) AddCracks(); }
        if (!_qUnmaxed && t > 2.3) { _qUnmaxed = true; if (_qMag > 0.55f) Unmaximize(); }
        if (Now >= _qShakeAt) { _qShakeAt = Now + 0.1; ShakeLoose(a); }
        if (t > 1.5 + _qMain + 3.5 || Cur.Quake < 0.05f)
        {
            _qOn = false; QuakeAmp = 0;
            float every = 26 / MathF.Max(0.2f, Mod("Earthquake.Frequency"));
            _qNext = Now + every * (0.6f + 0.8f * Rf());
            _remaxAt = Now + 4.5;
        }
    }

    // Snow, water, beads and resting leaves shaken off every edge, harder the stronger it shakes.
    void ShakeLoose(float a)
    {
        if (a < 0.08f) return;
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0) continue;
            bool hit = false;
            float p = MathF.Min(0.6f, a * 0.35f);
            for (int c = 0; c < w.H.Length; c++)
            {
                if (w.H[c] < 0.4f || Rf() > p) continue;
                float cx = w.R.Left + (c + 0.5f) * w.ColW;
                if (_rnd.Next(4) == 0) AddDebris(cx, w.R.Top - w.H[c], (Rf() - 0.5f) * 160 * w.Scale, -(40 + 120 * Rf() * a) * w.Scale, (2 + Rf() * 2.5f) * w.Scale, false);
                w.H[c] *= 0.55f; hit = true;
            }
            for (int c = 0; c < w.Wt.Length; c++)
            {
                if (w.Wt[c] < 0.3f || Rf() > p) continue;
                float cx = w.R.Left + (c + 0.5f) * w.ColW;
                if (_rnd.Next(4) == 0) AddDebris(cx, w.R.Top - 2, (Rf() - 0.5f) * 200 * w.Scale, -(60 + 100 * Rf()) * w.Scale, (1.5f + Rf() * 1.5f) * w.Scale, true);
                w.WaterTotal -= w.Wt[c] * 0.5f; w.Wt[c] *= 0.5f; hit = true;
            }
            if (w.Beads.Count > 0 && Rf() < a) { float bx = w.R.Left + w.Beads[^1]; AddDebris(bx, w.R.Top - 3, 0, 0, 3 * w.Scale, true); w.Beads.RemoveAt(w.Beads.Count - 1); hit = true; }
            if (hit) { w.WaterTotal = MathF.Max(0, w.WaterTotal); RecountPile(w); }
        }
        for (int i = 0; i < _nLeaves; i++)
        {
            ref var l = ref _leaves[i];
            if (l.On != null && Rf() < a * 0.5f) { l.On = null; l.Vy = -60 * Rf(); l.Spin = (Rf() - .5f) * 8; }
        }
    }

    void Unmaximize()
    {
        foreach (var t in Tracker.Z)
        {
            if (!t.Maximized || t.IsTaskbar || t.IsToast || _unmaxed.Contains(t.Hwnd)) continue;
            long ex = Native.GetWindowLongPtrW(t.Hwnd, -20);
            if ((ex & 0x8) != 0 || (ex & 0x80) != 0) continue;
            if (GrabOnlyTitle != null && !Native.WindowText(t.Hwnd).StartsWith(GrabOnlyTitle)) continue;
            Native.ShowWindowAsync(t.Hwnd, 4); // SW_SHOWNOACTIVATE: restore from maximized without taking focus
            _unmaxed.Add(t.Hwnd);
        }
    }

    // Maximize them again, the one in front last so it stays in front.
    void Remaximize()
    {
        foreach (var g in _grabs) if (_unmaxed.Contains(g.H) && !g.Home) { _remaxAt = Now + 1; return; } // still gliding home
        _remaxAt = -1;
        nint fg = Native.GetForegroundWindow();
        foreach (var h in _unmaxed)
            if (h != fg && Native.IsWindow(h) != 0 && Native.IsIconic(h) == 0 && Native.IsZoomed(h) == 0) Native.ShowWindowAsync(h, 3);
        if (_unmaxed.Contains(fg) && Native.IsZoomed(fg) == 0 && Native.IsIconic(fg) == 0) Native.ShowWindowAsync(fg, 3);
        _unmaxed.Clear();
    }

    // ------------------------------------------------------------------ screen cracks

    void AddCracks()
    {
        if (Monitors.Count == 0) return;
        var m = Monitors[PickMonitor()];
        var b = m.Bounds;
        float s = m.Scale;
        _cracks.Clear(); _crackMon = m; _crackT = Now;
        int sites = _qMag > 1.1f ? 2 : 1;
        for (int k = 0; k < sites; k++)
        {
            var c = new Vector2(b.Left + (0.18f + 0.64f * Rf()) * b.Width, b.Top + (0.2f + 0.55f * Rf()) * b.Height);
            int rays = 7 + _rnd.Next(4);
            float size = (110 + 160 * Rf()) * s * MathF.Min(1.6f, _qMag);
            var ends = new List<Vector2>();
            for (int r = 0; r < rays; r++)
            {
                float ang = (r + Rf() * 0.6f) / rays * MathF.Tau;
                ends.Add(CrackRay(c, ang, size * (0.5f + 0.7f * Rf()), 1.6f * s, true, s));
            }
            // A ring of short cross-cracks around the impact point.
            float rr = (18 + 16 * Rf()) * s;
            for (int r = 0; r < rays; r++)
            {
                float a0 = (r + 0.3f) / rays * MathF.Tau, a1 = (r + 1.1f) / rays * MathF.Tau;
                var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * rr * (0.8f + 0.4f * Rf());
                var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * rr * (0.8f + 0.4f * Rf());
                _cracks.Add(new CrackSeg { A = p0, B = p1, W = 1.0f * s });
            }
        }
    }

    Vector2 CrackRay(Vector2 from, float ang, float len, float width, bool branch, float s)
    {
        var p = from;
        float done = 0;
        while (done < len)
        {
            float step = (14 + 22 * Rf()) * s;
            ang += (Rf() - 0.5f) * 0.55f;
            var q = p + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * step;
            _cracks.Add(new CrackSeg { A = p, B = q, W = MathF.Max(0.6f * s, width * (1 - done / len * 0.6f)) });
            p = q; done += step;
            if (branch && Rf() < 0.18f) CrackRay(p, ang + (Rf() < 0.5f ? -1 : 1) * (0.5f + 0.5f * Rf()), (len - done) * 0.5f, width * 0.6f, false, s);
        }
        return p;
    }

    void DrawCracks(Gpu gpu, Monitor m)
    {
        if (_cracks.Count == 0 || _crackMon != m) return;
        double t = Now - _crackT;
        if (t > CrackLife) { _cracks.Clear(); return; }
        float fade = (float)Math.Clamp((CrackLife - t) / 3, 0, 1) * MathF.Min(1, (float)t * 12);
        var ctx = gpu.Ctx; var brush = gpu.FrostBrush;
        var oldAa = ctx.AntialiasMode;
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        var off = new Vector2(0.9f, 0.9f) * m.Scale;
        foreach (var c in _cracks)
        {
            brush.Color = new Color4(0.04f, 0.05f, 0.07f, 0.55f * fade);
            ctx.DrawLine(c.A, c.B, brush, c.W * 1.6f, gpu.RoundStroke);
        }
        foreach (var c in _cracks)
        {
            brush.Color = new Color4(0.92f, 0.95f, 1f, 0.6f * fade);
            ctx.DrawLine(c.A + off, c.B + off, brush, c.W * 0.7f, gpu.RoundStroke);
        }
        ctx.AntialiasMode = oldAa;
    }
}
