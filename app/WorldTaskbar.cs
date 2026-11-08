using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// Taskbar icons that fall off in an earthquake and float away in a flood. The taskbar is captured once with GDI
// (the overlay is not in a GDI capture), split into icon tiles wherever the pixels differ from the taskbar colour,
// and each tile becomes a little object with physics. Its home spot is painted over in the taskbar colour while it is
// away. The real taskbar is untouched and still clickable through the overlay. When the disaster is over the tiles
// fly home and the snapshot is thrown away.
public sealed partial class World
{
    sealed class TaskTile
    {
        public float HomeX, HomeY, W, H, X, Y, Vx, Vy, Ang, Spin, Delay, P, SX, SY, SA;
        public int SrcX;
        public byte State; // 0 home, 1 loose, 2 flying home
    }
    readonly List<TaskTile> _tiles = new();
    byte[]? _tbPx;
    int _tbW, _tbH;
    ID2D1Bitmap1? _tbBmp;
    Monitor? _tbMon;
    Color4 _tbBg;
    bool _tbDispose;
    double _tbHomeAt = -1;

    bool TilesLoose { get { foreach (var t in _tiles) if (t.State != 0) return true; return false; } }

    bool CaptureTaskbar()
    {
        if (_tbPx != null) return _tiles.Count > 0;
        Tracked? bar = null;
        foreach (var w in Tracker.Z) if (w.IsTaskbar) { bar = w; break; }
        if (bar == null) return false;
        var r = bar.R;
        int w0 = r.Width, h0 = r.Height;
        if (w0 < 100 || h0 < 20 || h0 > 200) return false;
        var mon = Tracker.MonitorAt(Monitors, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        if (mon == null) return false;
        var px = Native.CaptureScreen(r.Left, r.Top, w0, h0);
        if (px == null) return false;

        // The taskbar colour: the most common colour in the middle rows.
        var counts = new Dictionary<uint, int>();
        for (int y = h0 / 4; y < h0 * 3 / 4; y += 2)
            for (int x = 0; x < w0; x += 2) { uint c = px[y * w0 + x] & 0xF8F8F8; counts[c] = counts.GetValueOrDefault(c) + 1; }
        uint bgq = 0; int best = -1;
        foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; bgq = kv.Key; }
        long rs = 0, gs = 0, bs = 0, n = 0;
        for (int i = 0; i < px.Length; i += 3) if ((px[i] & 0xF8F8F8) == bgq) { rs += px[i] >> 16 & 255; gs += px[i] >> 8 & 255; bs += px[i] & 255; n++; }
        if (n == 0) return false;
        int br = (int)(rs / n), bgc = (int)(gs / n), bb = (int)(bs / n);
        _tbBg = new Color4(br / 255f, bgc / 255f, bb / 255f, 1);

        // Columns that hold something (an icon, the search box, the clock), grouped into tiles.
        var col = new bool[w0];
        int y0 = (int)(h0 * 0.15f), y1 = (int)(h0 * 0.85f);
        for (int x = 0; x < w0; x++)
        {
            int k = 0;
            for (int y = y0; y < y1; y++)
            {
                uint c = px[y * w0 + x];
                int d = Math.Abs((int)(c >> 16 & 255) - br) + Math.Abs((int)(c >> 8 & 255) - bgc) + Math.Abs((int)(c & 255) - bb);
                if (d > 60 && ++k >= 2) break;
            }
            col[x] = k >= 2;
        }
        _tiles.Clear();
        int start = -1, last = -1;
        for (int x = 0; x <= w0; x++)
        {
            bool on = x < w0 && col[x];
            if (on) { if (start < 0) start = x; last = x; }
            else if (start >= 0 && (x == w0 || x - last > 6))
            {
                int a = Math.Max(0, start - 3), e = Math.Min(w0 - 1, last + 3), wd = e - a + 1;
                if (wd >= 8 && wd <= 320)
                    _tiles.Add(new TaskTile { HomeX = r.Left + a, HomeY = r.Top, W = wd, H = h0, SrcX = a, X = r.Left + a, Y = r.Top });
                start = -1;
            }
        }
        if (_tiles.Count == 0) { _tbPx = null; return false; }
        var bytes = new byte[w0 * h0 * 4];
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i];
            bytes[i * 4] = (byte)c; bytes[i * 4 + 1] = (byte)(c >> 8); bytes[i * 4 + 2] = (byte)(c >> 16); bytes[i * 4 + 3] = 255;
        }
        _tbPx = bytes; _tbW = w0; _tbH = h0; _tbMon = mon;
        return true;
    }

    void KnockTaskbar()
    {
        if (!CaptureTaskbar()) return;
        float s = _tbMon!.Scale;
        foreach (var t in _tiles)
        {
            if (t.State == 2) { t.X = t.X; }
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
        bool disturbed = (_qOn && QuakeAmp > 0.03f) || (_flLevel > 0 && FloodY(m, b.Left + b.Width * 0.5f) < _tiles[0].HomeY + _tiles[0].H * 0.3f);
        bool anyLoose = false;
        foreach (var t in _tiles)
        {
            if (t.State == 1)
            {
                anyLoose = true;
                if (t.Delay > 0) { t.Delay -= dt; continue; }
                float wy = FloodY(m, t.X + t.W * 0.5f);
                if (_flLevel > 0 && wy < t.HomeY + t.H * 0.3f && wy < b.Bottom)
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
        if (!anyLoose) { _tiles.Clear(); _tbPx = null; _tbDispose = true; _tbHomeAt = -1; }
    }

    void SnapTilesHome() { _tiles.Clear(); _tbPx = null; _tbDispose = true; _tbHomeAt = -1; }

    /// <summary>cover: paint the home spots in the taskbar colour (before everything else); otherwise draw the tiles.</summary>
    void DrawTiles(Gpu gpu, Monitor m, bool cover)
    {
        if (_tbDispose && _tiles.Count == 0) { _tbBmp?.Dispose(); _tbBmp = null; _tbDispose = false; }
        if (_tiles.Count == 0 || _tbMon != m || _tbPx == null) return;
        var ctx = gpu.Ctx;
        if (cover)
        {
            gpu.FrostBrush.Color = _tbBg;
            foreach (var t in _tiles)
                if (t.State != 0) ctx.FillRectangle(new Rect(t.HomeX, t.HomeY, t.W, t.H), gpu.FrostBrush);
            return;
        }
        _tbBmp ??= gpu.MakeBitmap(_tbW, _tbH, _tbPx);
        var baseXf = ctx.Transform;
        foreach (var t in _tiles)
        {
            if (t.State == 0) continue;
            var c = new Vector2(t.X + t.W * 0.5f, t.Y + t.H * 0.5f);
            ctx.Transform = Matrix3x2.CreateRotation(t.Ang, c) * baseXf;
            ctx.DrawBitmap(_tbBmp, new Rect(t.X, t.Y, t.W, t.H), 1f, BitmapInterpolationMode.Linear, new Rect(t.SrcX, 0, t.W, t.H));
        }
        ctx.Transform = baseXf;
    }
}
