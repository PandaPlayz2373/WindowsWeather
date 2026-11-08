using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// Icicles ("have icicles form on un fullscreen windows corners that hang down"). When it is snowing
// or freezing, a few icicles grow slowly down from near the top corners of every normal (not maximized) window, where
// its top edge is visible. A resize or a hard fling snaps them off and they fall; when it warms up they melt and drip.
// Drawn as tapered translucent strokes with a cool rim and a bright highlight, only while some exist.
public sealed partial class World
{
    sealed class Icicle { public float Off, Len, Max, W, Rate; }
    readonly Dictionary<nint, List<Icicle>> _icicles = new();
    bool _iciclesGrowing;
    double _icicleTick;

    bool Freezing => Cur.Snow > 0.2f || Cur.Frost > 0.25f;

    void StepIcicles(float dt)
    {
        _iciclesGrowing = false;
        if (_icicles.Count == 0 && !Freezing) return;
        if (Now < _icicleTick) return; // slow things: 5 updates a second is plenty
        float step = (float)Math.Min(1, Now - (_icicleTick - 0.2)); _icicleTick = Now + 0.2;
        float grow = Mod("Icicles.Length");
        // Forget windows that are gone, maximized or no longer collect snow.
        List<nint>? gone = null;
        foreach (var (h, list) in _icicles)
            if (!Tracker.ByHwnd.TryGetValue(h, out var t) || t.Maximized || !t.Eligible || t.IsToast) (gone ??= new()).Add(h);
        if (gone != null) foreach (var h in gone) { if (Tracker.ByHwnd.TryGetValue(h, out var t)) BreakIcicles(t, 0, 0); _icicles.Remove(h); }

        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.Maximized || w.IsTaskbar || w.IsToast || w.R.Width < 160 || w.R.Height < 120) continue;
            if (!_icicles.TryGetValue(w.Hwnd, out var list))
            {
                if (!Freezing) continue;
                list = new();
                float s = w.Scale;
                foreach (int side in new[] { 0, 1 })
                {
                    int n = 1 + _rnd.Next(3);
                    for (int i = 0; i < n; i++)
                    {
                        float off = (7 + i * 11 + Rf() * 8) * s;
                        list.Add(new Icicle
                        {
                            Off = side == 0 ? off : w.R.Width - off,
                            Max = (16 + 46 * Rf() * (i == 0 ? 1 : 0.6f)) * s,
                            W = (3.2f + 2.6f * Rf()) * s,
                            Rate = 1f / (40 + 50 * Rf()), // fraction of full length per second
                        });
                    }
                }
                _icicles[w.Hwnd] = list;
            }
            foreach (var ic in list)
            {
                bool visible = InSpans(w, Math.Clamp((int)(ic.Off / w.ColW), 0, Math.Max(0, w.H.Length - 1)));
                if (Freezing && visible)
                {
                    float max = ic.Max * grow;
                    if (ic.Len < max) { ic.Len = MathF.Min(max, ic.Len + max * ic.Rate * step); _iciclesGrowing = true; }
                }
                else if (!Freezing)
                {
                    // Melting: shorter, with the odd drip off the tip.
                    ic.Len = MathF.Max(0, ic.Len - ic.Max / 25f * step);
                    if (ic.Len > 2 && Rf() < 0.08f) AddDebris(w.R.Left + ic.Off, w.R.Top + ic.Len, 0, 30, 1.6f * w.Scale, true);
                    _iciclesGrowing = true;
                }
            }
            if (!Freezing && list.TrueForAll(i => i.Len <= 0)) _icicles.Remove(w.Hwnd);
        }
    }

    /// <summary>Snap a window's icicles off (resize, fling); the pieces fall with the window's motion.</summary>
    void BreakIcicles(Tracked w, float vx, float vy)
    {
        if (!_icicles.TryGetValue(w.Hwnd, out var list)) return;
        foreach (var ic in list)
        {
            if (ic.Len < 3) continue;
            int pieces = 1 + (int)(ic.Len / (12 * w.Scale));
            for (int k = 0; k < pieces; k++)
                AddDebris(w.R.Left + ic.Off, w.R.Top + ic.Len * (k + 0.5f) / pieces, vx * 0.3f + (Rf() - 0.5f) * 60, vy * 0.3f - 40 * Rf(), MathF.Min(ic.W, 4 * w.Scale), false);
        }
        _icicles.Remove(w.Hwnd);
    }

    bool IciclesVisible(Monitor m)
    {
        foreach (var (h, list) in _icicles)
            if (Tracker.ByHwnd.TryGetValue(h, out var w) && w.R.Left < m.Bounds.Right && w.R.Right > m.Bounds.Left && w.R.Top < m.Bounds.Bottom && w.R.Top > m.Bounds.Top)
                foreach (var ic in list) if (ic.Len > 0.5f) return true;
        return false;
    }

    void DrawIcicles(Gpu gpu, Monitor m)
    {
        if (_icicles.Count == 0) return;
        var ctx = gpu.Ctx; var brush = gpu.FrostBrush;
        var oldAa = ctx.AntialiasMode;
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var (h, list) in _icicles)
        {
            if (!Tracker.ByHwnd.TryGetValue(h, out var w) || w.Maximized) continue;
            if (w.R.Left >= m.Bounds.Right || w.R.Right <= m.Bounds.Left || w.R.Top <= m.Bounds.Top || w.R.Top >= m.Bounds.Bottom) continue;
            foreach (var ic in list)
            {
                if (ic.Len < 0.5f) continue;
                if (!InSpans(w, Math.Clamp((int)(ic.Off / w.ColW), 0, Math.Max(0, w.H.Length - 1)))) continue;
                float x = w.R.Left + ic.Off, y = w.R.Top + 0.5f;
                // A tapered cone in four strokes, thinner toward the tip, with a slight wobble so they're not rulers.
                const int segs = 4;
                for (int pass = 0; pass < 3; pass++)
                {
                    for (int k = 0; k < segs; k++)
                    {
                        float t0 = k / (float)segs, t1 = (k + 1) / (float)segs;
                        float wd = ic.W * (1 - t0 * 0.85f);
                        float wob = MathF.Sin(ic.Off * 0.7f + k * 1.9f) * 0.5f * w.Scale;
                        var a = new Vector2(x + wob * t0, y + ic.Len * t0);
                        var b = new Vector2(x + wob * t1, y + ic.Len * t1);
                        switch (pass)
                        {
                            case 0: brush.Color = new Color4(0.45f, 0.58f, 0.72f, 0.38f); ctx.DrawLine(a, b, brush, wd + 1.4f * w.Scale, gpu.RoundStroke); break;
                            case 1: brush.Color = new Color4(0.86f, 0.93f, 1f, 0.78f); ctx.DrawLine(a, b, brush, wd, gpu.RoundStroke); break;
                            default:
                                brush.Color = new Color4(1f, 1f, 1f, 0.85f);
                                var o = new Vector2(-wd * 0.22f, 0);
                                ctx.DrawLine(a + o, b + o, brush, MathF.Max(0.7f, wd * 0.22f), gpu.RoundStroke); break;
                        }
                    }
                }
            }
        }
        ctx.AntialiasMode = oldAa;
    }
}
