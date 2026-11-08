using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

public sealed class Monitor
{
    public nint Handle;
    public RECT Bounds;
    public float Scale = 1;
    public Overlay? Ov;
    public FrostLayer?[] Frost = new FrostLayer?[4];
    public void DisposeFrost() { for (int i = 0; i < Frost.Length; i++) { Frost[i]?.Dispose(); Frost[i] = null; } }
}

/// <summary>How much of each effect is on screen. Crossfaded towards the target over ~10 s.</summary>
public struct Mix
{
    public float Snow, Rain, Haze, Wisps, Glow, Night, Storm, Low, Mid, High, Fog, Leaves, Frost, Hail, Aurora, Smog, Tornado, Quake, Flood;
    public static Mix Lerp(Mix a, Mix b, float t) => new()
    {
        Snow = a.Snow + (b.Snow - a.Snow) * t, Rain = a.Rain + (b.Rain - a.Rain) * t, Haze = a.Haze + (b.Haze - a.Haze) * t,
        Wisps = a.Wisps + (b.Wisps - a.Wisps) * t, Glow = a.Glow + (b.Glow - a.Glow) * t, Night = a.Night + (b.Night - a.Night) * t,
        Storm = a.Storm + (b.Storm - a.Storm) * t, Low = a.Low + (b.Low - a.Low) * t, Mid = a.Mid + (b.Mid - a.Mid) * t,
        High = a.High + (b.High - a.High) * t, Fog = a.Fog + (b.Fog - a.Fog) * t, Leaves = a.Leaves + (b.Leaves - a.Leaves) * t,
        Frost = a.Frost + (b.Frost - a.Frost) * t, Hail = a.Hail + (b.Hail - a.Hail) * t, Aurora = a.Aurora + (b.Aurora - a.Aurora) * t, Smog = a.Smog + (b.Smog - a.Smog) * t,
        Tornado = a.Tornado + (b.Tornado - a.Tornado) * t, Quake = a.Quake + (b.Quake - a.Quake) * t, Flood = a.Flood + (b.Flood - a.Flood) * t,
    };
    public readonly float MaxDiff(Mix b)
    {
        float m = 0;
        void D(float x, float y) { float d = MathF.Abs(x - y); if (d > m) m = d; }
        D(Snow, b.Snow); D(Rain, b.Rain); D(Haze, b.Haze); D(Wisps, b.Wisps); D(Glow, b.Glow); D(Night, b.Night); D(Storm, b.Storm);
        D(Low, b.Low); D(Mid, b.Mid); D(High, b.High); D(Fog, b.Fog); D(Leaves, b.Leaves);
        D(Frost, b.Frost); D(Hail, b.Hail); D(Aurora, b.Aurora); D(Smog, b.Smog); D(Tornado, b.Tornado); D(Quake, b.Quake); D(Flood, b.Flood);
        return m;
    }
    public readonly bool Any => Snow > 0.001f || Rain > 0.001f || Haze > 0.001f || Wisps > 0.001f || Glow > 0.001f || Night > 0.001f || Storm > 0.001f;
}

public sealed partial class World
{
    // ------------------------------------------------------------------ particles
    [StructLayout(LayoutKind.Sequential)]
    struct Flake { public float X, Y, Vy, Size, Phase, Sway, Bottom; public int Mon; }
    [StructLayout(LayoutKind.Sequential)]
    struct Debris { public float X, Y, Vx, Vy, Size, Life, Bottom; public byte Water; }
    [StructLayout(LayoutKind.Sequential)]
    struct Drop { public float X, Y, Vy, Len, Alpha, Bottom; public int Mon; }
    struct Twinkle { public float X, Y, T, Size; }

    Flake[] _flakes = new Flake[256]; int _nFlakes;
    Debris[] _debris = new Debris[512]; int _nDebris;
    Drop[] _drops = new Drop[256]; int _nDrops;
    readonly List<Twinkle> _twinkles = new();

    readonly Random _rnd = new();
    float Rf() => (float)_rnd.NextDouble();

    public Mix Cur, Target;
    public float SnowAmt = 1, RainAmt = 1, LeafAmt = 1, CloudAmt = 1; // the flyout's Amount sliders (0..2)
    public float WindX;              // px/s at scale 1, + = right
    public float SunX;               // -1 left .. +1 right: where the sun is
    public float SunElev = 30;       // degrees
    public double Now => App.Seconds;
    public float CursorX, CursorY, CursorVx, CursorVy;
    bool _cursorInit;
    public void ResetCursor() => _cursorInit = false;
    public double LastCursorMove = -10;
    double _nextTwinkle = 8, _nextFlash = 30, _flashT = -10;
    public double ActiveUntil;       // keep rendering until this time (window moved etc.)
    float _flakeAcc, _dropAcc;

    public List<Monitor> Monitors = new();
    public Tracker Tracker = null!;

    public void Kick(double seconds = 0.6) { ActiveUntil = Math.Max(ActiveUntil, Now + seconds); Kicked = true; }
    public bool Kicked; // something asked for frames since the main loop last looked

    /// <summary>Is anything on screen tied to a window's position (piles, puddles, beads, resting leaves, glow)?</summary>
    public bool NeedsPositions()
    {
        if (Cur.Glow > 0.01f || _torOn) return true;
        foreach (var w in Tracker.Z) if (w.PileTotal > 0.3f || w.WaterTotal > 0.3f || w.Beads.Count > 0) return true;
        for (int i = 0; i < _nLeaves; i++) if (_leaves[i].On != null) return true;
        return false;
    }

    /// <summary>Is there anything the cursor could brush (so an idle app should still glance at the cursor)?</summary>
    public bool HasInteractive() => _clouds.Count > 0 || Cur.Fog > 0.02f || NeedsPositions();

    // ------------------------------------------------------------------ reactions (called from the tracker)

    public void DropPile(Tracked w, float vx = 0, float vy = 0)
    {
        if (w.IsToast && w.PileTotal > 0.5f) Log.Write($"notification gone: its snow falls ({w.PileTotal:0} px of pile)");
        if (w.H.Length > 0 && w.PileTotal > 0.5f)
        {
            int budget = 900 - _nDebris;
            for (int i = 0; i < w.H.Length && budget > 0; i++)
            {
                float h = w.H[i];
                if (h < 0.6f) continue;
                int n = Math.Max(1, (int)(h / (4.5f * w.Scale)));
                if (_rnd.Next(3) == 0) n++;
                for (int k = 0; k < n && budget > 0; k++, budget--)
                {
                    float x = w.R.Left + (i + Rf()) * w.ColW;
                    float y = w.R.Top - Rf() * h;
                    AddDebris(x, y, vx * (0.75f + 0.5f * Rf()) + (Rf() - 0.5f) * 90 * w.Scale,
                        vy * (0.6f + 0.5f * Rf()) - Rf() * 60 * w.Scale, (2.2f + Rf() * 3.4f) * w.Scale, false);
                }
            }
        }
        if (w.H.Length > 0) Array.Clear(w.H);
        w.PileTotal = 0;
        SpillWater(w, vx, vy);
        LeavesFall(w, vx, vy);
        RunOffBeads(w, vx, vy);
        Kick(0.3);
    }

    void RunOffBeads(Tracked w, float vx, float vy)
    {
        foreach (var bx in w.Beads)
            AddDebris(w.R.Left + bx, w.R.Top - 3 * w.Scale, vx * 0.8f + (Rf() - .5f) * 40, vy * 0.5f - Rf() * 40, (3 + Rf() * 2.5f) * w.Scale, true);
        w.Beads.Clear();
    }

    public void OnWindowMoved(Tracked w, RECT old, uint ms)
    {
        Kick(0.5);
        if (!w.Eligible && !w.IsTaskbar) return;
        // A toast sliding in grows in height only: that is not a resize that should shake the snow off.
        bool resized = Math.Abs(old.Width - w.R.Width) > 1 || (!w.IsToast && Math.Abs(old.Height - w.R.Height) > 1);
        int dx = w.R.Left - old.Left, dy = w.R.Top - old.Top;
        if (resized)
        {
            // The cap breaks up and falls off, and the icicles snap.
            DropPile(w, 0, 0);
            BreakIcicles(w, 0, 0);
            BallsOffWindow(w, 0, 0);
            w.EnsureColumns();
            w.Vx = w.Vy = 0;
            return;
        }
        if (dx == 0 && dy == 0) return;
        float dt = w.LastEventMs == 0 ? 0.05f : Math.Clamp((ms - w.LastEventMs) / 1000f, 0.004f, 0.25f);
        if (ms - w.LastEventMs > 250) { w.Vx = w.Vy = 0; dt = 0.05f; }
        float ivx = dx / dt, ivy = dy / dt;
        w.Vx = w.Vx * 0.45f + ivx * 0.55f;
        w.Vy = w.Vy * 0.45f + ivy * 0.55f;
        float speed = MathF.Sqrt(w.Vx * w.Vx + w.Vy * w.Vy);
        float fling = 1300 * w.Scale;
        if (speed > fling) { BreakIcicles(w, w.Vx, w.Vy); BallsOffWindow(w, w.Vx, w.Vy); }
        if (speed > fling && w.PileTotal > 0.5f)
            DropPile(w, w.Vx, w.Vy);          // flung: the snow flies off with the throw
        else if (speed > fling) LeavesFall(w, w.Vx, w.Vy);
        else if (w.Beads.Count > 0 && (Math.Abs(dx) + Math.Abs(dy)) > 1)
            RunOffBeads(w, w.Vx, w.Vy);       // any move: beads run off
        if (Cur.Glow > 0.05f && Now - w.LastGlint > 1.2)
        {
            w.LastGlint = Now; w.GlintStart = Now; w.GlintDir = dx >= 0 ? 1 : -1;
        }
        if (_clouds.Count > 0 && speed > 250 * w.Scale)
            PushCloudsByWindow(w, w.Vx, w.Vy);
    }

    void AddDebris(float x, float y, float vx, float vy, float size, bool water)
    {
        if (_nDebris == _debris.Length) { if (_nDebris >= 4000) return; Array.Resize(ref _debris, _debris.Length * 2); }
        var m = Tracker.MonitorAt(Monitors, (int)x, (int)y) ?? Tracker.MonitorAt(Monitors, (int)x, (int)y + 40);
        float bottom = m != null ? m.Bounds.Bottom + 20 : VirtualBottom();
        _debris[_nDebris++] = new Debris { X = x, Y = y, Vx = vx, Vy = vy, Size = size, Life = 1, Bottom = bottom, Water = (byte)(water ? 1 : 0) };
    }

    float VirtualBottom() { float b = 0; foreach (var m in Monitors) b = MathF.Max(b, m.Bounds.Bottom); return b + 20; }

    public void ClearAll()
    {
        foreach (var w in Tracker.Z) { if (w.H.Length > 0) Array.Clear(w.H); w.PileTotal = 0; w.Beads.Clear(); }
        _nFlakes = _nDebris = _nDrops = _nLeaves = _nHail = 0;
        foreach (var w in Tracker.Z) { if (w.Wt.Length > 0) Array.Clear(w.Wt); w.WaterTotal = 0; }
        _ripples.Clear();
        _balls.Clear(); _held = null;
        Kick(0.2);
    }

    /// <summary>Starting in the middle of a snowfall: give the piles a head start.</summary>
    public void SeedPiles(float fraction)
    {
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.H.Length == 0) continue;
            float max = 14 * w.Scale * PileScale * fraction;
            // Only edges that are actually exposed would have caught snow.
            foreach (var (a, b) in w.Spans)
                for (int i = a; i < b; i++)
                    w.H[i] = max * (0.55f + 0.45f * MathF.Sin(i * 0.21f + w.Jit[0] * 3) * MathF.Sin(i * 0.057f + 1.3f));
            Relax(w, 1f);
            RecountPile(w);
        }
    }

    /// <summary>A window that just appeared while it is snowing gets a light dusting (toasts get more).</summary>
    public void OnNewSurface(Tracked w)
    {
        if (Cur.Snow < 0.2f || w.H.Length == 0) return;
        if (!w.IsToast) return;
        float max = 14 * w.Scale * 0.35f * Cur.Snow;
        for (int i = 0; i < w.H.Length; i++)
            w.H[i] = max * (0.6f + 0.4f * MathF.Sin(i * 0.33f + w.Jit[0] * 3) * MathF.Sin(i * 0.071f + 0.4f));
        Relax(w, 1f);
        RecountPile(w);
        Kick(0.3);
    }

    // ------------------------------------------------------------------ simulation step

    public void Step(float dt)
    {
        // Crossfade the effect mix (about 10 s end to end).
        float k = Math.Min(1, dt / 3.5f);
        Cur = Mix.Lerp(Cur, Target, k);
        if (Cur.MaxDiff(Target) < 0.004f) Cur = Target;

        UpdateCursor(dt);
        StepGusts(dt);
        StepTornado(dt);
        StepQuake(dt);
        StepFlood(dt);
        StepGrabs(dt);
        StepTiles(dt);
        StepIcicles(dt);
        StepSnowballs(dt);
        StepFlakes(dt);
        StepLeaves(dt);
        StepHail(dt);
        StepFrost(dt);
        StepRain(dt);
        StepDebris(dt);
        StepPiles(dt);
        StepClouds(dt);
        StepWater(dt);
        StepSkies(dt);
        StepLightning();
    }

    void UpdateCursor(float dt)
    {
        POINT p;
        unsafe { Native.GetCursorPos(&p); }
        if (!_cursorInit) { CursorX = p.X; CursorY = p.Y; _cursorInit = true; }
        float px = CursorX, py = CursorY;
        float vx = (p.X - px) / MathF.Max(dt, 0.001f), vy = (p.Y - py) / MathF.Max(dt, 0.001f);
        CursorVx = CursorVx * 0.4f + vx * 0.6f; CursorVy = CursorVy * 0.4f + vy * 0.6f;
        CursorX = p.X; CursorY = p.Y;
        if (p.X == (int)px && p.Y == (int)py) return;
        LastCursorMove = Now;
        if (HoldingBall) return; // the cursor is carrying a snowball: it doesn't brush anything else
        BrushPiles(px, py, p.X, p.Y);
        PushClouds(px, py, p.X, p.Y);
        KickLeaves(px, py, p.X, p.Y);
        BrushWater(px, py, p.X, p.Y);
    }

    public bool CursorNearSomething(int x, int y)
    {
        // Clouds only live in the upper part of the screen; the cursor only matters up there.
        if (Cur.Fog > 0.02f || _nLeaves > 0) return true;
        if (_clouds.Count > 0)
            foreach (var m in Monitors)
                if (x >= m.Bounds.Left && x < m.Bounds.Right && y >= m.Bounds.Top && y < CloudBandBottom(m) + 80 * m.Scale) return true;
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0) continue;
            if (w.PileTotal > 0.5f || w.Beads.Count > 0 || w.WaterTotal > 0.3f)
            {
                float pad = 30 * w.Scale;
                if (x >= w.R.Left - pad && x <= w.R.Right + pad && y >= w.R.Top - 14 * w.Scale - pad && y <= w.R.Top + pad) return true;
            }
        }
        return false;
    }

    void BrushPiles(float x0, float y0, float x1, float y1)
    {
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0) continue;
            bool beads = w.Beads.Count > 0;
            if (w.PileTotal < 0.3f && !beads) continue;
            float r = 6 * w.Scale;
            float top = w.R.Top;
            if (MathF.Max(y0, y1) < top - 16 * w.Scale - r || MathF.Min(y0, y1) > top + r) continue;
            float lo = MathF.Min(x0, x1) - r, hi = MathF.Max(x0, x1) + r;
            if (hi < w.R.Left || lo > w.R.Right) continue;
            int c0 = Math.Max(0, (int)((lo - w.R.Left) / w.ColW)), c1 = Math.Min(w.H.Length - 1, (int)((hi - w.R.Left) / w.ColW));
            float dxs = x1 - x0;
            float cvx = Math.Clamp(CursorVx, -2500, 2500), cvy = Math.Clamp(CursorVy, -2500, 2500);
            bool hit = false;
            for (int c = c0; c <= c1; c++)
            {
                float cx = w.R.Left + (c + 0.5f) * w.ColW;
                float t = MathF.Abs(dxs) < 0.5f ? 1 : Math.Clamp((cx - x0) / dxs, 0, 1);
                float cy = y0 + (y1 - y0) * t;
                float h = w.H[c];
                if (h < 0.3f) continue;
                if (cy < top - h - r || cy > top + r) continue;
                if (!InSpans(w, c)) continue;
                // Scrape this column down to below the cursor and throw the snow in the cursor's direction.
                float keep = Math.Clamp(top - cy - r, 0, h) * 0.5f;
                float removed = h - keep;
                w.H[c] = keep;
                hit = true;
                int n = removed > 5 * w.Scale ? 2 : 1;
                if (_rnd.Next(2) == 0 || removed > 2.5f)
                    for (int i = 0; i < n; i++)
                        AddDebris(cx, top - keep - Rf() * removed, cvx * (0.35f + 0.4f * Rf()) + (Rf() - .5f) * 60,
                            MathF.Min(cvy * 0.4f, 0) - 80 * Rf() - 20, (2 + Rf() * 3) * w.Scale, false);
            }
            if (beads && MathF.Min(y0, y1) < top + r && MathF.Max(y0, y1) > top - 12 * w.Scale)
            {
                for (int i = w.Beads.Count - 1; i >= 0; i--)
                {
                    float bx = w.R.Left + w.Beads[i];
                    if (bx < lo || bx > hi) continue;
                    AddDebris(bx, top - 3, cvx * 0.5f, -40 * Rf(), 3.5f * w.Scale, true);
                    w.Beads.RemoveAt(i);
                    hit = true;
                }
            }
            if (hit) { RecountPile(w); Kick(0.4); }
        }
    }

    static bool InSpans(Tracked w, int c)
    {
        foreach (var (a, b) in w.Spans) if (c >= a && c < b) return true;
        return false;
    }

    static void RecountPile(Tracked w)
    {
        float s = 0; var h = w.H;
        for (int i = 0; i < h.Length; i++) s += h[i];
        w.PileTotal = s;
    }

    void StepFlakes(float dt)
    {
        float snow = Cur.Snow;
        // Target population per monitor ~ 170 flakes per 1920 px of width at medium.
        int target = 0;
        foreach (var m in Monitors) target += (int)(170 * snow * SnowAmt * m.Bounds.Width / (1920f));
        if (_nFlakes < target)
        {
            // Spawn at the top at the rate needed to hold the population (life ~ height / 75 px/s).
            float rate = target / MathF.Max(6f, (Monitors.Count > 0 ? Monitors[0].Bounds.Height : 1080) / (75f * (Monitors.Count > 0 ? Monitors[0].Scale : 1)));
            _flakeAcc += rate * dt * 1.15f;
            while (_flakeAcc >= 1 && _nFlakes < target) { _flakeAcc -= 1; SpawnFlake(false); }
        }
        else _flakeAcc = 0;

        var spans = GatherSurfaces();
        for (int i = 0; i < _nFlakes; i++)
        {
            ref var f = ref _flakes[i];
            var m = Monitors[Math.Min(f.Mon, Monitors.Count - 1)];
            float s = m.Scale;
            float oy = f.Y;
            f.Phase += dt * (0.9f + f.Size * 0.05f);
            float sway = MathF.Sin(f.Phase) * f.Sway;
            f.Y += f.Vy * dt;
            f.X += (WindX * s * (0.6f + f.Size * 0.06f) + GustX * s * 0.9f + sway) * dt;
            if (f.X < m.Bounds.Left - 10) f.X += m.Bounds.Width + 20;
            else if (f.X > m.Bounds.Right + 10) f.X -= m.Bounds.Width + 20;
            bool dead = f.Y > f.Bottom || (_flLevel > 0 && f.Y > FloodY(Monitors[Math.Min(f.Mon, Monitors.Count - 1)], f.X));
            if (!dead && Land(spans, f.X, oy, f.Y, f.Size * s)) dead = true;
            if (dead) { _flakes[i] = _flakes[--_nFlakes]; i--; }
        }
    }

    void SpawnFlake(bool anywhere)
    {
        if (Monitors.Count == 0) return;
        if (_nFlakes == _flakes.Length) Array.Resize(ref _flakes, _flakes.Length * 2);
        int mi = PickMonitor();
        var m = Monitors[mi];
        float s = m.Scale;
        float size = (3.0f + MathF.Pow(Rf(), 2.0f) * 7.0f) * Mod("Snow.Size"); // mostly small, a few big
        _flakes[_nFlakes++] = new Flake
        {
            X = m.Bounds.Left + Rf() * m.Bounds.Width,
            Y = anywhere ? m.Bounds.Top + Rf() * m.Bounds.Height : m.Bounds.Top - 12 * s,
            Vy = (38 + size * 9 + Rf() * 22) * s,
            Size = size,
            Phase = Rf() * 6.28f,
            Sway = (8 + Rf() * 22) * s,
            Bottom = m.Bounds.Bottom + 10,
            Mon = mi,
        };
    }

    int PickMonitor()
    {
        if (Monitors.Count == 1) return 0;
        float tot = 0; foreach (var m in Monitors) tot += m.Bounds.Width;
        float r = Rf() * tot;
        for (int i = 0; i < Monitors.Count; i++) { r -= Monitors[i].Bounds.Width; if (r <= 0) return i; }
        return 0;
    }

    public void Populate()
    {
        // Fill the screen straight away instead of waiting for the first flakes to arrive from the top.
        int target = 0;
        foreach (var m in Monitors) target += (int)(170 * Target.Snow * SnowAmt * m.Bounds.Width / 1920f);
        while (_nFlakes < target) SpawnFlake(true);
        int dtarget = 0;
        foreach (var m in Monitors) dtarget += (int)(150 * Target.Rain * RainAmt * m.Bounds.Width / 1920f);
        while (_nDrops < dtarget) SpawnDrop(true);
    }

    // Surfaces: the visible part of every pile-holding top edge, flattened for quick hit tests.
    struct Surf { public Tracked W; public float Y, X0, X1; public int A, B; }
    readonly List<Surf> _surf = new(64);

    List<Surf> GatherSurfaces()
    {
        _surf.Clear();
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible) continue;
            foreach (var (a, b) in w.Spans)
                _surf.Add(new Surf { W = w, Y = w.R.Top, X0 = w.R.Left + a * w.ColW, X1 = w.R.Left + b * w.ColW, A = a, B = b });
        }
        return _surf;
    }

    bool Land(List<Surf> surfs, float x, float y0, float y1, float size)
    {
        foreach (var s in surfs)
        {
            if (x < s.X0 || x >= s.X1) continue;
            var w = s.W;
            int c = (int)((x - w.R.Left) / w.ColW);
            if (c < s.A || c >= s.B) continue;
            float top = s.Y - w.H[c];
            if (y0 <= top + 1 && y1 >= top - 1 && y1 <= s.Y + 6 * w.Scale)
            {
                // Deposit: a small mound centred on the column.
                // Toasts only live a few seconds: snow builds up on them much faster so the cap shows.
                float vol = size * 0.95f * (w.IsToast ? 9f : 1f);
                Deposit(w, c, vol * 0.5f);
                if (c > 0) Deposit(w, c - 1, vol * 0.25f);
                if (c + 1 < w.H.Length) Deposit(w, c + 1, vol * 0.25f);
                return true;
            }
        }
        return false;
    }

    static void Deposit(Tracked w, int c, float v) { w.H[c] += v; w.PileTotal += v; }

    void StepPiles(float dt)
    {
        bool melting = Cur.Snow < 0.05f;
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.H.Length == 0 || w.PileTotal < 0.05f) continue;
            if (melting)
            {
                // No more snow: the piles slowly melt away over a few minutes.
                float m = dt * 0.05f * w.Scale * (1 + Cur.Glow * 2 + Cur.Rain * 3);
                var h = w.H;
                for (int i = 0; i < h.Length; i++) h[i] = MathF.Max(0, h[i] - m);
                RecountPile(w);
            }
            Relax(w, dt);
        }
    }

    /// <summary>Let the pile slump: limit the slope between columns, round the ends, cap the height.</summary>
    void Relax(Tracked w, float dt)
    {
        var h = w.H; int n = h.Length;
        float max = 14 * w.Scale * PileScale * Mod("Snow.Piles"), slope = 1.25f * w.Scale * (w.ColW / 3f), end = 1.6f * w.Scale;
        float kk = Math.Min(1, dt * 12);
        for (int i = 0; i < n - 1; i++)
        {
            float d = h[i] - h[i + 1];
            if (d > slope) { float mv = (d - slope) * 0.5f * kk; h[i] -= mv; h[i + 1] += mv; }
            else if (d < -slope) { float mv = (-d - slope) * 0.5f * kk; h[i] += mv; h[i + 1] -= mv; }
        }
        float spilled = 0;
        for (int i = 0; i < n; i++)
        {
            float cap = MathF.Min(max * (0.92f + 0.08f * w.Jit[i]), (MathF.Min(i, n - 1 - i) + 0.6f) * end);
            if (h[i] > cap) { spilled += h[i] - cap; h[i] = cap; }
        }
        if (spilled > 0)
        {
            w.PileTotal -= spilled;
            // Overflow at the ends tumbles off as a few flakes.
            if (spilled > 1.5f * w.Scale && _rnd.Next(3) == 0)
            {
                bool left = _rnd.Next(2) == 0;
                AddDebris(left ? w.R.Left + 2 : w.R.Right - 2, w.R.Top - 3 * w.Scale, (left ? -30 : 30) * w.Scale, 0, (2 + Rf() * 2) * w.Scale, false);
            }
        }
    }

    void StepRain(float dt)
    {
        float rain = MathF.Max(Cur.Rain, Cur.Storm * 0.9f);
        int target = 0;
        foreach (var m in Monitors) target += (int)(150 * rain * RainAmt * m.Bounds.Width / 1920f);
        if (_nDrops < target)
        {
            float rate = target / 1.1f;
            _dropAcc += rate * dt;
            while (_dropAcc >= 1 && _nDrops < target) { _dropAcc -= 1; SpawnDrop(false); }
        }
        else _dropAcc = 0;

        var spans = _surf; // gathered in StepFlakes this frame
        for (int i = 0; i < _nDrops; i++)
        {
            ref var d = ref _drops[i];
            var m = Monitors[Math.Min(d.Mon, Monitors.Count - 1)];
            float oy = d.Y;
            d.Y += d.Vy * dt;
            d.X += (WindX * 4.5f + GustX * 1.4f) * m.Scale * dt;
            if (d.X < m.Bounds.Left - 20) d.X += m.Bounds.Width + 40;
            else if (d.X > m.Bounds.Right + 20) d.X -= m.Bounds.Width + 40;
            bool dead = d.Y > d.Bottom;
            if (!dead && _flLevel > 0 && d.Y > FloodY(m, d.X)) { dead = true; if (_rnd.Next(6) == 0) AddRipple(d.X, FloodY(m, d.X), m.Scale); }
            if (!dead)
                foreach (var s in spans)
                {
                    if (d.X < s.X0 || d.X >= s.X1 || oy > s.Y || d.Y < s.Y) continue;
                    var w = s.W;
                    dead = true;
                    // Splash, and sometimes a bead stays on the edge.
                    int n = 1 + _rnd.Next(3);
                    for (int k = 0; k < n; k++)
                        AddDebris(d.X, s.Y - 1, (Rf() - .5f) * 160 * m.Scale, -(60 + Rf() * 120) * m.Scale, (1.6f + Rf() * 1.4f) * m.Scale, true);
                    // The water stays: it collects into a puddle along the edge.
                    if (w.Wt.Length > 0)
                    {
                        int col = Math.Clamp((int)((d.X - w.R.Left) / w.ColW), 0, w.Wt.Length - 1);
                        if (w.Wt[col] > 0.5f && _rnd.Next(3) == 0) AddRipple(d.X, s.Y, m.Scale);
                        DepositWater(w, col, 0.9f * m.Scale * Mod("Rain.Puddles"));
                    }
                    break;
                }
            if (dead) { _drops[i] = _drops[--_nDrops]; i--; }
        }
        // Beads evaporate slowly when it stops raining.
        if (rain < 0.05f && _rnd.Next(30) == 0)
            foreach (var w in Tracker.Z) if (w.Beads.Count > 0) { w.Beads.RemoveAt(_rnd.Next(w.Beads.Count)); Kick(0.1); }
    }

    void SpawnDrop(bool anywhere)
    {
        if (Monitors.Count == 0) return;
        if (_nDrops == _drops.Length) Array.Resize(ref _drops, _drops.Length * 2);
        int mi = PickMonitor();
        var m = Monitors[mi];
        float s = m.Scale;
        float sx = m.Bounds.Left + Rf() * m.Bounds.Width, sy = m.Bounds.Top - 40 * s;
        if (RainSource(mi, out float cx, out float cy)) { sx = cx; sy = cy + 4 * s; } // from under a cloud
        if (anywhere) sy += Rf() * (m.Bounds.Bottom - sy);
        _drops[_nDrops++] = new Drop
        {
            X = sx,
            Y = sy,
            Vy = (950 + Rf() * 450) * s * Mod("Rain.Speed"),
            Len = (16 + Rf() * 20) * s,
            Alpha = 0.22f + Rf() * 0.22f,
            Bottom = m.Bounds.Bottom + 40,
            Mon = mi,
        };
    }

    void StepDebris(float dt)
    {
        for (int i = 0; i < _nDebris; i++)
        {
            ref var d = ref _debris[i];
            float g = d.Water != 0 ? 1500 : 1100;
            d.Vy += g * dt;
            float drag = MathF.Exp(-dt * (d.Water != 0 ? 0.6f : 1.4f));
            d.Vx *= drag;
            if (_torOn) TornadoPull(d.X, d.Y, 1, dt, 1450, ref d.Vx, ref d.Vy);
            if (d.Water == 0) d.Vy = MathF.Min(d.Vy, 900);
            d.X += d.Vx * dt; d.Y += d.Vy * dt;
            if (d.Water != 0) d.Life -= dt * 0.8f;
            if (d.Y > d.Bottom || d.Life <= 0) { _debris[i] = _debris[--_nDebris]; i--; }
        }
    }

    // ------------------------------------------------------------------ sky events: twinkles, lightning

    void StepSkies(float dt)
    {
        for (int i = _twinkles.Count - 1; i >= 0; i--)
        {
            var t = _twinkles[i]; t.T += dt; _twinkles[i] = t;
            if (t.T > 1.8f) _twinkles.RemoveAt(i);
        }
        if (Cur.Night > 0.3f && Now >= _nextTwinkle && Monitors.Count > 0)
        {
            var m = Monitors[PickMonitor()];
            _twinkles.Add(new Twinkle { X = m.Bounds.Left + Rf() * m.Bounds.Width, Y = m.Bounds.Top + Rf() * m.Bounds.Height * 0.45f, Size = (10 + Rf() * 8) * m.Scale });
            _nextTwinkle = Now + (9 + Rf() * 22) / Mod("Night.Stars");
        }
        if (Cur.Storm > 0.3f && Now >= _nextFlash)
        {
            _flashT = Now;
            // Most flashes come with a visible bolt; the rest are sheet lightning inside the clouds.
            if (Monitors.Count > 0 && Rf() < 0.95f) MakeBolt(Monitors[PickMonitor()]);
            _nextFlash = Now + (12 + Rf() * 28) / Mod("Storm.Frequency");
        }
    }

    float FlashAlpha()
    {
        double t = Now - _flashT;
        if (t < 0 || t > 0.9) return 0;
        // One soft swell and fade: no strobing.
        float a = t < 0.08 ? (float)(t / 0.08) : (float)Math.Exp(-(t - 0.08) * 5);
        return MathF.Max(a * 0.16f * Cur.Storm, BoltIntensity() * 0.2f) * MathF.Min(1.6f, Mod("Storm.Strength"));
    }

    public string Describe() =>
        $"mix low={Cur.Low:0.00} mid={Cur.Mid:0.00} high={Cur.High:0.00} fog={Cur.Fog:0.00} sunset={SunsetAmount:0.00} snow={Cur.Snow:0.00} rain={Cur.Rain:0.00} haze={Cur.Haze:0.00} wisps={Cur.Wisps:0.00} glow={Cur.Glow:0.00} night={Cur.Night:0.00} storm={Cur.Storm:0.00} " +
        $"flakes={_nFlakes} leaves={_nLeaves} hail={_nHail} frost={_frost:0.00} aurora={Cur.Aurora:0.00} smog={Cur.Smog:0.00} gust={GustX:0} moon={(MoonPhase < 0 ? "?" : $"{MoonPhase:0.00}@{MoonAlt:0}")} drops={_nDrops} debris={_nDebris} twinkles={_twinkles.Count} windX={WindX:0} sunX={SunX:0.00} sunElev={SunElev:0} " +
        $"lastSprites={_lastSprites} {CloudPushDebug()} clouds={_clouds.Count} cloudsBusy={_cloudsBusy} ripples={_ripples.Count} water={Tracker.Z.Sum(w => w.WaterTotal):0}";

    // ------------------------------------------------------------------ frame scheduling

    /// <summary>Frames per second needed right now; 0 means nothing is moving and presenting can stop.</summary>
    public int DesiredFps(int maxFps)
    {
        if (_nFlakes > 0 || _nDrops > 0 || _nDebris > 0 || Cur.Snow > 0.01f || Cur.Rain > 0.01f || Cur.Storm > 0.01f) return maxFps;
        if (Now < ActiveUntil) return maxFps;
        if (Now - LastCursorMove < 0.4) return maxFps;
        if (BoltIntensity() > 0 || TornadoBusy || BallsMoving) return maxFps;
        if (_twinkles.Count > 0 || Now - _flashT < 1) return Math.Min(30, maxFps);
        if (Cur.MaxDiff(Target) > 0) return 15;
        if (_ripples.Count > 0) return maxFps;
        int extras = ExtrasFps(maxFps);
        if (extras == maxFps) return maxFps;
        for (int i = 0; i < _nLeaves; i++) if (_leaves[i].On == null) return maxFps; // leaves in the air
        if (Cur.Leaves > 0.01f) return maxFps;                // more leaves on the way
        if (_nLeaves > 0) return 4;                           // only resting leaves: slow, to fade them out
        if (_cloudsBusy) return maxFps;                       // clouds being shoved
        if (_cloudsReforming) return 15;                      // shoved puffs drifting home
        if (_clouds.Count > 0) return 10;                     // clouds drifting: slow and cheap
        foreach (var w in Tracker.Z) if (w.WaterTotal > 0.05f && Cur.Rain < 0.05f && Cur.Storm < 0.05f) return 5; // puddles drying
        foreach (var w in Tracker.Z) if (w.PileTotal > 0.05f && Cur.Snow < 0.05f) return 5; // melting
        if (_balls.Count > 0 && Cur.Snow < 0.05f) return 5;                                    // snowballs melting
        foreach (var w in Tracker.Z) if (Now - w.GlintStart < 0.8) return maxFps;
        if (_iciclesGrowing && extras < 5) return 5;                       // icicles growing or melting: slow
        return extras;
    }

    /// <summary>Seconds until something scheduled happens while idle (twinkle, lightning).</summary>
    public double NextEventIn()
    {
        double t = double.MaxValue;
        if (Cur.Night > 0.3f) t = Math.Min(t, _nextTwinkle - Now);
        if (Cur.Storm > 0.3f) t = Math.Min(t, _nextFlash - Now);
        return Math.Max(0, t);
    }

    // ------------------------------------------------------------------ rendering

    readonly SpriteList _sprites = new();

    // Where each frame's CPU time goes (shown by --dump, then reset).
    public long ProfStep, ProfBuild, ProfSubmit, ProfEndDraw, ProfPresent, ProfFrames, ProfSprites;
    double _profSince = -1;
    public string ProfileText()
    {
        double f = Math.Max(1, ProfFrames), ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double secs = _profSince < 0 ? 0 : Now - _profSince;
        string s = $"frames={ProfFrames} over {secs:0}s ({ProfFrames / Math.Max(1, secs):0.0}/s) per frame: step {ProfStep * ms / f:0.000} ms, build {ProfBuild * ms / f:0.000} ms, " +
                   $"submit {ProfSubmit * ms / f:0.000} ms, enddraw {ProfEndDraw * ms / f:0.000} ms, present {ProfPresent * ms / f:0.000} ms, sprites {ProfSprites / f:0}";
        ProfStep = ProfBuild = ProfSubmit = ProfEndDraw = ProfPresent = ProfFrames = ProfSprites = 0;
        _profSince = Now;
        return s;
    }
    int _lastSprites;

    // Sprite-batch colours are straight (not premultiplied) alpha: D2D premultiplies them itself.
    static Color4 Pm(float r, float g, float b, float a) => new(r, g, b, a);

    public void Render(Gpu gpu, Monitor m)
    {
        var ov = m.Ov!;
        var ctx = gpu.Ctx;
        var b = m.Bounds;
        float s = m.Scale;
        long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _sprites.Clear();
        UpdateMoonTexture(gpu);
        UpdateFrostLayers(gpu, m);
        BuildSprites(m);
        AddLightningSprites(m);
        long q1 = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfBuild += q1 - q0;

        _lastSprites = _sprites.Count;
        bool flash = FlashAlpha() > 0.002f;
        bool content = _sprites.Count > 0 || flash || FrostVisible(m) || DisasterVisible(m) || IciclesVisible(m);
        if (!content && !ov.HasContent) return; // already blank: nothing to present

        ctx.Target = ov.Target;
        long q2 = System.Diagnostics.Stopwatch.GetTimestamp();
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        ctx.Transform = Matrix3x2.CreateTranslation(-b.Left, -b.Top);
        DrawTiles(gpu, m, cover: true);
        if (flash)
        {
            float fa = FlashAlpha();
            _sprites.Add(Atlas.Vignette, b.Left, b.Top, b.Right, b.Bottom, Pm(0.86f, 0.9f, 1f, fa));
        }
        if (_sprites.Count > 0)
        {
            if (gpu.SpriteBatchOk)
            {
                var batch = gpu.Batch;
                batch.Clear();
                batch.AddSprites((uint)_sprites.Count, _sprites.Dest, _sprites.Src, _sprites.Col,
                    _sprites.AnyTransform ? _sprites.Xf : null!, 16, 16, 16, _sprites.AnyTransform ? 24u : 0u);
                ctx.DrawSpriteBatch(batch, 0, (uint)_sprites.Count, gpu.AtlasBmp, BitmapInterpolationMode.Linear, SpriteOptions.None);
            }
            else
            {
                var baseXf = ctx.Transform;
                for (int i = 0; i < _sprites.Count; i++)
                {
                    var d = _sprites.Dest[i]; var src = _sprites.Src[i]; var c = _sprites.Col[i];
                    ctx.Transform = _sprites.Xf[i] * baseXf;
                    ctx.DrawBitmap(gpu.AtlasBmp, new Rect(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top), c.A, BitmapInterpolationMode.Linear,
                        new Rect(src.Left, src.Top, src.Right - src.Left, src.Bottom - src.Top));
                }
                ctx.Transform = baseXf;
            }
        }
        DrawIcicles(gpu, m);
        DrawTiles(gpu, m, cover: false);
        DrawCracks(gpu, m);
        DrawBolt(gpu, m);
        DrawFrostLayers(ctx, m); // frost on top of everything
        ctx.Transform = Matrix3x2.Identity;
        long q3 = System.Diagnostics.Stopwatch.GetTimestamp();
        var hr = ctx.EndDraw();
        long q4 = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfSubmit += q3 - q2; ProfEndDraw += q4 - q3;
        ctx.Target = null;
        if (hr.Failure) throw new SharpGen.Runtime.SharpGenException(hr);
        ov.Swap.Present(0, Vortice.DXGI.PresentFlags.None).CheckError();
        ProfPresent += System.Diagnostics.Stopwatch.GetTimestamp() - q4;
        ProfSprites += _sprites.Count;
        ov.HasContent = content;
    }

    void BuildSprites(Monitor m)
    {
        var b = m.Bounds;
        float s = m.Scale;
        var sp = _sprites;

        // Back to front: air, sky (aurora, moon), clouds, then everything that happens on the windows.
        AddSmogSprites(m);
        AddAuroraSprites(m);
        AddMoonSprites(m);
        AddCloudSprites(m);
        AddTornadoSprites(m);

        // Sun glow: light catching the top edge of each window. Drawn from "additive" sprites (colour, zero alpha),
        // so it only ever brightens what is under it; it can never muddy a dark window brown.
        if (Cur.Glow > 0.01f)
        {
            float elev = Math.Clamp(SunElev, 0, 60) / 60f;
            // Low sun is warmer and a little stronger; high sun is a paler, whiter light.
            float wr = 1f, wg = 0.80f + 0.12f * elev, wb = 0.58f + 0.24f * elev;
            float strength = Cur.Glow * (0.55f + 0.15f * (1 - elev)) * (1 - 0.45f * Cur.Smog) * Mod("Sun.Glow");
            float side = Math.Clamp(SunX * 1.6f, -1, 1); // which end of each edge faces the sun
            foreach (var w in Tracker.Z)
            {
                if (!w.Eligible || w.Spans.Count == 0) continue;
                if (w.R.Right < b.Left || w.R.Left > b.Right || w.R.Top < b.Top || w.R.Top > b.Bottom) continue;
                float y = w.R.Top;
                float up = 7 * s, down = 21 * s; // rim at a quarter of the sprite: a little light above, bloom below
                float seg = 16 * s;
                foreach (var (ca, cb) in w.Spans)
                {
                    float x0 = w.R.Left + ca * w.ColW, x1 = MathF.Min(w.R.Right, w.R.Left + cb * w.ColW);
                    for (float x = x0; x < x1; x += seg)
                    {
                        float xe = MathF.Min(x1, x + seg);
                        float u = ((x + xe) * 0.5f - w.R.Left) / MathF.Max(1, w.R.Width); // 0 left .. 1 right
                        float toward = side >= 0 ? u : 1 - u;                         // 0 far end .. 1 sun end
                        float lit = MathF.Abs(side) < 0.15f ? 0.65f : 0.22f + 0.78f * toward * toward * (3 - 2 * toward);
                        // Ends of the edge taper so the light does not stop abruptly.
                        float d = MathF.Min(x - w.R.Left, w.R.Right - xe) / (30 * s);
                        float taper = Math.Clamp(d, 0, 1);
                        taper = 0.25f + 0.75f * taper * taper * (3 - 2 * taper);
                        sp.Add(Atlas.EdgeLight, x, y - up, xe, y + down, Pm(wr, wg, wb, strength * lit * taper));
                    }
                }
                // A soft flare where the light hits the sun-side corner.
                if (MathF.Abs(side) >= 0.15f && w.R.Width > 200 * s)
                {
                    float fx = side > 0 ? w.R.Right - 14 * s : w.R.Left + 14 * s, fr = 44 * s;
                    sp.Add(Atlas.Flare, fx - fr, y - fr * 0.55f, fx + fr, y + fr * 0.55f, Pm(wr, wg, wb, strength * 0.6f * MathF.Abs(side)));
                }
                // A glint slides along the edge when the window moves.
                double gt = Now - w.GlintStart;
                if (gt >= 0 && gt < 0.8)
                {
                    float p = (float)(gt / 0.8);
                    float gx = w.GlintDir > 0 ? w.R.Left + w.R.Width * p : w.R.Right - w.R.Width * p;
                    float a = MathF.Sin(p * MathF.PI) * 0.8f * Cur.Glow;
                    float gs = 30 * s;
                    sp.Add(Atlas.Flare, gx - gs, y - gs * 0.45f, gx + gs, y + gs * 0.45f, Pm(1f, 0.95f, 0.85f, a));
                }
            }
        }

        // Puddles sit on the edge under any snow.
        AddWaterSprites(m);

        // Snow piles: a body column plus a round cap per column; overlapping caps make the lumpy top.
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.Spans.Count == 0 || w.PileTotal < 0.3f) continue;
            if (w.R.Right < b.Left || w.R.Left > b.Right || w.R.Top < b.Top - 2 || w.R.Top - 20 * s > b.Bottom) continue;
            float y = w.R.Top + 1;
            var h = w.H;
            foreach (var (ca, cb) in w.Spans)
                for (int c = ca; c < cb; c++)
                {
                    float hh = h[c];
                    if (hh < 0.5f) continue;
                    hh += w.Jit[c] * 0.6f * s * Math.Min(1, hh / (5 * s));
                    float x = w.R.Left + c * w.ColW;
                    float capR = Math.Clamp(hh * 0.55f, 1f, w.ColW * 1.45f);
                    float top = y - hh;
                    if (hh > capR)
                        sp.Add(Atlas.Body, x - 0.25f, top + capR, x + w.ColW + 0.25f, y, Pm(1, 1, 1, 0.97f));
                    float cx = x + w.ColW * 0.5f;
                    sp.Add(Atlas.Cap, cx - capR, top, cx + capR, top + capR * 2, Pm(1, 1, 1, 0.98f));
                }
        }

        // Leaves (resting ones sit on top of any snow).
        AddLeafSprites(m);

        // Beads on edges.
        foreach (var w in Tracker.Z)
        {
            if (w.Beads.Count == 0 || w.Spans.Count == 0) continue;
            float r = 3.2f * w.Scale, y = w.R.Top;
            foreach (var bx in w.Beads)
            {
                float x = w.R.Left + bx;
                if (x < b.Left || x > b.Right) continue;
                if (!InSpans(w, (int)(bx / w.ColW))) continue;
                sp.Add(Atlas.Bead, x - r, y - r * 1.7f, x + r, y + r * 0.3f, new Color4(1, 1, 1, 1));
            }
        }

        // Falling snow.
        for (int i = 0; i < _nFlakes; i++)
        {
            ref var f = ref _flakes[i];
            if (f.X < b.Left - 20 || f.X > b.Right + 20 || f.Y < b.Top - 20 || f.Y > b.Bottom + 20) continue;
            float r = f.Size * s * 0.5f * 1.35f; // sprite includes a soft margin
            var src = r > 6 ? Atlas.Flake32 : r > 3 ? Atlas.Flake16 : Atlas.Flake8;
            float a = MathF.Min(1, Cur.Snow * 1.5f + 0.25f) * (0.75f + 0.25f * MathF.Min(1, f.Size / 5));
            sp.Add(src, f.X - r, f.Y - r, f.X + r, f.Y + r, new Color4(1, 1, 1, a));
        }

        // Snow and water debris (knocked off piles, splashes, runoff).
        for (int i = 0; i < _nDebris; i++)
        {
            ref var d = ref _debris[i];
            if (d.X < b.Left - 20 || d.X > b.Right + 20 || d.Y < b.Top - 20 || d.Y > b.Bottom + 20) continue;
            float r = d.Size * 0.5f * (d.Water != 0 ? 1f : 1.35f);
            if (d.Water != 0)
            {
                float a = Math.Clamp(d.Life, 0, 1) * 0.8f;
                sp.Add(r > 2.2f ? Atlas.Bead : Atlas.Dot, d.X - r, d.Y - r, d.X + r, d.Y + r, new Color4(1, 1, 1, a));
            }
            else
                sp.Add(r > 3 ? Atlas.Flake16 : Atlas.Flake8, d.X - r, d.Y - r, d.X + r, d.Y + r, new Color4(1, 1, 1, 1));
        }

        // Rain streaks, tilted by the wind.
        if (_nDrops > 0)
        {
            float vx = WindX * 4.5f;
            for (int i = 0; i < _nDrops; i++)
            {
                ref var d = ref _drops[i];
                if (d.X < b.Left - 40 || d.X > b.Right + 40 || d.Y < b.Top - 60 || d.Y > b.Bottom + 60) continue;
                float ang = MathF.Atan2(-vx * m.Scale, d.Vy); // tilt away from vertical
                float half = 1.6f * s;
                float a = d.Alpha * MathF.Min(1, MathF.Max(Cur.Rain, Cur.Storm) * 1.6f + 0.2f);
                // Head at (X, Y); the streak extends up behind it.
                var xf = Matrix3x2.CreateRotation(ang, new Vector2(d.X, d.Y));
                sp.AddRotated(Atlas.Streak, d.X - half, d.Y - d.Len, d.X + half, d.Y, new Color4(1, 1, 1, a), xf);
            }
        }

        // Hail pellets, then frost in the corners on top of everything.
        AddHailSprites(m);
        AddFrostSprites(m);

        // Clear night: a rare faint twinkle.
        foreach (var t in _twinkles)
        {
            if (t.X < b.Left || t.X > b.Right || t.Y < b.Top || t.Y > b.Bottom) continue;
            float p = t.T / 1.8f;
            float a = MathF.Sin(p * MathF.PI) * 0.4f * Cur.Night;
            float r = t.Size * (0.7f + 0.3f * MathF.Sin(p * MathF.PI));
            sp.Add(Atlas.Glint, t.X - r, t.Y - r, t.X + r, t.Y + r, new Color4(1, 1, 1, a));
        }

        AddSnowballSprites(m);
        // Flood water in front of everything on the desktop.
        AddFloodSprites(m);
    }
}