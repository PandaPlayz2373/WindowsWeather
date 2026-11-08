using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// Lightning strikes in a storm ("add lighting strikes to the storm").
// A jagged bolt with a few forks drops from under a low cloud (or the top of the screen) to the first window top,
// the taskbar or the bottom of the screen. It strikes, dims, restrikes once and fades, all inside half a second, and
// blows the snow and water off the edge it hits. Drawn as three stroked passes (wide glow, halo, white core) only
// while it is visible, so it costs nothing between strikes.
public sealed partial class World
{
    struct BoltSeg { public Vector2 A, B; public float W, Al; }
    readonly List<BoltSeg> _bolt = new(160);
    Monitor? _boltMon;
    double _boltT = -10;
    Vector2 _boltTop, _boltHit;
    bool _boltBlastPending;

    const double BoltLife = 0.55;

    /// <summary>Brightness of the bolt over its life: strike, dim, one restrike, fade. 0 when gone.</summary>
    float BoltIntensity()
    {
        double t = Now - _boltT;
        if (_bolt.Count == 0 || t < 0 || t > BoltLife) return 0;
        if (t < 0.035) return (float)(0.6 + t / 0.035 * 0.4);
        if (t < 0.085) return (float)(1 - (t - 0.035) / 0.05 * 0.7);
        if (t < 0.12) return (float)(0.3 + (t - 0.085) / 0.035 * 0.6);
        return (float)(0.9 * Math.Exp(-(t - 0.12) * 8));
    }

    public void StrikeNow() { if (Monitors.Count > 0) { MakeBolt(Monitors[PickMonitor()]); _flashT = Now; Kick(1); } }

    void MakeBolt(Monitor m)
    {
        _bolt.Clear();
        var b = m.Bounds;
        float s = m.Scale, h = b.Height;
        int mi = Monitors.IndexOf(m);
        float x0, y0;
        if (!RainSource(mi, out x0, out y0)) { x0 = b.Left + (0.12f + Rf() * 0.76f) * b.Width; y0 = b.Top + h * (0.04f + Rf() * 0.08f); }
        x0 = Math.Clamp(x0, b.Left + 40 * s, b.Right - 40 * s);

        // Where it lands: the first exposed window top (or the taskbar) under the end point, well below the cloud.
        float x1 = Math.Clamp(x0 + (Rf() - 0.5f) * 0.3f * b.Width, b.Left + 20 * s, b.Right - 20 * s);
        float y1 = b.Bottom - 2;
        foreach (var sf in GatherSurfaces())
            if (x1 >= sf.X0 && x1 < sf.X1 && sf.Y > y0 + h * 0.22f && sf.Y < y1 && sf.Y <= b.Bottom) y1 = sf.Y;

        _boltTop = new Vector2(x0, y0);
        _boltHit = new Vector2(x1, y1);
        _boltMon = m;
        _boltT = Now;
        _boltBlastPending = true;

        int forks = 0;
        Walk(new Vector2(x0, y0), new Vector2(x1, y1), 2.6f * s * MathF.Sqrt(Mod("Storm.Strength")), 1f, 9f * s, true, ref forks, s);
    }

    // A random walk toward the target: each step drops a little and jinks sideways, pulled back toward the line.
    void Walk(Vector2 from, Vector2 to, float width, float alpha, float step, bool main, ref int forks, float s)
    {
        var p = from;
        float total = MathF.Max(1, to.Y - from.Y);
        int guard = 0;
        while (p.Y < to.Y - 0.5f && guard++ < 400)
        {
            float remain = to.Y - p.Y;
            float dy = MathF.Min(remain, step * (0.6f + Rf() * 0.9f));
            float pull = (to.X - p.X) / MathF.Max(remain / dy, 1);
            float dx = pull + (Rf() - 0.5f) * step * 2.1f;
            var q = new Vector2(p.X + dx, p.Y + dy);
            if (remain - dy < 0.5f) q.X = main ? to.X : q.X;
            float along = (q.Y - from.Y) / total;
            float w = width * (main ? 1 - 0.35f * along : 1 - 0.8f * along);
            float a = alpha * (main ? 1 : 1 - 0.85f * along);
            _bolt.Add(new BoltSeg { A = p, B = q, W = MathF.Max(0.6f * s, w), Al = a });
            p = q;
            // Forks: thinner, fainter, shorter, ending in the air.
            if (main && forks < 4 && along > 0.08f && along < 0.75f && Rf() < 0.07f)
            {
                forks++;
                float dir = Rf() < 0.5f ? -1 : 1;
                float flen = remain * (0.25f + Rf() * 0.3f);
                var end = new Vector2(p.X + dir * flen * (0.4f + Rf() * 0.5f), p.Y + flen);
                int none = 99;
                Walk(p, end, width * 0.45f, alpha * 0.7f, step * 0.8f, false, ref none, s);
            }
        }
    }

    void StepLightning()
    {
        if (!_boltBlastPending || _boltMon == null) return;
        _boltBlastPending = false;
        BlastAt(_boltHit.X, _boltHit.Y, _boltMon.Scale);
    }

    // The strike throws the snow and water off the edge it hit, plus a few sparks.
    void BlastAt(float x, float y, float s)
    {
        float r = 46 * s;
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0 || MathF.Abs(w.R.Top - y) > 2 || x < w.R.Left - r || x > w.R.Right + r) continue;
            int c0 = Math.Max(0, (int)((x - r - w.R.Left) / w.ColW)), c1 = (int)((x + r - w.R.Left) / w.ColW);
            bool hit = false;
            if (w.H.Length > 0)
                for (int c = c0; c <= Math.Min(w.H.Length - 1, c1); c++)
                {
                    if (w.H[c] < 0.3f || !InSpans(w, c)) continue;
                    float cx = w.R.Left + (c + 0.5f) * w.ColW, side = cx < x ? -1 : 1;
                    float k = 1 - MathF.Abs(cx - x) / r;
                    if (_rnd.Next(2) == 0)
                        AddDebris(cx, w.R.Top - w.H[c] * Rf(), side * (80 + 260 * k * Rf()) * s, -(120 + 320 * k * Rf()) * s, (2 + Rf() * 3) * w.Scale, false);
                    w.H[c] *= 0.15f * (1 - k);
                    hit = true;
                }
            if (w.Wt.Length > 0)
                for (int c = c0; c <= Math.Min(w.Wt.Length - 1, c1); c++)
                {
                    if (w.Wt[c] < 0.2f) continue;
                    float cx = w.R.Left + (c + 0.5f) * w.ColW, side = cx < x ? -1 : 1;
                    if (_rnd.Next(2) == 0)
                        AddDebris(cx, w.R.Top - 2, side * (60 + 200 * Rf()) * s, -(150 + 260 * Rf()) * s, (1.6f + Rf() * 1.6f) * w.Scale, true);
                    w.WaterTotal -= w.Wt[c] * 0.85f;
                    w.Wt[c] *= 0.15f;
                    hit = true;
                }
            if (hit) { w.WaterTotal = MathF.Max(0, w.WaterTotal); RecountPile(w); }
        }
        for (int i = 0; i < 7; i++)
            AddDebris(x, y - 2, (Rf() - 0.5f) * 420 * s, -(140 + Rf() * 260) * s, (1.4f + Rf()) * s, true);
        Kick(1);
    }

    // Additive light: the cloud lit from inside and a glow where it lands. Called after the normal sprites.
    void AddLightningSprites(Monitor m)
    {
        float i = BoltIntensity();
        if (i <= 0.01f || _boltMon != m) return;
        float s = m.Scale;
        float cr = 190 * s;
        _sprites.Add(Atlas.Flare, _boltTop.X - cr, _boltTop.Y - cr * 0.55f, _boltTop.X + cr, _boltTop.Y + cr * 0.45f, Pm(0.80f, 0.85f, 1f, 0.55f * i));
        float hr = 70 * s;
        _sprites.Add(Atlas.Flare, _boltHit.X - hr, _boltHit.Y - hr * 0.7f, _boltHit.X + hr, _boltHit.Y + hr * 0.3f, Pm(0.85f, 0.9f, 1f, 0.8f * i));
    }

    void DrawBolt(Gpu gpu, Monitor m)
    {
        float i = BoltIntensity();
        if (i <= 0.01f || _boltMon != m) return;
        var ctx = gpu.Ctx;
        var brush = gpu.FrostBrush;
        var oldAa = ctx.AntialiasMode;
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        foreach (var g in _bolt)
        {
            brush.Color = new Color4(0.62f, 0.70f, 1f, 0.10f * i * g.Al);
            ctx.DrawLine(g.A, g.B, brush, g.W * 7f, gpu.RoundStroke);
        }
        foreach (var g in _bolt)
        {
            brush.Color = new Color4(0.74f, 0.80f, 1f, 0.38f * i * g.Al);
            ctx.DrawLine(g.A, g.B, brush, g.W * 2.6f, gpu.RoundStroke);
        }
        foreach (var g in _bolt)
        {
            brush.Color = new Color4(1f, 1f, 1f, MathF.Min(1, 1.05f * i * g.Al));
            ctx.DrawLine(g.A, g.B, brush, g.W, gpu.RoundStroke);
        }
        ctx.AntialiasMode = oldAa;
    }
}
