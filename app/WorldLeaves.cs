using System.Numerics;
using Vortice.Mathematics;

namespace WindowsWeather;

// Falling leaves ("can we also maybe add falling leaves or something for fall?").
// In Live mode they come with autumn at the laptop's latitude on dry days, more when it is windy. Each leaf flutters,
// spins and sways as it falls, settles on window tops and the taskbar, rides along a slow move, flies off with a fling
// (or a resize / minimize), and gets kicked up by the cursor. A dozen or so sprites: costs next to nothing.
public sealed partial class World
{
    struct Leaf
    {
        public float X, Y, Vx, Vy, Ang, Spin, Size, Phase;
        public byte Kind, Tint;
        public int Mon;
        public Tracked? On;        // resting on this edge (null = in the air)
        public float OffX;         // where along the edge
        public float Rest;         // seconds spent resting
    }

    Leaf[] _leaves = new Leaf[64];
    int _nLeaves;
    float _leafAcc;

    static readonly (float r, float g, float b)[] LeafTints =
    {
        (0.93f, 0.50f, 0.14f), // orange
        (0.78f, 0.20f, 0.11f), // red
        (0.95f, 0.74f, 0.20f), // gold
        (0.58f, 0.34f, 0.15f), // brown
        (0.86f, 0.36f, 0.12f), // rust
    };

    int LeafTarget()
    {
        int t = 0;
        foreach (var m in Monitors) t += (int)(16 * Cur.Leaves * LeafAmt * m.Bounds.Width / 1920f);
        return t;
    }

    void SpawnLeaf(bool anywhere)
    {
        if (Monitors.Count == 0) return;
        if (_nLeaves == _leaves.Length) Array.Resize(ref _leaves, _leaves.Length * 2);
        int mi = PickMonitor();
        var m = Monitors[mi];
        float s = m.Scale;
        _leaves[_nLeaves++] = new Leaf
        {
            X = m.Bounds.Left + Rf() * m.Bounds.Width,
            Y = anywhere ? m.Bounds.Top + Rf() * m.Bounds.Height * 0.8f : m.Bounds.Top - 24 * s,
            Vy = (38 + Rf() * 26) * s,
            Ang = Rf() * 6.28f, Spin = (Rf() - 0.5f) * 3.2f,
            Size = (19 + Rf() * 12) * s, Phase = Rf() * 6.28f,
            Kind = (byte)_rnd.Next(2), Tint = (byte)_rnd.Next(LeafTints.Length), Mon = mi,
        };
    }

    void StepLeaves(float dt)
    {
        int target = LeafTarget();
        int airborne = 0;
        for (int i = 0; i < _nLeaves; i++) if (_leaves[i].On == null) airborne++;
        if (airborne < target)
        {
            _leafAcc += target / 14f * dt; // about one screen-height of fall per 14 s
            while (_leafAcc >= 1) { _leafAcc -= 1; SpawnLeaf(false); }
        }
        else _leafAcc = 0;

        var surfs = _surf; // visible top edges, gathered this frame by the snow
        for (int i = 0; i < _nLeaves; i++)
        {
            ref var l = ref _leaves[i];
            var m = Monitors[Math.Min(l.Mon, Monitors.Count - 1)];
            float s = m.Scale;
            if (l.On != null)
            {
                // Resting: follow the window; let go if the edge is gone; fade after a while.
                var w = l.On;
                l.Rest += dt;
                bool gone = !w.Eligible || w.Spans.Count == 0 || !Tracker.Z.Contains(w) || !InSpans(w, (int)(l.OffX / w.ColW));
                if (gone) { l.On = null; l.Vy = 30 * s; l.Spin = (Rf() - .5f) * 4; continue; }
                if (_torOn && TorInfluence(l.X, l.Y, out _, out _) > 0.3f) { l.On = null; l.Vy = -180 * s; l.Spin = (Rf() - .5f) * 12; continue; }
                if (_flLevel > 0 && l.Y > FloodY(m, l.X)) { l.On = null; l.Vy = -40 * s; continue; }
                l.X = w.R.Left + l.OffX; l.Y = w.R.Top - l.Size * 0.18f;
                if (l.Rest > 75 || (Cur.Leaves < 0.02f && l.Rest > 8)) { _leaves[i] = _leaves[--_nLeaves]; i--; }
                continue;
            }
            // Flutter: a pendulum sway that tips the leaf and slows its fall, plus the wind.
            l.Phase += dt * (1.6f + l.Size * 0.02f / s);
            float sway = MathF.Sin(l.Phase);
            float targetVy = (30 + 22 * MathF.Abs(MathF.Cos(l.Phase))) * s;
            l.Vy += (targetVy - l.Vy) * MathF.Min(1, dt * 1.5f);
            float targetVx = (WindX * 1.4f + GustX * 1.2f) * Mod("Leaves.Wind") * s + sway * 55 * s;
            l.Vx += (targetVx - l.Vx) * MathF.Min(1, dt * 1.2f);
            if (_torOn) { TornadoPull(l.X, l.Y, s, dt, 300 * s, ref l.Vx, ref l.Vy); }
            if (_flLevel > 0)
            {
                // Floating: held at the surface and carried along it.
                float wy = FloodY(m, l.X);
                if (l.Y > wy - l.Size * 0.15f)
                {
                    l.Vy = (wy - l.Size * 0.15f - l.Y) * 4;
                    l.Vx = MathF.Sin((float)Now * 0.3f + l.Phase) * 30 * s;
                    l.Spin *= MathF.Exp(-dt * 3);
                    l.Phase -= dt * (1.6f + l.Size * 0.02f / s) * 0.9f; // hardly any flutter on the water
                }
            }
            float oy = l.Y;
            l.X += l.Vx * dt; l.Y += l.Vy * dt;
            l.Ang += (l.Spin + sway * 1.4f) * dt;
            l.Spin *= MathF.Exp(-dt * 0.3f);
            if (l.X < m.Bounds.Left - 40) l.X += m.Bounds.Width + 80;
            else if (l.X > m.Bounds.Right + 40) l.X -= m.Bounds.Width + 80;
            if (l.Y > m.Bounds.Bottom + 30) { _leaves[i] = _leaves[--_nLeaves]; i--; continue; }
            // Land on a visible top edge (not too many per edge).
            foreach (var sf in surfs)
            {
                if (l.X < sf.X0 || l.X >= sf.X1 || oy > sf.Y || l.Y < sf.Y) continue;
                int resting = 0;
                for (int k = 0; k < _nLeaves; k++) if (_leaves[k].On == sf.W) resting++;
                if (resting >= Math.Max(2, sf.W.R.Width / (45 * s))) break;
                l.On = sf.W; l.OffX = l.X - sf.W.R.Left; l.Rest = 0;
                l.Ang = (Rf() - 0.5f) * 0.9f; // settle nearly flat
                break;
            }
        }
    }

    /// <summary>Leaves resting on a window let go (resize, fling, minimize, close): they fly with the window's motion.</summary>
    void LeavesFall(Tracked w, float vx, float vy)
    {
        for (int i = 0; i < _nLeaves; i++)
        {
            ref var l = ref _leaves[i];
            if (l.On != w) continue;
            l.On = null;
            l.Vx = vx * (0.6f + 0.5f * Rf()) + (Rf() - .5f) * 60 * w.Scale;
            l.Vy = vy * 0.4f - (40 + Rf() * 90) * w.Scale;
            l.Spin = (Rf() - .5f) * 9;
        }
    }

    /// <summary>The cursor kicks resting leaves up and pushes falling ones aside.</summary>
    void KickLeaves(float x0, float y0, float x1, float y1)
    {
        if (_nLeaves == 0) return;
        float cvx = Math.Clamp(CursorVx, -2500, 2500), cvy = Math.Clamp(CursorVy, -2500, 2500);
        float sx = x1 - x0, sy = y1 - y0, len2 = MathF.Max(1, sx * sx + sy * sy);
        for (int i = 0; i < _nLeaves; i++)
        {
            ref var l = ref _leaves[i];
            float t = Math.Clamp(((l.X - x0) * sx + (l.Y - y0) * sy) / len2, 0, 1);
            float dx = l.X - (x0 + sx * t), dy = l.Y - (y0 + sy * t);
            float reach = l.Size * 1.1f;
            if (dx * dx + dy * dy > reach * reach) continue;
            float s = Monitors[Math.Min(l.Mon, Monitors.Count - 1)].Scale;
            l.On = null;
            l.Vx = cvx * 0.45f + (Rf() - .5f) * 40 * s;
            l.Vy = MathF.Min(cvy * 0.3f, 0) - (60 + Rf() * 110) * s;
            l.Spin = (Rf() - .5f) * 10 + cvx * 0.004f;
            Kick(0.5);
        }
    }

    void AddLeafSprites(Monitor m)
    {
        if (_nLeaves == 0) return;
        var b = m.Bounds;
        float bright = 0.55f + 0.45f * MathF.Min(1, MathF.Max(0, (SunElev + 6) / 16f)); // dimmer at dusk and night
        for (int i = 0; i < _nLeaves; i++)
        {
            ref var l = ref _leaves[i];
            if (l.X < b.Left - 40 || l.X > b.Right + 40 || l.Y < b.Top - 40 || l.Y > b.Bottom + 40) continue;
            var c = LeafTints[l.Tint];
            float a = l.On != null && l.Rest > 65 ? MathF.Max(0, 1 - (l.Rest - 65) / 10f) : 1;
            float h = l.Size * 0.5f;
            // Foreshortening as it tumbles: the leaf looks thinner when it turns edge-on.
            float squash = l.On != null ? 0.55f : 0.35f + 0.65f * MathF.Abs(MathF.Cos(l.Phase * 0.8f + l.Ang * 0.3f));
            var xf = Matrix3x2.CreateScale(1, squash, new Vector2(l.X, l.Y)) * Matrix3x2.CreateRotation(l.Ang, new Vector2(l.X, l.Y));
            _sprites.AddRotated(l.Kind == 0 ? Atlas.Leaf : Atlas.Leaf2, l.X - h, l.Y - h, l.X + h, l.Y + h,
                Pm(c.r * bright, c.g * bright, c.b * bright, 0.92f * a), xf);
        }
    }

    // ------------------------------------------------------------------ the season

    /// <summary>0..1: how autumn it is at this latitude today (peak October to mid November in the north).</summary>
    public static float AutumnAmount(double lat, DateTime date)
    {
        int doy = date.DayOfYear;
        if (lat < 0) doy = (doy + 182) % 365 + 1;  // southern hemisphere: six months on
        // Ramp in from Sep 10 (253), full Oct 1 to Nov 15 (274..319), ramp out by Dec 5 (339).
        if (doy < 253 || doy > 339) return 0;
        if (doy < 274) return (doy - 253) / 21f;
        if (doy <= 319) return 1;
        return (339 - doy) / 20f;
    }
}
