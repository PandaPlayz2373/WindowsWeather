using System.Numerics;
using Vortice.Mathematics;

namespace WindowsWeather;

// Snowballs ("in snow mode have snowballs generate and roll down tilted objects. or i can grab a
// snowball or clump on the ground and drag it through other snow and it becomes a bigger snowball the more i drag it
// and move it through snow"; option 1: click the snowball itself).
//   - Clumps form now and then on deep enough snow piles. Some sit; some start rolling toward the nearer end.
//   - A ball on a pile rolls downhill on the pile's slope (the ends taper), picks up the snow it rolls over and grows,
//     and falls off the end of the edge onto whatever is below: another window, the taskbar, or off the screen.
//   - Click a ball (each one has a small invisible handle window, so the click never reaches the app underneath) and
//     drag it: it follows the cursor, grows while it is dragged through snow, and is thrown with the cursor's speed
//     when let go. A big ball dropped hard bursts.
//   - Resting balls melt away when it stops snowing.
public sealed partial class World
{
    public sealed class Snowball
    {
        public int Id;
        public float X, Y, Vx, Vy, R, Ang, Rest, Off; // Off: x along the edge it is on (it moves with the window)
        public Tracked? On;        // rolling or resting on this edge (null = in the air or held)
        public bool Held;
        public Monitor M = null!;
    }
    readonly List<Snowball> _balls = new();
    int _ballIds;
    double _nextBall = 6;
    Snowball? _held;
    float _heldGrab;               // how much snow the held ball has gathered this drag (for the log)
    const int MaxBalls = 8;

    /// <summary>The balls, for the app's grab handles (screen px).</summary>
    public IReadOnlyList<Snowball> Balls => _balls;
    public bool HoldingBall => _held != null;
    bool BallsMoving { get { foreach (var b in _balls) if (b.Held || b.On == null || MathF.Abs(b.Vx) > 2) return true; return false; } }

    float PileTopAt(Tracked w, float x, out int col)
    {
        col = (int)((x - w.R.Left) / w.ColW);
        float h = col >= 0 && col < w.H.Length ? w.H[col] : 0;
        return w.R.Top - h;
    }

    void StepSnowballs(float dt)
    {
        // A clump forms now and then where the snow is deep enough.
        if (Now >= _nextBall)
        {
            _nextBall = Now + (7 + 9 * Rf()) / MathF.Max(0.2f, Mod("Snow.Snowballs"));
            if (_balls.Count < MaxBalls && Cur.Snow > 0.2f && Mod("Snow.Snowballs") > 0.15f) SpawnClump();
        }
        var surfs = _surf;
        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var b = _balls[i];
            // The ball belongs to whichever screen it is over (dragged, thrown or rolled across, or the monitors were
            // rebuilt): that overlay draws it and its scale applies. Below every screen it keeps the last one.
            var mm = Tracker.MonitorAt(Monitors, (int)b.X, (int)b.Y);
            if (mm != null) b.M = mm;
            else if (!Monitors.Contains(b.M))
            {
                if (Monitors.Count == 0) { if (_held == b) _held = null; _balls.RemoveAt(i); continue; }
                b.M = Monitors[0];
            }
            float s = b.M.Scale;
            if (b.Held)
            {
                // Follows the cursor (a little behind, so it feels like it has weight).
                float tx = CursorX, ty = CursorY;
                float k = MathF.Min(1, dt * 22);
                float nx = b.X + (tx - b.X) * k, ny = b.Y + (ty - b.Y) * k;
                b.Vx = (nx - b.X) / MathF.Max(dt, 0.001f); b.Vy = (ny - b.Y) / MathF.Max(dt, 0.001f);
                b.Ang += (nx - b.X) / MathF.Max(1, b.R);
                GatherAlong(b, b.X, nx, ny, held: true);
                b.X = nx; b.Y = ny;
                continue;
            }
            if (b.On != null)
            {
                var w = b.On;
                b.X = w.R.Left + b.Off;
                bool gone = !Tracker.Z.Contains(w) || !w.Eligible || w.Spans.Count == 0;
                int c0 = (int)((b.X - w.R.Left) / w.ColW);
                bool offEdge = b.X < w.R.Left || b.X > w.R.Right || !InSpans(w, Math.Clamp(c0, 0, Math.Max(0, w.H.Length - 1)));
                if (gone || offEdge) { b.On = null; b.Vy = 0; continue; }
                // Roll downhill on the pile's slope; snow slows it more than a bare edge.
                float hl = PileTopAt(w, b.X - b.R * 0.6f, out _), hr = PileTopAt(w, b.X + b.R * 0.6f, out int col);
                float slope = (hr - hl) / MathF.Max(1, b.R * 1.2f);          // + when the right side is lower
                b.Vx += Math.Clamp(slope, -1.5f, 1.5f) * 900 * s * dt;
                bool onSnow = col >= 0 && col < w.H.Length && w.H[col] > 0.6f;
                b.Vx *= MathF.Exp(-dt * (onSnow ? 1.1f : 0.5f));
                float ox = b.X;
                b.X += b.Vx * dt;
                b.Ang += (b.X - ox) / MathF.Max(1, b.R);
                if (MathF.Abs(b.X - ox) > 0.05f) GatherAlong(b, ox, b.X, w.R.Top, held: false);
                b.Y = PileTopAt(w, b.X, out _) - b.R * 0.85f;
                if (MathF.Abs(slope) < 0.2f && MathF.Abs(b.Vx) < 3 * s) { b.Vx = 0; b.Rest += dt; } else b.Rest = 0;
                b.Off = b.X - w.R.Left;
            }
            else
            {
                // In the air.
                float oy = b.Y;
                b.Vy += 1800 * s * dt;
                b.Vx *= MathF.Exp(-dt * 0.15f);
                b.X += b.Vx * dt; b.Y += b.Vy * dt;
                b.Ang += b.Vx * dt / MathF.Max(1, b.R) * 0.5f;
                if (b.Vy > 0)
                    foreach (var sf in surfs)
                    {
                        if (b.X < sf.X0 || b.X >= sf.X1) continue;
                        float top = PileTopAt(sf.W, b.X, out _);
                        if (oy + b.R * 0.85f > top + 1 || b.Y + b.R * 0.85f < top) continue;
                        float speed = b.Vy;
                        if (speed > 1300 * s && b.R > 14 * s) { Burst(b); _balls.RemoveAt(i); goto next; } // dropped hard: bursts
                        b.On = sf.W; b.Off = b.X - sf.W.R.Left; b.Y = top - b.R * 0.85f; b.Vy = 0;
                        if (speed > 300 * s) for (int k = 0; k < 4; k++) AddDebris(b.X, top - 2, (Rf() - 0.5f) * 160 * s, -(60 + 100 * Rf()) * s, (1.8f + Rf() * 2) * s, false);
                        break;
                    }
                if (b.Y > b.M.Bounds.Bottom + b.R * 2 + 40) { _balls.RemoveAt(i); continue; }
            }
            // No more snow: resting balls slowly melt.
            if (Cur.Snow < 0.05f && !b.Held && b.Rest > 1) b.R -= dt * 0.25f * s;
            if (b.Rest > 120) b.R -= dt * 0.1f * s;
            if (b.R < 2.5f * s) _balls.RemoveAt(i);
            next:;
        }
    }

    void SpawnClump()
    {
        // A random deep spot on a visible pile.
        var cand = new List<(Tracked w, int c)>();
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.H.Length == 0 || w.PileTotal < 20) continue;
            for (int c = 4; c < w.H.Length - 4; c += 3) if (w.H[c] > 4.5f * w.Scale && InSpans(w, c)) cand.Add((w, c));
        }
        if (cand.Count == 0) return;
        var (tw, tc) = cand[_rnd.Next(cand.Count)];
        var m = Tracker.MonitorAt(Monitors, (int)(tw.R.Left + tc * tw.ColW), tw.R.Top) ?? (Monitors.Count > 0 ? Monitors[0] : null);
        if (m == null) return;
        float s = m.Scale, x = tw.R.Left + (tc + 0.5f) * tw.ColW;
        var b = new Snowball { Id = ++_ballIds, X = x, Off = x - tw.R.Left, R = (4.5f + 2 * Rf()) * s, On = tw, M = m };
        b.Y = PileTopAt(tw, x, out _) - b.R * 0.85f;
        tw.H[tc] *= 0.6f; RecountPile(tw);
        // Half of them set off rolling toward the nearer end, where the pile slopes away.
        if (Rf() < 0.5f) b.Vx = (x - tw.R.Left < tw.R.Right - x ? -1 : 1) * (50 + 90 * Rf()) * s;
        _balls.Add(b);
        Kick(1);
    }

    // Picks up the snow under the ball between x0 and x1 and grows it (area adds up).
    void GatherAlong(Snowball b, float x0, float x1, float y, bool held)
    {
        float s = b.M.Scale;
        float lo = MathF.Min(x0, x1) - b.R * 0.5f, hi = MathF.Max(x0, x1) + b.R * 0.5f;
        float gained = 0;
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.H.Length == 0 || w.PileTotal < 0.5f) continue;
            if (hi < w.R.Left || lo > w.R.Right) continue;
            // Held: only while the ball is down in the snow on this edge.
            if (held && (y + b.R * 0.9f < w.R.Top - 16 * w.Scale * PileScale || y - b.R > w.R.Top + 4)) continue;
            if (!held && b.On != w) continue;
            int c0 = Math.Max(0, (int)((lo - w.R.Left) / w.ColW)), c1 = Math.Min(w.H.Length - 1, (int)((hi - w.R.Left) / w.ColW));
            bool hit = false;
            for (int c = c0; c <= c1; c++)
            {
                if (w.H[c] < 0.4f || !InSpans(w, c)) continue;
                float take = w.H[c] * (held ? 0.7f : 0.35f);
                w.H[c] -= take;
                gained += take * w.ColW;
                hit = true;
            }
            if (hit) RecountPile(w);
        }
        if (gained <= 0) return;
        // Only a ball you drag gets big; ones rolling on their own stay small.
        float maxR = held ? 60 * s * Mod("Snow.Snowballs") + 20 * s : 11 * s * MathF.Max(0.5f, Mod("Snow.Snowballs"));
        b.R = MathF.Max(b.R, MathF.Min(maxR, MathF.Sqrt(b.R * b.R + gained * 0.55f / MathF.PI))); // never shrinks a big one
        if (held) _heldGrab += gained;
    }

    void Burst(Snowball b)
    {
        float s = b.M.Scale;
        int n = Math.Min(30, 6 + (int)(b.R / s));
        for (int k = 0; k < n; k++)
            AddDebris(b.X + (Rf() - 0.5f) * b.R, b.Y + (Rf() - 0.5f) * b.R, (Rf() - 0.5f) * 420 * s, -(100 + 300 * Rf()) * s, (2 + Rf() * 3.5f) * s, false);
        if (_held == b) _held = null;
    }

    /// <summary>After the monitors are rebuilt: each ball to the screen it is over, the rest dropped.</summary>
    public void RemapBalls()
    {
        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var m = Tracker.MonitorAt(Monitors, (int)_balls[i].X, (int)_balls[i].Y);
            if (m == null) { if (_held == _balls[i]) _held = null; _balls.RemoveAt(i); }
            else _balls[i].M = m;
        }
    }

    // A window flung or resized throws the balls sitting on it.
    void BallsOffWindow(Tracked w, float vx, float vy)
    {
        foreach (var b in _balls)
            if (b.On == w) { b.On = null; b.Vx = vx * 0.6f; b.Vy = MathF.Min(0, vy * 0.6f) - 200 * b.M.Scale; }
    }

    // ------------------------------------------------------------------ grabbing (called by the app's handle windows)

    public void GrabBall(int id)
    {
        foreach (var b in _balls)
            if (b.Id == id) { b.Held = true; b.On = null; b.Vx = b.Vy = 0; b.Rest = 0; _held = b; _heldGrab = 0; Kick(1); return; }
    }

    public void ReleaseBall()
    {
        if (_held == null) return;
        var b = _held;
        b.Held = false; _held = null;
        // Thrown with the cursor's speed.
        float s = b.M.Scale;
        b.Vx = Math.Clamp(CursorVx, -2500 * s, 2500 * s);
        b.Vy = Math.Clamp(CursorVy, -2500 * s, 2500 * s);
        // Let go on top of an edge: it settles there instead of falling through.
        foreach (var sf in _surf)
            if (b.X >= sf.X0 && b.X < sf.X1)
            {
                float top = PileTopAt(sf.W, b.X, out _);
                if (MathF.Abs(b.Y + b.R * 0.85f - top) < b.R * 0.8f && MathF.Abs(b.Vy) < 400 * s) { b.On = sf.W; b.Off = b.X - sf.W.R.Left; b.Y = top - b.R * 0.85f; b.Vy = 0; break; }
            }
        Kick(1);
    }

    // ------------------------------------------------------------------ drawing

    void AddSnowballSprites(Monitor m)
    {
        if (_balls.Count == 0) return;
        var sp = _sprites;
        foreach (var b in _balls)
        {
            if (b.M != m) continue;
            float r = b.R;
            // A faint shadow under it, the ball, and three specks that turn with it so it visibly rolls.
            if (b.On != null) sp.Add(Atlas.Flake32, b.X - r * 0.9f, b.Y + r * 0.55f, b.X + r * 0.9f, b.Y + r * 1.05f, Pm(0.45f, 0.52f, 0.62f, 0.25f));
            sp.Add(Atlas.Cap, b.X - r, b.Y - r, b.X + r, b.Y + r, Pm(1, 1, 1, 1));
            for (int k = 0; k < 3; k++)
            {
                float a = b.Ang + k * 2.1f, d = r * (0.35f + 0.18f * k);
                float sx = b.X + MathF.Cos(a) * d, sy = b.Y + MathF.Sin(a) * d * 0.9f;
                float z = MathF.Max(1.2f, r * 0.16f);
                sp.Add(Atlas.Dot, sx - z, sy - z, sx + z, sy + z, Pm(0.62f, 0.70f, 0.80f, 0.55f));
            }
        }
    }
}
