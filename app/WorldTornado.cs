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
    double _torNext, _grabScanAt, _torSuckAt;

    struct TPart { public float F, A, W, Size, Shade; public byte Kind; }
    readonly TPart[] _tp = new TPart[300];

    sealed class Grab
    {
        public nint H; public Tracked? T; public Monitor M = null!;
        public int OX, OY, LX, LY, Wd, Ht;
        public float Ox, Oy, Vx, Vy, Mass, Calm, Held, Immune, ThrowAt;
        public bool Home;
    }
    readonly List<Grab> _grabs = new();

    /// <summary>Testing: only windows whose title starts with this are picked up (null = any window).</summary>
    public string? GrabOnlyTitle;

    const uint MoveFlags = 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000; // NOSIZE NOZORDER NOACTIVATE NOOWNERZORDER ASYNCWINDOWPOS

    public bool TornadoBusy => _torOn || _grabs.Count > 0;

    // ------------------------------------------------------------------ funnel geometry (f: 0 = cloud base, 1 = ground)

    float TorAxis(float f)
    {
        float s = _torMon!.Scale;
        return _torX + (1 - f) * -_torDir * 70 * s + MathF.Sin(_torPhase + f * 4.2f) * 38 * s * 4 * f * (1 - f);
    }
    float TorR(float f) => (24 + 200 * MathF.Pow(Math.Clamp(1 - f, 0, 1), 1.8f)) * _torMon!.Scale;
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
        return Ss(1 - MathF.Abs(dx) / r) * Cur.Tornado;
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
        return Ss(1 - near / rad) * Cur.Tornado;
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
        StepGrabs(dt);
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
        // Forms a little inside the screen edge, so the touchdown is in view, then crosses.
        _torX = _torDir > 0 ? m.Bounds.Left + m.Bounds.Width * 0.14f : m.Bounds.Right - m.Bounds.Width * 0.14f;
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
        float speed = (75 + 40 * MathF.Sin(_torAge * 0.37f)) * s * Ss(_torTouch * 1.3f - 0.2f);
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
        if (Now >= _torSuckAt) { _torSuckAt = Now + 0.06; SuckEdges(s); }
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

    // ------------------------------------------------------------------ real windows

    unsafe void ScanGrabs()
    {
        foreach (var t in Tracker.Z)
        {
            if (t.IsTaskbar || t.IsToast || t.Maximized || t.R.Width < 120 || t.R.Height < 80) continue;
            bool have = false;
            foreach (var g in _grabs) if (g.H == t.Hwnd) { have = true; break; }
            if (have) continue;
            if (WindowInfluence(t.R.Left, t.R.Top, t.R.Right, t.R.Bottom, out _) < 0.25f) continue;
            if (Native.IsIconic(t.Hwnd) != 0 || Native.IsZoomed(t.Hwnd) != 0) continue;
            long ex = Native.GetWindowLongPtrW(t.Hwnd, -20);
            if ((ex & 0x8) != 0 || (ex & 0x80) != 0) continue; // topmost and tool windows stay put
            if (GrabOnlyTitle != null && !Native.WindowText(t.Hwnd).StartsWith(GrabOnlyTitle)) continue;
            RECT wr;
            if (Native.GetWindowRect(t.Hwnd, &wr) == 0) continue;
            float area = (float)wr.Width * wr.Height;
            _grabs.Add(new Grab
            {
                H = t.Hwnd, T = t, M = _torMon!, OX = wr.Left, OY = wr.Top, LX = wr.Left, LY = wr.Top, Wd = wr.Width, Ht = wr.Height,
                Mass = 1 + area / 350000f, ThrowAt = 3.5f + Rf() * 2.5f,
            });
        }
    }

    void StepGrabs(float dt)
    {
        if (_torOn && _torTouch > 0.45f && Now >= _grabScanAt) { _grabScanAt = Now + 0.1; ScanGrabs(); }
        if (_grabs.Count == 0) return;
        uint ms = (uint)(App.Seconds * 1000);
        for (int i = _grabs.Count - 1; i >= 0; i--)
        {
            var g = _grabs[i];
            // Closed, minimized, maximized or picked up by the user: it is theirs again, wherever it is.
            if (Native.IsWindow(g.H) == 0 || Native.IsIconic(g.H) != 0 || Native.IsZoomed(g.H) != 0 || Tracker.Dragging == g.H) { _grabs.RemoveAt(i); continue; }
            var b = g.M.Bounds;
            float s = g.M.Scale;
            float l = g.OX + g.Ox, t = g.OY + g.Oy;
            float dx = 0;
            g.Immune -= dt;
            float k = _torMon == g.M && g.Immune <= 0 ? WindowInfluence(l, t, l + g.Wd, t + g.Ht, out dx) : 0;
            if (k > 0.02f)
            {
                g.Calm = 0; g.Home = false;
                float inv = 1 / g.Mass;
                // Lifted toward about a third of the way down the funnel, pushed down if it rides higher than that.
                float fy = ((t + g.Ht * 0.5f) - _torTop) / MathF.Max(1, _torGround - _torTop);
                float lift = Math.Clamp((fy - 0.32f) * 2.6f, -0.7f, 1f);
                g.Vx += (-dx * 14f * k + (Rf() - 0.5f) * 1500 * s * k) * inv * dt;
                g.Vy += (-520 * s * k * lift + (Rf() - 0.5f) * 1100 * s * k) * inv * dt;
                if (k > 0.3f) g.Held += dt;
                // Held a few seconds, then thrown out of the side of the funnel.
                if (g.Held > g.ThrowAt)
                {
                    float side = dx == 0 ? -_torDir : MathF.Sign(dx);
                    if (side == _torDir) side = Rf() < 0.6f ? -_torDir : _torDir;
                    g.Vx = side * (900 + 500 * Rf()) * s / MathF.Sqrt(g.Mass);
                    g.Vy = -(200 + 250 * Rf()) * s / MathF.Sqrt(g.Mass);
                    g.Immune = 2.5f; g.Held = 0; g.ThrowAt = 3.5f + Rf() * 2.5f;
                }
            }
            else
            {
                g.Calm += dt;
                if (g.Oy < 0) g.Vy += 900 * s * dt; // dropped: falls back to its own height
                else if (g.Vy > 0) { g.Oy = MathF.Min(g.Oy, 0); g.Vy *= -0.25f; }
                if (g.Calm > 3f) g.Home = true;
            }
            if (g.Home)
            {
                float e = MathF.Exp(-dt * 3.5f);
                g.Ox *= e; g.Oy *= e; g.Vx = g.Vy = 0;
                if (MathF.Abs(g.Ox) < 1.5f && MathF.Abs(g.Oy) < 1.5f) { g.Ox = g.Oy = 0; }
            }
            else
            {
                float damp = MathF.Exp(-dt * 1.8f);
                g.Vx = Math.Clamp(g.Vx * damp, -1600 * s, 1600 * s);
                g.Vy = Math.Clamp(g.Vy * damp, -1600 * s, 1600 * s);
                g.Ox += g.Vx * dt; g.Oy += g.Vy * dt;
                // Keep the title bar on the screen: never above the top, never lost off a side or under the taskbar.
                float x = g.OX + g.Ox, y = g.OY + g.Oy;
                float minX = b.Left - g.Wd * 0.5f, maxX = b.Right - g.Wd * 0.5f, minY = b.Top, maxY = _torMon == g.M ? _torGround - 90 * s : b.Bottom - 120 * s;
                if (x < minX) { g.Ox = minX - g.OX; g.Vx = MathF.Abs(g.Vx) * 0.3f; }
                if (x > maxX) { g.Ox = maxX - g.OX; g.Vx = -MathF.Abs(g.Vx) * 0.3f; }
                if (y < minY) { g.Oy = minY - g.OY; g.Vy = MathF.Abs(g.Vy) * 0.3f; }
                if (y > MathF.Max(minY, maxY)) { g.Oy = MathF.Max(minY, maxY) - g.OY; g.Vy = -MathF.Abs(g.Vy) * 0.3f; }
            }
            int nx = g.OX + (int)MathF.Round(g.Ox), ny = g.OY + (int)MathF.Round(g.Oy);
            if (nx != g.LX || ny != g.LY)
            {
                Native.SetWindowPos(g.H, 0, nx, ny, 0, 0, MoveFlags);
                g.LX = nx; g.LY = ny;
                if (g.T != null && Tracker.ByHwnd.ContainsKey(g.H)) Tracker.UpdateRect(g.T, ms);
            }
            if (g.Home && g.Ox == 0 && g.Oy == 0) _grabs.RemoveAt(i);
        }
        Kick(0.2);
    }

    /// <summary>Put every borrowed window back where it was, right now (pausing, quitting).</summary>
    public void RestoreWindows()
    {
        foreach (var g in _grabs)
            if (Native.IsWindow(g.H) != 0 && Native.IsIconic(g.H) == 0 && Native.IsZoomed(g.H) == 0 && (g.LX != g.OX || g.LY != g.OY))
                Native.SetWindowPos(g.H, 0, g.OX, g.OY, 0, 0, MoveFlags & ~0x4000u);
        _grabs.Clear();
        _torOn = false;
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
