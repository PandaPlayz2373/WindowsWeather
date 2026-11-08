namespace WindowsWeather;

// Real windows borrowed by the disasters (tornado, flood, earthquake). Each one remembers exactly where it was; forces
// from whichever disaster is acting on it move it with async SetWindowPos, and once nothing has touched it for 3 s it
// glides home. RestoreWindows() puts everything back at once (pause, quit, effect off). The user always wins: a window
// they drag, minimize or maximize is let go wherever it is. Maximized, minimized, topmost and tool windows are never
// picked up (the earthquake un-maximizes some on purpose and maximizes them again when it is over).
public sealed partial class World
{
    sealed class Grab
    {
        public nint H; public Tracked? T; public Monitor M = null!;
        public int OX, OY, LX, LY, Wd, Ht;
        public float Ox, Oy, Vx, Vy, Mass, Calm, Held, Immune, ThrowAt, Phase;
        public bool Home;
    }
    readonly List<Grab> _grabs = new();
    double _grabScanAt;

    /// <summary>Testing: only windows whose title starts with this are touched (null = any window).</summary>
    public string? GrabOnlyTitle;

    const uint MoveFlags = 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000; // NOSIZE NOZORDER NOACTIVATE NOOWNERZORDER ASYNCWINDOWPOS

    public bool TornadoBusy => _torOn || _grabs.Count > 0 || DisasterBusy;

    bool Touchable(Tracked t)
    {
        if (t.IsTaskbar || t.IsToast || t.Maximized || t.R.Width < 120 || t.R.Height < 80) return false;
        if (Native.IsIconic(t.Hwnd) != 0 || Native.IsZoomed(t.Hwnd) != 0) return false;
        long ex = Native.GetWindowLongPtrW(t.Hwnd, -20);
        if ((ex & 0x8) != 0 || (ex & 0x80) != 0) return false; // topmost and tool windows stay put
        return GrabOnlyTitle == null || Native.WindowText(t.Hwnd).StartsWith(GrabOnlyTitle);
    }

    unsafe void ScanGrabs(bool tornado, bool flood, bool quake)
    {
        foreach (var t in Tracker.Z)
        {
            bool have = false;
            foreach (var g in _grabs) if (g.H == t.Hwnd) { have = true; break; }
            if (have || t.IsTaskbar || t.IsToast || t.Maximized) continue;
            var m = Tracker.MonitorAt(Monitors, (t.R.Left + t.R.Right) / 2, (t.R.Top + t.R.Bottom) / 2);
            if (m == null) continue;
            bool want = quake
                || (tornado && _torMon == m && WindowInfluence(t.R.Left, t.R.Top, t.R.Right, t.R.Bottom, out _) >= 0.25f)
                || (flood && t.R.Bottom > FloodY(m, (t.R.Left + t.R.Right) * 0.5f) - 4);
            if (!want || !Touchable(t)) continue;
            RECT wr;
            if (Native.GetWindowRect(t.Hwnd, &wr) == 0) continue;
            float area = (float)wr.Width * wr.Height;
            _grabs.Add(new Grab
            {
                H = t.Hwnd, T = t, M = m, OX = wr.Left, OY = wr.Top, LX = wr.Left, LY = wr.Top, Wd = wr.Width, Ht = wr.Height,
                Mass = 1 + area / 350000f, ThrowAt = 3.5f + Rf() * 2.5f, Phase = Rf() * 6.28f,
            });
        }
    }

    void StepGrabs(float dt)
    {
        bool tor = _torOn && _torTouch > 0.45f, flood = _flLevel > 0.01f, quake = QuakeAmp > 0.05f;
        if ((tor || flood || quake) && Now >= _grabScanAt) { _grabScanAt = Now + (quake ? 0.3 : 0.1); ScanGrabs(tor, flood, quake); }
        if (_grabs.Count == 0) return;
        uint ms = (uint)(App.Seconds * 1000);
        float qa = QuakeAmp;
        for (int i = _grabs.Count - 1; i >= 0; i--)
        {
            var g = _grabs[i];
            // Closed, minimized, maximized or picked up by the user: it is theirs again, wherever it is.
            if (Native.IsWindow(g.H) == 0 || Native.IsIconic(g.H) != 0 || Native.IsZoomed(g.H) != 0 || Tracker.Dragging == g.H) { _grabs.RemoveAt(i); continue; }
            var b = g.M.Bounds;
            float s = g.M.Scale;
            float l = g.OX + g.Ox, t = g.OY + g.Oy;
            bool active = false;

            // Tornado: pulled to the funnel, lifted toward a third of the way down, shaken, thrown out after a few seconds.
            float dx = 0;
            g.Immune -= dt;
            float k = _torMon == g.M && g.Immune <= 0 ? WindowInfluence(l, t, l + g.Wd, t + g.Ht, out dx) : 0;
            if (k > 0.02f)
            {
                active = true;
                float inv = 1 / g.Mass;
                float fy = ((t + g.Ht * 0.5f) - _torTop) / MathF.Max(1, _torGround - _torTop);
                float lift = Math.Clamp((fy - 0.32f) * 2.6f, -0.7f, 1f);
                g.Vx += (-dx * 14f * k + (Rf() - 0.5f) * 1500 * s * k) * inv * dt;
                g.Vy += (-520 * s * k * lift + (Rf() - 0.5f) * 1100 * s * k) * inv * dt;
                if (k > 0.3f) g.Held += dt;
                if (g.Held > g.ThrowAt)
                {
                    float side = dx == 0 ? -_torDir : MathF.Sign(dx);
                    if (side == _torDir) side = Rf() < 0.6f ? -_torDir : _torDir;
                    g.Vx = side * (900 + 500 * Rf()) * s / MathF.Sqrt(g.Mass);
                    g.Vy = -(200 + 250 * Rf()) * s / MathF.Sqrt(g.Mass);
                    g.Immune = 2.5f; g.Held = 0; g.ThrowAt = 3.5f + Rf() * 2.5f;
                }
            }

            // Flood: floats with its bottom a little under the surface, bobbing and drifting on the current.
            if (_flLevel > 0.005f)
            {
                float wy = FloodY(g.M, l + g.Wd * 0.5f), bottom = t + g.Ht;
                if (bottom > wy)
                {
                    active = true;
                    float depth = MathF.Min(g.Ht, 220 * s);
                    float sub = Math.Clamp((bottom - wy) / depth, 0, 1.5f);
                    float push = -(sub - 0.3f) * 2600 * s / g.Mass;
                    // Water only lifts: as it drains a window settles back to its own height, never below it.
                    if (push > 0 && g.Oy >= 0) { push = 0; g.Oy = 0; if (g.Vy > 0) g.Vy = 0; }
                    g.Vy += push * dt;
                    float drag = MathF.Exp(-dt * 2.2f * MathF.Min(sub, 1));
                    g.Vx *= drag; g.Vy *= drag;
                    g.Vx += MathF.Sin((float)Now * 0.25f + g.Phase) * 70 * s * dt;
                }
            }

            // Earthquake: windows walk a little while it shakes (the shake itself is added below, not integrated).
            if (qa > 0.01f)
            {
                g.Calm = 0;
                g.Vx += (Rf() - 0.5f) * qa * 2400 * s / g.Mass * dt;
                g.Vy += (Rf() - 0.5f) * qa * 1200 * s / g.Mass * dt;
            }

            if (active) { g.Calm = 0; g.Home = false; }
            else
            {
                if (qa <= 0.01f) g.Calm += dt;
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
                float minX = b.Left - g.Wd * 0.5f, maxX = b.Right - g.Wd * 0.5f, minY = b.Top, maxY = GroundY(g.M) - 90 * s;
                if (x < minX) { g.Ox = minX - g.OX; g.Vx = MathF.Abs(g.Vx) * 0.3f; }
                if (x > maxX) { g.Ox = maxX - g.OX; g.Vx = -MathF.Abs(g.Vx) * 0.3f; }
                if (y < minY) { g.Oy = minY - g.OY; g.Vy = MathF.Abs(g.Vy) * 0.3f; }
                if (y > MathF.Max(minY, maxY)) { g.Oy = MathF.Max(minY, maxY) - g.OY; g.Vy = -MathF.Abs(g.Vy) * 0.3f; }
            }
            float sx = 0, sy = 0;
            if (qa > 0.01f) { float a = qa * 16 * s / MathF.Sqrt(g.Mass); sx = (Rf() - 0.5f) * 2 * a; sy = (Rf() - 0.5f) * 1.2f * a; }
            int nx = g.OX + (int)MathF.Round(g.Ox + sx), ny = g.OY + (int)MathF.Round(g.Oy + sy);
            if (nx != g.LX || ny != g.LY)
            {
                Native.SetWindowPos(g.H, 0, nx, ny, 0, 0, MoveFlags);
                g.LX = nx; g.LY = ny;
                if (g.T != null && Tracker.ByHwnd.ContainsKey(g.H)) Tracker.UpdateRect(g.T, ms);
            }
            if (g.Home && g.Ox == 0 && g.Oy == 0 && qa <= 0.01f && g.LX == g.OX && g.LY == g.OY) _grabs.RemoveAt(i);
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
        EndDisasters();
    }
}
