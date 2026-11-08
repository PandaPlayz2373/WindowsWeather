using System.Numerics;
using Vortice.Mathematics;

namespace WindowsWeather;

// Tornado ("a tonado that actually moves your windows ... or mixes up things, like destroys things").
// A funnel drops out of the wall cloud, touches down on the taskbar and crosses the screen. Real windows it reaches
// are picked up: pulled toward the funnel, lifted, swung back and forth across it and shaken (bigger windows are
// heavier and move less), then dropped when it moves on. Snow piles, puddles and resting leaves are sucked off every
// edge it crosses and spun up the funnel. Every window glides back to exactly where it was a few seconds after the
// tornado has passed, and at once if the app pauses or quits, so the layout is only ever borrowed.
// Windows the user grabs mid-storm are let go for good. Maximized, minimized, topmost and tool windows are left alone.
public sealed partial class World
{
    bool _torOn;
    Monitor? _torMon;
    float _torX, _torDir = 1, _torPhase, _torAge, _torTouch, _torTop, _torGround;
    double _torNext, _torSuckAt;

    struct TPart { public float F, A, W, Size, Shade; public byte Kind; }
    readonly TPart[] _tp = new TPart[300];



    // ------------------------------------------------------------------ funnel geometry (f: 0 = cloud base, 1 = ground)

    float TorAxis(float f)
    {
        float s = _torMon!.Scale;
        return _torX + (1 - f) * -_torDir * 70 * s + MathF.Sin(_torPhase + f * 4.2f) * 38 * s * 4 * f * (1 - f);
    }
    float TorR(float f) => (24 + 200 * MathF.Pow(Math.Clamp(1 - f, 0, 1), 1.8f)) * _torMon!.Scale * Mod("Tornado.Size");
    float TorY(float f) => _torTop + f * (_torGround - _torTop);

    static float Ss(float k) { k = Math.Clamp(k, 0, 1); return k * k * (3 - 2 * k); }

    /// <summary>How hard the funnel pulls at a point (0..1), the horizontal offset from its axis and the height there.</summary>
    float TorInfluence(float x, float y, out float dx, out float f)
    {
        dx = 0; f = 0;
        if (!_torOn || _torMon == null) return 0;
        f = (y - _torTop) / MathF.Max(1, _torGround - _torTop);
        if (f < -0.12f || f > 1.04f || f > _torTouch + 0.05f) return 0;
        float fc = Math.Clamp(f, 0, 1);
        dx = x - TorAxis(fc);
        float r = TorR(fc) * 2.2f + 40 * _torMon.Scale;
        return Ss(1 - MathF.Abs(dx) / r) * Cur.Tornado * MathF.Min(1.5f, Mod("Tornado.Power"));
    }

    /// <summary>The same for a window: by its nearest side to the funnel, at the height of its top edge.</summary>
    float WindowInfluence(float l, float t, float r, float b, out float dx)
    {
        dx = 0;
        if (!_torOn || _torMon == null || b < _torTop || t > _torGround) return 0;
        float f = (Math.Clamp((t + b) * 0.5f, _torTop, _torGround) - _torTop) / MathF.Max(1, _torGround - _torTop);
        if (f > _torTouch) return 0;
        float ax = TorAxis(f);
        float near = ax < l ? l - ax : ax > r ? ax - r : 0;
        dx = (l + r) * 0.5f - ax;
        float rad = TorR(f) * 1.3f + 30 * _torMon.Scale;
        return Ss(1 - near / rad) * Cur.Tornado * MathF.Min(1.5f, Mod("Tornado.Power"));
    }

    /// <summary>Pull a loose particle (leaf, snow chunk, water) into the funnel: spun across it, lifted, flung out the top.</summary>
    void TornadoPull(float x, float y, float s, float dt, float lift, ref float vx, ref float vy)
    {
        float k = TorInfluence(x, y, out float dx, out float f);
        if (k <= 0) return;
        vx += (-dx * 16f * k + (Rf() - 0.5f) * 900 * s * k) * dt;
        vy += (-lift * k + (Rf() - 0.5f) * 600 * s * k) * dt;
        if (f < 0.1f) vx += (dx < 0 ? -1 : 1) * 600 * s * dt;
    }

    // ------------------------------------------------------------------ life cycle

    void StepTornado(float dt)
    {
        if (!_torOn && Cur.Tornado > 0.4f && Now >= _torNext && Monitors.Count > 0) StartTornado(Monitors[PickMonitor()]);
        if (_torOn) MoveTornado(dt);
    }

    public void TornadoNow()
    {
        if (Monitors.Count == 0) return;
        _torNext = 0;
        if (!_torOn) StartTornado(Monitors[PickMonitor()]);
        Kick(1);
    }

    void StartTornado(Monitor m)
    {
        _torMon = m; _torOn = true; _torAge = 0; _torTouch = 0;
        _torDir = Rf() < 0.5f ? 1 : -1;
        // Forms just inside the screen edge, so the touchdown is in view and it still crosses everything on the taskbar
        // from one end to the other (it should also take "the network and time and calendar and all
        // that down there").
        _torX = _torDir > 0 ? m.Bounds.Left + m.Bounds.Width * 0.025f : m.Bounds.Right - m.Bounds.Width * 0.025f;
        _torPhase = Rf() * 6.28f;
        _torTop = m.Bounds.Top + m.Bounds.Height * 0.2f;
        _torGround = GroundY(m);
        for (int i = 0; i < _tp.Length; i++) _tp[i] = NewPart(i < 230 ? (byte)0 : i < 270 ? (byte)1 : (byte)2, Rf());
    }

    TPart NewPart(byte kind, float f) => kind switch
    {
        0 => new TPart { Kind = 0, F = MathF.Pow(f, 0.8f), A = Rf() * 6.283f, W = 2.2f + 3f * f * (0.8f + 0.4f * Rf()), Size = 0.8f + 0.5f * Rf(), Shade = Rf() },
        1 => new TPart { Kind = 1, F = 1, A = Rf() * 6.283f, W = 1.2f + Rf(), Size = 0.7f + 0.8f * Rf(), Shade = f },
        _ => new TPart { Kind = 2, F = 0.25f + 0.75f * f, A = Rf() * 6.283f, W = 3.5f + 3f * Rf(), Size = 5 + 9 * Rf(), Shade = Rf() },
    };

    float GroundY(Monitor m)
    {
        var b = m.Bounds;
        foreach (var w in Tracker.Z)
            if (w.IsTaskbar && w.R.Left < b.Right && w.R.Right > b.Left && w.R.Top > b.Top + b.Height / 2) return w.R.Top;
        return b.Bottom;
    }

    void MoveTornado(float dt)
    {
        var m = _torMon!;
        float s = m.Scale;
        _torAge += dt; _torPhase += dt * 0.9f;
        bool leaving = Cur.Tornado < 0.3f;
        _torTouch = leaving ? MathF.Max(0, _torTouch - dt / 2.5f) : MathF.Min(1, _torTouch + dt / 3.5f);
        // Creeps while it touches down, then crosses at a walking pace that surges and stalls.
        float speed = (75 + 40 * MathF.Sin(_torAge * 0.37f)) * s * Ss(_torTouch * 1.8f - 0.8f) * Mod("Tornado.Speed"); // stands still until it is nearly down
        _torX += _torDir * speed * dt;
        _torTop = m.Bounds.Top + m.Bounds.Height * 0.2f;
        _torGround = GroundY(m);
        bool off = _torDir > 0 ? _torX > m.Bounds.Right + 280 * s : _torX < m.Bounds.Left - 280 * s;
        if (off || (leaving && _torTouch <= 0)) { _torOn = false; _torNext = Now + 5 + Rf() * 6; return; }

        for (int i = 0; i < _tp.Length; i++)
        {
            ref var p = ref _tp[i];
            switch (p.Kind)
            {
                case 0:
                    p.A += p.W * dt; p.F -= dt * 0.05f;
                    if (p.F < 0) p.F += 1;
                    break;
                case 1:
                    p.A += p.W * dt; p.Shade += dt * 0.35f;
                    if (p.Shade > 1) p = NewPart(1, 0);
                    break;
                default:
                    p.A += p.W * dt; p.F -= dt * 0.09f;
                    if (p.F < 0.04f) p = NewPart(2, 1);
                    break;
            }
        }
        if (Now >= _torSuckAt) { _torSuckAt = Now + 0.06; SuckEdges(s); TornadoTakeIcons(s); }
    }

    // Taskbar icons the funnel touches down on are ripped off and spun up it ("i want tornado to do
    // the same thing with the taking icons and things from your task bar"); they fly home once it has gone.
    void TornadoTakeIcons(float s)
    {
        if (_torTouch < 0.75f || _torMon == null) return;
        var mb = _torMon.Bounds;
        Tracked? bar = null;
        foreach (var w in Tracker.Z)
            if (w.IsTaskbar && w.R.Left < mb.Right && w.R.Right > mb.Left && w.R.Top > mb.Top + mb.Height / 2) { bar = w; break; }
        if (bar == null || bar.R.Top < _torGround - 2 || bar.R.Top > _torGround + 2) return; // only the taskbar it stands on
        float ax = TorAxis(1), reach = TorR(1) * 2.5f + 30 * s;
        if (ax + reach < bar.R.Left || ax - reach > bar.R.Right) return;
        if (_tiles.Count == 0)
        {
            if (Now - _tbFailAt < 2) return;
            if (!CaptureTaskbar(bar)) { _tbFailAt = Now; return; }
        }
        foreach (var t in _tiles)
        {
            if (t.Caught) continue;
            float cx = t.X + t.W * 0.5f, cy = t.Y + t.H * 0.5f;
            if (t.State == 1 && cy < _torGround - 40 * s) continue; // already in the air: thrown ones are not caught again
            if (MathF.Abs(cx - ax) > reach) continue;
            t.State = 1; t.Delay = 0; t.Spin = (Rf() - 0.5f) * 10;
            // Carried a long way up before it is thrown: most of the way to the cloud.
            t.Caught = true; t.CF = 1; t.CA = Rf() * MathF.Tau; t.CW = (3.2f + 2.2f * Rf()) * (Rf() < 0.5f ? 1 : -1);
            t.Rise = 0.16f + 0.1f * Rf(); t.ThrowF = 0.08f + 0.25f * Rf();
        }
        _tbHomeAt = -1;
        Kick(1);
    }

    // Snow and water on any edge the funnel crosses are torn off into it.
    void SuckEdges(float s)
    {
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0 || (w.PileTotal < 0.3f && w.WaterTotal < 0.3f && w.Beads.Count == 0)) continue;
            float f = (w.R.Top - _torTop) / MathF.Max(1, _torGround - _torTop);
            if (f < 0 || f > 1.02f || f > _torTouch) continue;
            float fc = Math.Clamp(f, 0, 1), ax = TorAxis(fc), r = TorR(fc) * 1.25f + 20 * s;
            if (ax + r < w.R.Left || ax - r > w.R.Right) continue;
            int c0 = Math.Max(0, (int)((ax - r - w.R.Left) / w.ColW)), c1 = (int)((ax + r - w.R.Left) / w.ColW);
            bool hit = false;
            for (int c = c0; c <= Math.Min(w.H.Length - 1, c1); c++)
            {
                if (w.H[c] < 0.3f || !InSpans(w, c)) continue;
                float cx = w.R.Left + (c + 0.5f) * w.ColW;
                if (_rnd.Next(3) == 0) AddDebris(cx, w.R.Top - w.H[c] * Rf(), (Rf() - 0.5f) * 200 * s, -(250 + 300 * Rf()) * s, (2 + Rf() * 3) * w.Scale, false);
                w.H[c] *= 0.4f;
                hit = true;
            }
            for (int c = c0; c <= Math.Min(w.Wt.Length - 1, c1); c++)
            {
                if (w.Wt[c] < 0.2f) continue;
                float cx = w.R.Left + (c + 0.5f) * w.ColW;
                if (_rnd.Next(3) == 0) AddDebris(cx, w.R.Top - 2, (Rf() - 0.5f) * 200 * s, -(250 + 300 * Rf()) * s, (1.6f + Rf() * 1.6f) * w.Scale, true);
                w.WaterTotal -= w.Wt[c] * 0.6f;
                w.Wt[c] *= 0.4f;
                hit = true;
            }
            for (int i = w.Beads.Count - 1; i >= 0; i--)
            {
                float bx = w.R.Left + w.Beads[i];
                if (bx < ax - r || bx > ax + r) continue;
                AddDebris(bx, w.R.Top - 3, 0, -300 * s, 3 * w.Scale, true);
                w.Beads.RemoveAt(i);
                hit = true;
            }
            if (hit) { w.WaterTotal = MathF.Max(0, w.WaterTotal); RecountPile(w); }
        }
    }

    // ------------------------------------------------------------------ drawing

    void AddTornadoSprites(Monitor m)
    {
        if (!_torOn || _torMon != m) return;
        float s = m.Scale, a0 = MathF.Min(1, Cur.Tornado * 1.2f);
        for (int pass = 0; pass < 2; pass++)
        {
            bool frontPass = pass == 1;
            for (int i = 0; i < _tp.Length; i++)
            {
                var p = _tp[i];
                float sinA = MathF.Sin(p.A), cosA = MathF.Cos(p.A);
                if ((sinA > 0) != frontPass) continue;
                switch (p.Kind)
                {
                    case 0:
                    {
                        if (p.F > _torTouch) continue;
                        // A third of the puffs make a tight, dark core; the rest the ragged outer wall.
                        bool core = i % 3 == 0;
                        float r = TorR(p.F), x = TorAxis(p.F) + cosA * r * (core ? 0.3f : 0.78f), y = TorY(p.F) + sinA * r * 0.10f;
                        float w = (r * (core ? 0.8f : 0.9f) + 16 * s) * p.Size, h = w * 0.85f;
                        float tip = Math.Clamp((_torTouch - p.F) / 0.08f, 0, 1);
                        float sh = core ? 0.22f + p.Shade * 0.05f : (frontPass ? 0.30f : 0.40f) + p.Shade * 0.07f;
                        float al = (core ? 0.42f : frontPass ? 0.36f : 0.26f) * a0 * tip;
                        _sprites.Add(p.Shade < 0.5f ? Atlas.Puff : Atlas.Puff2, x - w / 2, y - h / 2, x + w / 2, y + h / 2, Pm(sh, sh * 1.02f, sh * 1.08f, al));
                        break;
                    }
                    case 1:
                    {
                        if (_torTouch < 0.9f) continue;
                        float age = p.Shade, r0 = TorR(1);
                        float r = r0 * (1.2f + age * 3.5f) * p.Size;
                        float x = TorAxis(1) + cosA * r, y = _torGround - age * 70 * s + sinA * r * 0.15f;
                        float w = (40 + age * 90) * s * p.Size, h = w * 0.75f;
                        float al = 0.30f * MathF.Sin(age * MathF.PI) * a0 * (_torTouch - 0.9f) * 10;
                        _sprites.Add(Atlas.Puff, x - w / 2, y - h / 2, x + w / 2, y + h / 2, Pm(0.44f, 0.39f, 0.33f, al));
                        break;
                    }
                    default:
                    {
                        if (p.F > _torTouch) continue;
                        float r = TorR(p.F) * 1.1f, x = TorAxis(p.F) + cosA * r, y = TorY(p.F) + sinA * r * 0.12f;
                        float z = p.Size * s * (frontPass ? 1.1f : 0.8f);
                        var src = p.Shade < 0.35f ? Atlas.Leaf : p.Shade < 0.7f ? Atlas.Leaf2 : Atlas.Dot;
                        var xf = Matrix3x2.CreateRotation(p.A * 3, new Vector2(x, y));
                        _sprites.AddRotated(src, x - z, y - z, x + z, y + z, Pm(0.20f, 0.17f, 0.14f, (frontPass ? 0.85f : 0.45f) * a0), xf);
                        break;
                    }
                }
            }
        }
    }
}
