namespace WindowsWeather;

// Flood ("maybe add floods"). Murky water rises from the bottom of the screen over ~20 s, sloshes
// with waves, holds, then drains away, and comes back a little later while the effect is on. Windows whose bottom
// edge goes under float up and bob on the current (WorldGrabs), the taskbar icons float off the taskbar and bob on
// the surface (WorldTaskbar), leaves float, rain and snow end at the surface, and snow piles under water melt.
public sealed partial class World
{
    float _flLevel, _flPeak, _flT;
    int _flStage;                // 0 waiting, 1 rising, 2 holding, 3 draining
    double _flNext, _flHoldUntil;

    public void FloodNow() { _flNext = 0; if (_flStage == 0 || _flStage == 3) { _flStage = 1; _flPeak = PeakLevel(); } Kick(1); }

    float PeakLevel() => Math.Clamp((0.30f + 0.14f * Rf()) * Mod("Flood.Height"), 0.08f, 0.85f);

    void StepFlood(float dt)
    {
        bool want = Cur.Flood > 0.4f;
        switch (_flStage)
        {
            case 0:
                if (want && Now >= _flNext) { _flStage = 1; _flPeak = PeakLevel(); }
                break;
            case 1:
                _flLevel += dt * _flPeak / 20f;
                if (!want) _flStage = 3;
                else if (_flLevel >= _flPeak) { _flLevel = _flPeak; _flStage = 2; _flHoldUntil = Now + 10 + Rf() * 6; }
                break;
            case 2:
                if (!want || Now >= _flHoldUntil) _flStage = 3;
                break;
            case 3:
                _flLevel -= dt * MathF.Max(0.02f, _flPeak / 14f) * (want ? 1 : 2);
                if (_flLevel <= 0) { _flLevel = 0; _flStage = 0; _flNext = Now + 6 + Rf() * 6; }
                break;
        }
        if (_flLevel <= 0) return;
        _flT += dt;
        // Snow and puddles under water are gone; resting leaves come loose and float.
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0 || (w.PileTotal < 0.05f && w.WaterTotal < 0.05f)) continue;
            var m = Tracker.MonitorAt(Monitors, (w.R.Left + w.R.Right) / 2, w.R.Top);
            if (m == null || w.R.Top < FloodY(m, (w.R.Left + w.R.Right) * 0.5f)) continue;
            float e = MathF.Exp(-dt * 1.5f);
            for (int c = 0; c < w.H.Length; c++) w.H[c] *= e;
            if (w.Wt.Length > 0) { Array.Clear(w.Wt); w.WaterTotal = 0; }
            w.Beads.Clear();
            RecountPile(w);
        }
        if (_flStage == 1 || _flStage == 2) FloatTaskbar();
    }

    /// <summary>The water surface at x (screen px), or far below the screen when there is no flood.</summary>
    float FloodY(Monitor m, float x)
    {
        if (_flLevel <= 0) return 1e9f;
        float s = m.Scale, h = m.Bounds.Height;
        float y = m.Bounds.Bottom - _flLevel * h;
        float amp = MathF.Min(1, _flLevel * 8) * Mod("Flood.Waves");
        return y + amp * (6 * s * MathF.Sin(x * 0.011f / s + _flT * 1.3f) + 3.5f * s * MathF.Sin(x * 0.029f / s - _flT * 2.2f)
                          + 2 * s * MathF.Sin(x * 0.061f / s + _flT * 3.1f));
    }

    void AddFloodSprites(Monitor m)
    {
        if (_flLevel <= 0) return;
        var b = m.Bounds;
        float s = m.Scale, step = 6 * s;
        float fade = MathF.Min(1, _flLevel * 25);
        var sp = _sprites;
        for (float x = b.Left; x < b.Right; x += step)
        {
            float y = FloodY(m, x + step * 0.5f);
            if (y >= b.Bottom) continue;
            sp.Add(Atlas.Solid, x, y, x + step + 0.5f, b.Bottom, Pm(0.20f, 0.31f, 0.33f, 0.36f * fade));
            if (y + 60 * s < b.Bottom) sp.Add(Atlas.Solid, x, y + 60 * s, x + step + 0.5f, b.Bottom, Pm(0.08f, 0.15f, 0.18f, 0.22f * fade));
            sp.Add(Atlas.Solid, x, y - 1.2f * s, x + step + 0.5f, y + 1.3f * s, Pm(0.80f, 0.88f, 0.92f, 0.45f * fade));
            // Foam flecks riding the crests.
            int hsh = (int)((x - b.Left) / step) * 7919 + (int)(_flT * 2);
            if ((hsh & 15) == 0) { float fx = x + (hsh >> 4 & 7) * 0.7f * s; sp.Add(Atlas.Dot, fx - 2 * s, y - 2.5f * s, fx + 2 * s, y + 1.5f * s, Pm(0.9f, 0.95f, 1f, 0.55f * fade)); }
        }
    }
}
