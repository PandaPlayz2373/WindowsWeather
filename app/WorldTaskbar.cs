using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// Taskbar icons that fall off in an earthquake and float away in a flood. The taskbar is captured once with GDI
// (the overlay is not in a GDI capture) and split into icons wherever the pixels differ from the taskbar colour.
// Each icon is cut out of its background (they flew off "as screenshots ... the apps bring a piece
// of the task bar with it. it shouldnt"): the taskbar around it is flooded in from the tile's edges, made transparent
// with a soft edge, and the tile is trimmed to the icon. The spot it leaves is painted with the taskbar itself, each
// row blended from the taskbar just left of it to just right of it, so no flat patch shows. The real taskbar is
// untouched and still clickable through the overlay. When the disaster is over the icons fly home and the snapshot
// is thrown away.
public sealed partial class World
{
    sealed class TaskTile
    {
        public float HomeX, HomeY, W, H, X, Y, Vx, Vy, Ang, Spin, Delay, P, SX, SY, SA;
        public int SrcX, SrcY;           // where the icon sits in the captured strip (trimmed to the icon itself)
        public int CoverX, CoverW;       // the columns painted over while it is away (strip coordinates)
        public byte State; // 0 home, 1 loose, 2 flying home
        // Carried by the tornado: orbiting the funnel at height CF (1 = ground, 0 = cloud) and angle CA, until it is
        // thrown out at height ThrowF.
        public bool Caught;
        public float CF, CA, CW, ThrowF, Rise;
    }
    readonly List<TaskTile> _tiles = new();
    byte[]? _tbPx, _tbCleanPx;           // icons cut out (premultiplied alpha) / the taskbar with the icons painted out
    int _tbW, _tbH, _tbLeft, _tbTop;
    ID2D1Bitmap1? _tbBmp, _tbClean;
    Monitor? _tbMon;
    bool _tbDispose;
    double _tbHomeAt = -1;

    bool TilesLoose { get { foreach (var t in _tiles) if (t.State != 0) return true; return false; } }

    static int Diff(uint c, int r, int g, int b) =>
        Math.Abs((int)(c >> 16 & 255) - r) + Math.Abs((int)(c >> 8 & 255) - g) + Math.Abs((int)(c & 255) - b);

    double _tbFailAt = -10;

    bool CaptureTaskbar(Tracked? bar = null)
    {
        if (_tbPx != null) return _tiles.Count > 0;
        if (bar == null) foreach (var w in Tracker.Z) if (w.IsTaskbar) { bar = w; break; }
        if (bar == null) return false;
        var r = bar.R;
        int w0 = r.Width, h0 = r.Height;
        if (w0 < 100 || h0 < 20 || h0 > 200) return false;
        var mon = Tracker.MonitorAt(Monitors, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        if (mon == null) return false;
        var px = Native.CaptureScreen(r.Left, r.Top, w0, h0);
        if (px == null) return false;

        // The taskbar colour: the most common colour in the middle rows (only used to find where the icons are).
        var counts = new Dictionary<uint, int>();
        for (int y = h0 / 4; y < h0 * 3 / 4; y += 2)
            for (int x = 0; x < w0; x += 2) { uint c = px[y * w0 + x] & 0xF8F8F8; counts[c] = counts.GetValueOrDefault(c) + 1; }
        uint bgq = 0; int best = -1;
        foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; bgq = kv.Key; }
        long rs = 0, gs = 0, bs = 0, n = 0;
        for (int i = 0; i < px.Length; i += 3) if ((px[i] & 0xF8F8F8) == bgq) { rs += px[i] >> 16 & 255; gs += px[i] >> 8 & 255; bs += px[i] & 255; n++; }
        if (n == 0) return false;
        int br = (int)(rs / n), bgc = (int)(gs / n), bb = (int)(bs / n);

        // Columns that hold something (an icon, the search box, the clock), grouped into tiles.
        var col = new bool[w0];
        int y0 = (int)(h0 * 0.15f), y1 = (int)(h0 * 0.85f);
        for (int x = 0; x < w0; x++)
        {
            int k = 0;
            for (int y = y0; y < y1; y++) if (Diff(px[y * w0 + x], br, bgc, bb) > 60 && ++k >= 2) break;
            col[x] = k >= 2;
        }
        // Runs of content columns, split at gaps wider than `gap` pixels.
        List<(int a, int e)> Runs(int from, int to, int gap)
        {
            var o = new List<(int a, int e)>();
            int start = -1, last = -1;
            for (int x = from; x <= to + 1; x++)
            {
                bool on = x <= to && col[x];
                if (on) { if (start < 0) start = x; last = x; }
                else if (start >= 0 && (x > to || x - last > gap)) { o.Add((start, last)); start = -1; }
            }
            return o;
        }
        var groups = new List<(int a, int e)>();
        foreach (var (s0, e0) in Runs(0, w0 - 1, 6))
        {
            // The tray (network, volume, battery) and the clock sit close together: split a wide run at its smaller
            // gaps so each becomes its own icon instead of one slab (or being skipped as too wide).
            var parts = e0 - s0 + 1 > 110 ? Runs(s0, e0, 2) : new List<(int a, int e)> { (s0, e0) };
            if (parts.Count == 1 && e0 - s0 + 1 > 320) parts = Runs(s0, e0, 1);
            foreach (var (ps, pe) in parts)
            {
                int a = Math.Max(0, ps - 3), e = Math.Min(w0 - 1, pe + 3);
                if (e - a + 1 >= 8 && e - a + 1 <= 320) groups.Add((a, e));
            }
        }
        // Neighbouring tiles must not overlap (each one's background is measured just outside it).
        for (int i = 1; i < groups.Count; i++)
            if (groups[i].a <= groups[i - 1].e) { int mid = (groups[i].a + groups[i - 1].e) / 2; groups[i - 1] = (groups[i - 1].a, mid - 1); groups[i] = (mid + 1, groups[i].e); }
        groups.RemoveAll(g => g.e - g.a + 1 < 8);
        if (groups.Count == 0) return false;

        var icon = new byte[w0 * h0 * 4];   // transparent everywhere except the icons
        var clean = new byte[w0 * h0 * 4];  // the taskbar as captured; the icons are painted out below
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i];
            clean[i * 4] = (byte)c; clean[i * 4 + 1] = (byte)(c >> 8); clean[i * 4 + 2] = (byte)(c >> 16); clean[i * 4 + 3] = 255;
        }
        _tiles.Clear();
        const int Tol = 34;
        var queue = new Queue<int>();
        foreach (var (a, e) in groups)
        {
            int tw = e - a + 1;
            // Each row's own taskbar colour, from just outside the tile (the taskbar is not one flat colour).
            var rowR = new int[h0]; var rowG = new int[h0]; var rowB = new int[h0];
            var left = new uint[h0]; var right = new uint[h0];
            for (int y = 0; y < h0; y++)
            {
                uint l = px[y * w0 + Math.Max(0, a - 1)], rr = px[y * w0 + Math.Min(w0 - 1, e + 1)];
                left[y] = l; right[y] = rr;
                rowR[y] = (int)((l >> 16 & 255) + (rr >> 16 & 255)) / 2;
                rowG[y] = (int)((l >> 8 & 255) + (rr >> 8 & 255)) / 2;
                rowB[y] = (int)((l & 255) + (rr & 255)) / 2;
            }
            int RowDiff(int x, int y) => Diff(px[y * w0 + x], rowR[y], rowG[y], rowB[y]);

            // Flood the background in from the tile's border: what it reaches is taskbar, what it can't is the icon.
            var bgm = new bool[tw * h0];
            void Seed(int x, int y)
            {
                int k = y * tw + (x - a);
                if (bgm[k] || RowDiff(x, y) >= Tol) return;
                bgm[k] = true; queue.Enqueue(k);
            }
            for (int x = a; x <= e; x++) { Seed(x, 0); Seed(x, h0 - 1); }
            for (int y = 0; y < h0; y++) { Seed(a, y); Seed(e, y); }
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                int y = k / tw, x = a + k % tw;
                if (x > a) Seed(x - 1, y);
                if (x < e) Seed(x + 1, y);
                if (y > 0) Seed(x, y - 1);
                if (y < h0 - 1) Seed(x, y + 1);
            }
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < h0; y++)
                for (int x = a; x <= e; x++)
                {
                    int k = y * tw + (x - a);
                    if (bgm[k]) continue;
                    // A soft edge: pixels touching the background fade with how close their colour is to it.
                    bool edge = (x > a && bgm[k - 1]) || (x < e && bgm[k + 1]) || (y > 0 && bgm[k - tw]) || (y < h0 - 1 && bgm[k + tw]);
                    float al = edge ? Math.Clamp((RowDiff(x, y) - 10) / 50f, 0.25f, 1f) : 1f;
                    uint c = px[y * w0 + x];
                    int i = (y * w0 + x) * 4;
                    icon[i] = (byte)((c & 255) * al); icon[i + 1] = (byte)((c >> 8 & 255) * al);
                    icon[i + 2] = (byte)((c >> 16 & 255) * al); icon[i + 3] = (byte)(255 * al);
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            if (maxX < 0 || maxX - minX < 3 || maxY - minY < 3) continue; // nothing solid: it was only taskbar
            // The patch has to cover the whole button, not just the icon: the active app's button has a lighter rounded
            // highlight wider than its icon. Widen it until the plain taskbar colour, at most 22 px each side.
            bool Plain(int x) => Diff(px[(h0 / 4) * w0 + x], br, bgc, bb) < 26 && Diff(px[(h0 / 2) * w0 + x], br, bgc, bb) < 26 && Diff(px[(h0 * 3 / 4) * w0 + x], br, bgc, bb) < 26;
            int ca = a, ce = e;
            while (ca > 0 && a - ca < 22 && !Plain(ca - 1)) ca--;
            while (ce < w0 - 1 && ce - e < 22 && !Plain(ce + 1)) ce++;
            int cw = ce - ca + 1;
            // Paint the icon out of the clean copy, each row blending from the taskbar on its left to that on its right.
            for (int y = 0; y < h0; y++)
            {
                uint l = px[y * w0 + Math.Max(0, ca - 1)], rr = px[y * w0 + Math.Min(w0 - 1, ce + 1)];
                for (int x = ca; x <= ce; x++)
                {
                    float f = (x - ca + 1) / (float)(cw + 1);
                    int i = (y * w0 + x) * 4;
                    clean[i] = (byte)((l & 255) * (1 - f) + (rr & 255) * f);
                    clean[i + 1] = (byte)((l >> 8 & 255) * (1 - f) + (rr >> 8 & 255) * f);
                    clean[i + 2] = (byte)((l >> 16 & 255) * (1 - f) + (rr >> 16 & 255) * f);
                }
            }
            int iw = maxX - minX + 1, ih = maxY - minY + 1;
            _tiles.Add(new TaskTile
            {
                SrcX = minX, SrcY = minY, W = iw, H = ih, CoverX = ca, CoverW = cw,
                HomeX = r.Left + minX, HomeY = r.Top + minY, X = r.Left + minX, Y = r.Top + minY,
            });
        }
        Log.Write($"taskbar capture {w0}x{h0}: {groups.Count} groups, {_tiles.Count} icons: " + string.Join(" ", _tiles.Select(t => $"{t.SrcX}+{(int)t.W}")) +
                  " | groups " + string.Join(" ", groups.Select(g => $"{g.a}-{g.e}")));
        if (_tiles.Count == 0) return false;
        _tbPx = icon; _tbCleanPx = clean; _tbW = w0; _tbH = h0; _tbLeft = r.Left; _tbTop = r.Top; _tbMon = mon;
        return true;
    }

    void KnockTaskbar()
    {
        if (!CaptureTaskbar()) return;
        float s = _tbMon!.Scale;
        foreach (var t in _tiles)
        {
            t.State = 1;
            t.Vy = -(280 + 380 * Rf()) * s * MathF.Min(1.4f, _qMag);
            t.Vx = (Rf() - 0.5f) * 320 * s;
            t.Spin = (Rf() - 0.5f) * 9;
            t.Delay = Rf() * 0.6f;
        }
        _tbHomeAt = -1;
        Kick(1);
    }

    void FloatTaskbar()
    {
        // Only once the water is over the taskbar.
        Tracked? bar = null;
        foreach (var w in Tracker.Z) if (w.IsTaskbar) { bar = w; break; }
        if (bar == null) return;
        var m = Tracker.MonitorAt(Monitors, (bar.R.Left + bar.R.Right) / 2, (bar.R.Top + bar.R.Bottom) / 2);
        if (m == null || FloodY(m, (bar.R.Left + bar.R.Right) * 0.5f) > bar.R.Top + bar.R.Height * 0.3f) return;
        if (!CaptureTaskbar()) return;
        foreach (var t in _tiles)
            if (t.State != 1) { t.State = 1; t.Vx = (Rf() - 0.5f) * 30; t.Vy = -20; t.Spin = 0; t.Delay = Rf() * 1.2f; }
        _tbHomeAt = -1;
    }

    void StepTiles(float dt)
    {
        if (_tiles.Count == 0) return;
        var m = _tbMon!;
        var b = m.Bounds;
        float s = m.Scale;
        float barMid = _tbTop + _tbH * 0.3f;
        bool disturbed = (_qOn && QuakeAmp > 0.03f) || (_flLevel > 0 && FloodY(m, b.Left + b.Width * 0.5f) < barMid) || (_torOn && _torMon == m);
        bool anyLoose = false;
        foreach (var t in _tiles)
        {
            if (t.State == 1 && t.Caught)
            {
                anyLoose = true;
                if (!_torOn || _torMon != m) { t.Caught = false; t.Vy = 0; continue; }
                // Carried up the funnel ("carry them up a little more for a little more destruction"):
                // round and round, climbing, spinning, then flung out of the side near the top.
                t.CF = MathF.Min(t.CF - t.Rise * dt, _torTouch);
                t.CA += t.CW * dt;
                float f = MathF.Max(0, t.CF), r = TorR(f) * 0.95f;
                float nx = TorAxis(f) + MathF.Cos(t.CA) * r - t.W * 0.5f;
                float ny = TorY(f) + MathF.Sin(t.CA) * r * 0.14f - t.H * 0.5f;
                float k = MathF.Min(1, dt * 8); // about 0.3 s to swing into the orbit, then it follows closely
                float ox = t.X, oy = t.Y;
                t.X += (nx - t.X) * k; t.Y += (ny - t.Y) * k;
                t.Vx = (t.X - ox) / MathF.Max(dt, 0.001f); t.Vy = (t.Y - oy) / MathF.Max(dt, 0.001f);
                t.Ang += t.Spin * dt;
                if (t.CF <= t.ThrowF)
                {
                    t.Caught = false;
                    float side = MathF.Cos(t.CA) >= 0 ? 1 : -1;
                    t.Vx = side * (500 + 500 * Rf()) * s;
                    t.Vy = -(150 + 250 * Rf()) * s;
                }
                continue;
            }
            if (t.State == 1)
            {
                anyLoose = true;
                if (t.Delay > 0) { t.Delay -= dt; continue; }
                float wy = FloodY(m, t.X + t.W * 0.5f);
                if (_flLevel > 0 && wy < barMid && wy < b.Bottom)
                {
                    // Floating: held at the surface, bobbing and tipping with the waves, drifting on the current.
                    float target = wy - t.H * 0.55f;
                    t.Vy += ((target - t.Y) * 9 - t.Vy * 3f) * dt;
                    t.Vx += (MathF.Sin((float)Now * 0.3f + t.HomeX * 0.01f) * 40 * s - t.Vx * 0.8f) * dt;
                    float tilt = (FloodY(m, t.X + t.W) - FloodY(m, t.X)) / MathF.Max(1, t.W);
                    t.Ang += (MathF.Atan(tilt) - t.Ang) * MathF.Min(1, dt * 3);
                }
                else
                {
                    t.Vy += 1500 * s * dt;
                    t.Ang += t.Spin * dt;
                }
                if (_torOn && _torMon == m)
                {
                    // Caught in the funnel: spun across it, lifted, thrown out of the top.
                    TornadoPull(t.X + t.W * 0.5f, t.Y + t.H * 0.5f, s, dt, 2300 * s, ref t.Vx, ref t.Vy);
                    if (TorInfluence(t.X + t.W * 0.5f, t.Y + t.H * 0.5f, out _, out _) > 0.2f) t.Ang += t.Spin * dt * 0.5f;
                }
                if (_qOn && QuakeAmp > 0.2f && t.Y >= t.HomeY - 1 && t.Vy > 0 && Rf() < QuakeAmp * dt * 3) t.Vy = -(150 + 250 * Rf()) * s; // another jolt
                t.X += t.Vx * dt; t.Y += t.Vy * dt;
                if (t.X < b.Left - t.W) t.X = b.Left - t.W;
                if (t.X > b.Right) t.X = b.Right;
                if (t.Y > b.Bottom + 60 * s) { t.Y = b.Bottom + 60 * s; t.Vy = 0; t.Vx = 0; t.Spin = 0; } // fell off the screen
            }
            else if (t.State == 2)
            {
                anyLoose = true;
                if (t.Delay > 0) { t.Delay -= dt; continue; }
                t.P = MathF.Min(1, t.P + dt / 0.9f);
                float e = 1 - MathF.Pow(1 - t.P, 3);
                t.X = t.SX + (t.HomeX - t.SX) * e; t.Y = t.SY + (t.HomeY - t.SY) * e - MathF.Sin(t.P * MathF.PI) * 40 * s; t.Ang = t.SA * (1 - e);
                if (t.P >= 1) { t.State = 0; t.X = t.HomeX; t.Y = t.HomeY; t.Ang = 0; }
            }
        }
        if (disturbed) _tbHomeAt = -1;
        else if (_tbHomeAt < 0) _tbHomeAt = Now + 2;
        else if (Now >= _tbHomeAt)
        {
            foreach (var t in _tiles)
                if (t.State == 1) { t.State = 2; t.P = 0; t.SX = t.X; t.SY = t.Y; t.SA = t.Ang; t.Delay = Rf() * 0.8f; }
        }
        if (!anyLoose && !(_torOn && _torMon == m)) SnapTilesHome();
    }

    void SnapTilesHome() { _tiles.Clear(); _tbPx = null; _tbCleanPx = null; _tbDispose = true; _tbHomeAt = -1; }

    /// <summary>cover: paint the taskbar back over the empty spots (before everything else); otherwise draw the icons.</summary>
    void DrawTiles(Gpu gpu, Monitor m, bool cover)
    {
        if (_tbDispose && _tiles.Count == 0) { _tbBmp?.Dispose(); _tbBmp = null; _tbClean?.Dispose(); _tbClean = null; _tbDispose = false; }
        if (_tiles.Count == 0 || _tbMon != m || _tbPx == null || _tbCleanPx == null) return;
        var ctx = gpu.Ctx;
        if (cover)
        {
            _tbClean ??= gpu.MakeBitmap(_tbW, _tbH, _tbCleanPx);
            foreach (var t in _tiles)
                if (t.State != 0)
                    ctx.DrawBitmap(_tbClean, new Rect(_tbLeft + t.CoverX, _tbTop, t.CoverW, _tbH), 1f,
                        BitmapInterpolationMode.NearestNeighbor, new Rect(t.CoverX, 0, t.CoverW, _tbH));
            return;
        }
        _tbBmp ??= gpu.MakeBitmap(_tbW, _tbH, _tbPx);
        var baseXf = ctx.Transform;
        foreach (var t in _tiles)
        {
            if (t.State == 0) continue;
            var c = new Vector2(t.X + t.W * 0.5f, t.Y + t.H * 0.5f);
            ctx.Transform = Matrix3x2.CreateRotation(t.Ang, c) * baseXf;
            ctx.DrawBitmap(_tbBmp, new Rect(t.X, t.Y, t.W, t.H), 1f, BitmapInterpolationMode.Linear, new Rect(t.SrcX, t.SrcY, t.W, t.H));
        }
        ctx.Transform = baseXf;
    }
}
