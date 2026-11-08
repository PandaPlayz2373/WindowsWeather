using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// More of the real weather ("all of them"):
//   frost    : creeps in from the screen corners when it is freezing and humid
//   gusts    : bursts from the real gust speed push snow, rain and leaves and blow snow and leaves off edges
//   hail     : pellets that bounce off window tops and the taskbar
//   snow     : real snowfall rate scales the flakes; real snow depth scales how tall piles get
//   moon     : the real phase at its real place in the sky on clear nights
//   aurora   : NOAA's Kp index against this laptop's geomagnetic latitude; soft curtains when it would be visible
//   smog     : the US AQI tints the screen slightly and dims the sun on bad-air days
public sealed partial class World
{
    // ------------------------------------------------------------------ inputs from the weather data

    public float GustKmh, WindKmh;
    public float PileScale = 1;        // from snow depth: taller piles when there is deep snow on the ground
    public float MoonPhase = -1;       // 0 new .. 0.5 full .. 1; -1 unknown
    public float MoonAz, MoonAlt = -90;  // degrees
    public bool ForceMoon;             // "Night" preview: show the moon even if it is below the horizon

    // ------------------------------------------------------------------ frost
    // Real crystal growth ("I want the crystalizing of the corners, not just a scaling low poly image").
    // Each corner grows its own randomly seeded ice: needles start along the screen edges and grow inwards, splitting
    // into side branches at the 60 degree angles ice crystals take, thinner and fainter at every generation. Segments are
    // drawn once, antialiased, into a small cached layer per corner, so only new growth costs anything; a finished
    // frost costs nothing to show. When it warms up the layer fades out, and the next freeze grows a new pattern.

    float _frost;           // frost level, grows towards Cur.Frost slowly (minutes), melts faster
    public bool FastFrost;  // the Frost preview: grow in seconds

    void StepFrost(float dt)
    {
        float target = Cur.Frost;
        float rate = FastFrost ? 1f / 28f : target > _frost ? 1f / 240f : 1f / 50f;
        _frost = target > _frost ? MathF.Min(target, _frost + rate * dt) : MathF.Max(target, _frost - rate * dt);
        _frostGrowing = false;
        foreach (var m in Monitors)
            for (int k = 0; k < 4; k++)
            {
                m.Frost[k] ??= new FrostLayer(k, _rnd.Next());
                if (m.Frost[k]!.Step(dt, _frost, m.Bounds.Width, m.Bounds.Height, FastFrost ? 2.5f : 1f)) _frostGrowing = true;
            }
    }

    bool _frostGrowing;
    bool FrostChanging => _frostGrowing || MathF.Abs(_frost - Cur.Frost) > 0.002f;

    /// <summary>Draw the new crystal segments into each corner's cached layer (before the overlay frame begins).</summary>
    void UpdateFrostLayers(Gpu gpu, Monitor m)
    {
        foreach (var f in m.Frost) f?.Flush(gpu);
    }

    bool FrostVisible(Monitor m)
    {
        foreach (var f in m.Frost) if (f is { Opacity: > 0.01f, Bmp: not null }) return true;
        return false;
    }

    void DrawFrostLayers(ID2D1DeviceContext3 ctx, Monitor m)
    {
        var b = m.Bounds;
        foreach (var f in m.Frost)
        {
            if (f is not { Opacity: > 0.01f, Bmp: not null }) continue;
            var r = f.Side switch
            {
                0 => new Rect(b.Left, b.Top, f.Len, f.Depth),
                1 => new Rect(b.Left, b.Bottom - f.Depth, f.Len, f.Depth),
                2 => new Rect(b.Left, b.Top, f.Depth, f.Len),
                _ => new Rect(b.Right - f.Depth, b.Top, f.Depth, f.Len),
            };
            ctx.DrawBitmap(f.Bmp, r, f.Opacity, BitmapInterpolationMode.Linear, null);
        }
    }

    void AddFrostSprites(Monitor m) { } // frost is drawn from its crystal layers (DrawFrostLayers)

    // ------------------------------------------------------------------ gusts

    public float GustX;          // extra sideways push right now, px/s at scale 1
    double _nextGust = 20, _gustT = -10;
    float _gustDir = 1, _gustPeak;

    void StepGusts(float dt)
    {
        if (GustKmh < 18 || Cur.Fog > 0.5f) { GustX = 0; return; }
        if (Now >= _nextGust)
        {
            _gustT = Now;
            _gustPeak = Math.Clamp((GustKmh - 12) * 7, 40, 420);
            _gustDir = WindX != 0 ? MathF.Sign(WindX) : (Rf() < 0.5f ? -1 : 1);
            _nextGust = Now + (14 + Rf() * 26) * Math.Clamp(30 / GustKmh, 0.35f, 1.5f);
        }
        double t = Now - _gustT;
        float env = t < 0 || t > 2.4 ? 0 : MathF.Sin((float)(t / 2.4) * MathF.PI);
        GustX = _gustDir * _gustPeak * env;
        if (env > 0.6f) BlowEdges(dt, env);
    }

    /// <summary>At the height of a gust, snow is blown off the windward end of edges and resting leaves lift.</summary>
    void BlowEdges(float dt, float env)
    {
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.PileTotal < 1 || w.H.Length == 0) continue;
            int n = w.H.Length;
            int cols = Math.Max(1, (int)(n * 0.15f * env * dt * 4));
            for (int k = 0; k < cols; k++)
            {
                int c = _gustDir > 0 ? _rnd.Next(n / 2) : n - 1 - _rnd.Next(n / 2);
                float take = MathF.Min(w.H[c], 2.5f * w.Scale);
                if (take < 0.6f) continue;
                w.H[c] -= take; w.PileTotal -= take;
                AddDebris(w.R.Left + (c + 0.5f) * w.ColW, w.R.Top - w.H[c] - 1, GustX * w.Scale * (0.6f + 0.6f * Rf()), -(20 + Rf() * 50) * w.Scale,
                    (1.6f + Rf() * 2) * w.Scale, false);
            }
            Kick(0.3);
        }
        for (int i = 0; i < _nLeaves; i++)
        {
            ref var l = ref _leaves[i];
            if (l.On == null || Rf() > dt * 2.5f * env) continue;
            l.On = null; l.Vx = GustX * 0.9f; l.Vy = -(40 + Rf() * 80); l.Spin = (Rf() - .5f) * 8;
        }
    }

    // ------------------------------------------------------------------ hail

    struct Hail { public float X, Y, Vx, Vy, Size, Bottom; public int Bounces, Mon; }
    Hail[] _hail = new Hail[128];
    int _nHail;
    float _hailAcc;

    void StepHail(float dt)
    {
        int target = 0;
        foreach (var m in Monitors) target += (int)(70 * Cur.Hail * RainAmt * m.Bounds.Width / 1920f);
        if (_nHail < target)
        {
            _hailAcc += target / 1.2f * dt;
            while (_hailAcc >= 1 && _nHail < target)
            {
                _hailAcc -= 1;
                if (_nHail == _hail.Length) Array.Resize(ref _hail, _hail.Length * 2);
                int mi = PickMonitor(); var m = Monitors[mi]; float s = m.Scale;
                float sx = m.Bounds.Left + Rf() * m.Bounds.Width, sy = m.Bounds.Top - 20 * s;
                if (RainSource(mi, out float cx, out float cy)) { sx = cx; sy = cy; }
                _hail[_nHail++] = new Hail { X = sx, Y = sy, Vx = (Rf() - .5f) * 40 * s, Vy = (650 + Rf() * 350) * s, Size = (3 + Rf() * 3.5f) * s, Bottom = m.Bounds.Bottom + 10, Mon = mi };
            }
        }
        else _hailAcc = 0;
        var surfs = _surf;
        for (int i = 0; i < _nHail; i++)
        {
            ref var h = ref _hail[i];
            float s = Monitors[Math.Min(h.Mon, Monitors.Count - 1)].Scale;
            float oy = h.Y;
            h.Vy += 1800 * s * dt;
            h.X += (h.Vx + GustX * 0.4f * s) * dt; h.Y += h.Vy * dt;
            bool dead = h.Y > h.Bottom;
            if (!dead && h.Vy > 0)
                foreach (var sf in surfs)
                {
                    if (h.X < sf.X0 || h.X >= sf.X1 || oy > sf.Y || h.Y < sf.Y) continue;
                    // Bounce: lose most of the speed, kick sideways; after two bounces it rolls off.
                    if (h.Bounces >= 2) break;
                    h.Y = sf.Y - 1; h.Vy = -h.Vy * (h.Bounces == 0 ? 0.38f : 0.25f); h.Vx += (Rf() - .5f) * 220 * s; h.Bounces++;
                    break;
                }
            if (dead) { _hail[i] = _hail[--_nHail]; i--; }
        }
    }

    void AddHailSprites(Monitor m)
    {
        var b = m.Bounds;
        for (int i = 0; i < _nHail; i++)
        {
            ref var h = ref _hail[i];
            if (h.X < b.Left - 10 || h.X > b.Right + 10 || h.Y < b.Top - 10 || h.Y > b.Bottom + 10) continue;
            float r = h.Size * 0.5f;
            _sprites.Add(Atlas.Bead, h.X - r, h.Y - r, h.X + r, h.Y + r, Pm(0.92f, 0.96f, 1f, 0.95f));
        }
    }

    // ------------------------------------------------------------------ moon

    float _moonPhaseDrawn = -2;

    /// <summary>Re-paints the moon sprite in the atlas when the phase has moved on (a few times a day).</summary>
    void UpdateMoonTexture(Gpu gpu)
    {
        if (MoonPhase < 0 || MathF.Abs(MoonPhase - _moonPhaseDrawn) < 0.004f) return;
        _moonPhaseDrawn = MoonPhase;
        gpu.PaintMoon(MoonPhase);
    }

    bool MoonVisible(out float alpha)
    {
        alpha = 0;
        if (MoonPhase < 0) return false;
        bool night = SunElev < -4 || ForceMoon;
        if (!night || (MoonAlt < 1 && !ForceMoon)) return false;
        float clouds = MathF.Min(1, Cur.Low + Cur.Mid * 0.8f + Cur.Fog);
        alpha = (1 - 0.85f * clouds) * MathF.Min(1, (-SunElev - 4) / 6 + (ForceMoon ? 1 : 0));
        return alpha > 0.02f;
    }

    void AddMoonSprites(Monitor m)
    {
        if (!MoonVisible(out float alpha)) return;
        var b = m.Bounds;
        float s = m.Scale;
        float alt = ForceMoon && MoonAlt < 10 ? 35 : MoonAlt;
        float az = ForceMoon && MoonAlt < 10 ? 160 : MoonAz;
        // Facing south: east on the left, west on the right; higher in the sky = higher on screen.
        float x = b.Left + b.Width * (0.5f + 0.42f * -MathF.Sin(az * MathF.PI / 180));
        float y = b.Top + b.Height * (0.06f + 0.28f * (1 - Math.Clamp(alt / 75f, 0, 1)));
        float r = 17 * s;
        float lit = 0.5f - 0.5f * MathF.Cos(MoonPhase * 2 * MathF.PI); // illuminated fraction
        float gr = r * 4.5f;
        _sprites.Add(Atlas.Flare, x - gr, y - gr, x + gr, y + gr, Pm(0.75f, 0.82f, 1f, 0.16f * lit * alpha));
        _sprites.Add(Atlas.Moon, x - r, y - r, x + r, y + r, Pm(1f, 0.98f, 0.92f, 0.92f * alpha));
    }

    // ------------------------------------------------------------------ aurora

    void AddAuroraSprites(Monitor m)
    {
        if (Cur.Aurora < 0.01f) return;
        var b = m.Bounds;
        float t = (float)Now;
        int n = 44;
        float step = b.Width / (float)n;
        for (int i = 0; i < n; i++)
        {
            float x = b.Left + (i + 0.5f) * step;
            // A slowly folding curtain: the base wanders up and down, the rays breathe.
            float baseY = b.Top + b.Height * (0.20f + 0.045f * MathF.Sin(x * 0.0035f + t * 0.12f) + 0.02f * MathF.Sin(x * 0.011f - t * 0.23f));
            float h = b.Height * (0.13f + 0.07f * (0.5f + 0.5f * MathF.Sin(x * 0.006f + t * 0.31f + i)));
            float pulse = 0.55f + 0.45f * MathF.Sin(t * 0.7f + i * 0.9f) * MathF.Sin(t * 0.23f + i * 0.37f);
            float k = Cur.Aurora * (0.10f + 0.12f * pulse);
            float w = step * 1.8f;
            _sprites.Add(Atlas.AuroraRay, x - w / 2, baseY - h, x + w / 2, baseY, Pm(0.35f, 1f, 0.58f, k));
            _sprites.Add(Atlas.AuroraRay, x - w / 2, baseY - h * 1.9f, x + w / 2, baseY - h * 0.75f, Pm(0.85f, 0.35f, 0.9f, k * 0.45f));
        }
    }

    // ------------------------------------------------------------------ smog

    void AddSmogSprites(Monitor m)
    {
        if (Cur.Smog < 0.01f) return;
        var b = m.Bounds;
        _sprites.Add(Atlas.Solid, b.Left, b.Top, b.Right, b.Bottom, Pm(0.76f, 0.68f, 0.52f, 0.07f * Cur.Smog));
        _sprites.Add(Atlas.Band, b.Left, b.Top, b.Right, b.Top + b.Height * 0.5f, Pm(0.72f, 0.64f, 0.48f, 0.08f * Cur.Smog));
    }

    // ------------------------------------------------------------------ frame needs

    int ExtrasFps(int maxFps)
    {
        if (_nHail > 0 || Cur.Hail > 0.01f) return maxFps;
        if (Cur.Aurora > 0.01f) return 15;
        if (_frostGrowing) return 20;   // crystals visibly growing
        if (FrostChanging) return 4;
        return 0;
    }
}

/// <summary>
/// One edge's frost (top, bottom, left or right): fern crystals growing in from the screen edge, deepest towards the
/// corners, drawn once into a cached band. Starts as a faint fringe and creeps inwards as the frost builds.
/// </summary>
public sealed class FrostLayer : IDisposable
{
    struct Branch { public Vector2 P; public float Ang, Left, Len0, W, A, NextSplit, Speed, Curl; public int Gen; }
    struct Seg { public Vector2 A, B; public float W, Al; }

    public readonly int Side;            // 0 top, 1 bottom, 2 left, 3 right
    public ID2D1Bitmap1? Bmp;
    public float Len, Depth;             // band size in px: along the edge, and how far in it can reach
    public float Opacity;
    readonly Random _r;
    readonly float _phase;
    readonly List<Branch> _live = new();
    readonly List<Seg> _pending = new();
    int _segs;
    float _grown, _seedAcc, _extent;
    bool _clear = true;

    public FrostLayer(int side, int seed) { Side = side; _r = new Random(seed); _phase = (float)(_r.NextDouble() * 100); }
    float Rf() => (float)_r.NextDouble();

    /// <summary>How far in the frost reaches at a point along the edge: deepest at the corners, a wavering band between.</summary>
    float DepthAt(float x)
    {
        float c = MathF.Min(x, Len - x) / Len;                  // 0 at a corner .. 0.5 mid-edge
        float corner = MathF.Max(0, 1 - c / 0.22f);
        float wave = 0.10f * MathF.Sin(x * 0.011f + _phase) + 0.06f * MathF.Sin(x * 0.037f + _phase * 2);
        return _extent * MathF.Max(0.05f, 0.28f + 0.72f * MathF.Pow(corner, 1.3f) + wave);
    }

    public bool Step(float dt, float frost, float w, float h, float speed)
    {
        float minDim = MathF.Min(w, h);
        Len = Side < 2 ? w : h;
        Depth = MathF.Ceiling(minDim * 0.30f);
        _extent = minDim * 0.30f * frost;
        // Subtle at first: faint while thin, fuller as it thickens. Fades as a whole when it warms up.
        float targetOp = frost > 0.005f ? 0.22f + 0.78f * MathF.Min(1, frost * 1.3f) : 0;
        if (frost + 0.02f < _grown) targetOp *= Math.Clamp(frost / MathF.Max(_grown, 0.01f), 0, 1);
        float op0 = Opacity;
        Opacity += (targetOp - Opacity) * MathF.Min(1, dt * 0.8f);
        if (MathF.Abs(Opacity - targetOp) < 0.003f) Opacity = targetOp;
        if (frost < 0.005f && Opacity < 0.01f) { if (_segs > 0 || _live.Count > 0) Reset(); return false; }
        if (frost < 0.005f) return Opacity != op0;
        _grown = MathF.Max(_grown, frost);

        // New needles keep starting along the edge (more near the corners), so the frost densifies as it grows.
        int budget = (int)(Len * 14);
        _seedAcc += dt * speed * (0.4f + 2.2f * frost) * (Len / 900f);
        while (_seedAcc >= 1 && _segs < budget && _live.Count < 140) { _seedAcc -= 1; Seed(); }

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var b = _live[i];
            float d = b.Speed * speed * dt;
            b.Ang += (Rf() - 0.5f) * (b.Gen == 0 ? 0.09f : 0.03f) + (b.Gen == 0 ? b.Curl * dt * speed : 0);
            var np = b.P + new Vector2(MathF.Cos(b.Ang), MathF.Sin(b.Ang)) * d;
            _pending.Add(new Seg { A = b.P, B = np, W = b.W, Al = b.A });
            _segs++;
            b.P = np; b.Left -= d; b.NextSplit -= d;
            if (b.NextSplit <= 0 && b.Gen < 2 && _segs < budget)
            {
                // Fern frost: short barbs at 60 degrees on both sides, shorter towards the tip; barbs carry tiny barbs.
                float tipward = Math.Clamp(b.Left / MathF.Max(1, b.Len0), 0.25f, 1);
                foreach (int s in new[] { -1, 1 })
                {
                    if (b.Gen == 1 && Rf() < 0.5f) continue;
                    float len = (b.Gen == 0 ? 5 + Rf() * 11 : 2 + Rf() * 4) * tipward;
                    _live.Add(new Branch
                    {
                        P = b.P, Ang = b.Ang + s * (MathF.PI / 3 + (Rf() - 0.5f) * 0.18f), Left = len, Len0 = len,
                        W = b.W * 0.6f, A = b.A * 0.8f, Gen = b.Gen + 1, Speed = b.Speed * 0.7f, NextSplit = 1.8f + Rf() * 1.6f,
                    });
                }
                b.NextSplit = b.Gen == 0 ? 2.6f + Rf() * 2.2f : 99;
            }
            bool dead = b.Left <= 0 || b.P.Y < 0 || b.P.Y > Depth || b.P.X < 0 || b.P.X > Len ||
                        (b.Gen == 0 && b.P.Y > DepthAt(b.P.X) * (0.9f + 0.2f * Rf()));
            if (dead) _live.RemoveAt(i); else _live[i] = b;
        }
        return _live.Count > 0 || _pending.Count > 0 || Opacity != op0;
    }

    void Seed()
    {
        // Pick a spot along the edge, favouring the corners; grow inwards, leaning towards the middle near the ends.
        float x;
        if (Rf() < 0.55f) { float c = MathF.Pow(Rf(), 2.2f) * Len * 0.25f; x = Rf() < 0.5f ? c : Len - c; }
        else x = Rf() * Len;
        float reach = DepthAt(x);
        if (reach < 3) return;
        float lean = (x < Len * 0.25f ? 1 : x > Len * 0.75f ? -1 : 0) * 0.45f;
        float ang = MathF.PI / 2 - lean + (Rf() - 0.5f) * 1.1f;
        float len = reach * (0.45f + 0.65f * Rf());
        _live.Add(new Branch
        {
            P = new Vector2(x, Rf() * 1.5f), Ang = ang, Left = len, Len0 = len, W = 0.9f + 0.5f * Rf(), A = 0.24f + 0.2f * Rf(),
            Gen = 0, Speed = 7 + Rf() * 6, NextSplit = 2 + Rf() * 3, Curl = (Rf() - 0.5f) * 0.035f,
        });
    }

    void Reset() { _live.Clear(); _pending.Clear(); _segs = 0; _grown = 0; _clear = true; }

    /// <summary>Draw pending segments into the band: a soft glow pass, then the crisp needle.</summary>
    public void Flush(Gpu gpu)
    {
        if (_pending.Count == 0 && !_clear) return;
        int bw = (int)MathF.Max(8, Side < 2 ? Len : Depth), bh = (int)MathF.Max(8, Side < 2 ? Depth : Len);
        if (Bmp == null || Bmp.PixelSize.Width != bw || Bmp.PixelSize.Height != bh)
        {
            Bmp?.Dispose();
            Bmp = gpu.Ctx.CreateBitmap(new Vortice.Mathematics.SizeI(bw, bh), IntPtr.Zero, 0,
                new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            _clear = true;
        }
        var ctx = gpu.Ctx;
        var oldAa = ctx.AntialiasMode;
        ctx.Target = Bmp;
        ctx.BeginDraw();
        if (_clear) { ctx.Clear(new Color4(0, 0, 0, 0)); _clear = false; }
        ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        // Edge space: x runs along the edge, y points into the screen. Map it onto this band.
        float D = Depth;
        ctx.Transform = Side switch
        {
            0 => Matrix3x2.Identity,
            1 => new Matrix3x2(1, 0, 0, -1, 0, D),
            2 => new Matrix3x2(0, 1, 1, 0, 0, 0),
            _ => new Matrix3x2(0, 1, -1, 0, D, 0),
        };
        var brush = gpu.FrostBrush;
        foreach (var s in _pending)
        {
            brush.Color = new Color4(0.80f, 0.88f, 1f, s.Al * 0.09f);
            ctx.DrawLine(s.A, s.B, brush, s.W * 3.0f, gpu.RoundStroke);
        }
        foreach (var s in _pending)
        {
            // A faint blue-grey under-stroke so the crystals still read over a white page.
            brush.Color = new Color4(0.50f, 0.60f, 0.74f, s.Al * 0.30f);
            ctx.DrawLine(s.A, s.B, brush, s.W * 1.7f, gpu.RoundStroke);
        }
        foreach (var s in _pending)
        {
            brush.Color = new Color4(0.95f, 0.975f, 1f, s.Al);
            ctx.DrawLine(s.A, s.B, brush, s.W, gpu.RoundStroke);
        }
        _pending.Clear();
        ctx.Transform = Matrix3x2.Identity;
        ctx.AntialiasMode = oldAa;
        ctx.EndDraw();
        ctx.Target = null;
    }

    public void Dispose() { Bmp?.Dispose(); Bmp = null; }
}
