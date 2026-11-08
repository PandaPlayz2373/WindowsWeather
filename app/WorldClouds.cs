using Vortice.Mathematics;

namespace WindowsWeather;

// Clouds and puddles.
//
// Clouds : "clouds should only appear from the topish area of the screen" and "have the clouds
// brush away... not with a circle mouse but with an entity of a cloud moving". So a cloud is a thing: a cluster of
// soft puffs drifting through the top of the screen. The cursor shoves the puffs it passes through along with its
// motion, the cloud slides away and thins, then slowly gathers itself back together.
//
// Puddles : "rain should turn to puddles". Rain collects as water on window tops and the taskbar,
// spreads flat, ripples where drops land, drips off the ends, rides along with a slow move, spills when flung.
public sealed partial class World
{
    // Cloud layers : "figure out the level of the clouds in the sky and draw them proportionally to
    // the level on the screen... foggy would be covering the screen, with the visual see distance as how thick the fog
    // is". Open-Meteo reports cover per altitude, so each layer gets its own band of the screen:
    //   0 low  (stratus / cumulus, under ~2 km): lowest band, big and grey; rain falls from these
    //   1 mid  (alto clouds, 2-6 km): middle band
    //   2 high (cirrus, above 6 km): thin stretched wisps right at the top
    //   3 fog  (cloud at the ground): anywhere on screen, density from the reported visibility
    struct Puff { public float Ox, Oy, Dx, Dy, Vx, Vy, R, A; public byte Kind; }

    sealed class Cloud
    {
        public float X, Y, Vx, W, Fade;
        public float Bottom;   // underside, relative to Y: rain falls from here
        public float Rainy;    // 0..1: a rain cloud is bigger and greyer
        public int Mon, Layer;
        public bool Leaving;
        public Puff[] P = [];
    }

    struct Ripple { public float X, Y, T, S; }

    readonly List<Cloud> _clouds = new();
    readonly List<Ripple> _ripples = new();
    bool _cloudsBusy, _cloudsReforming;
    public bool IsDay = true;

    static readonly float[] BandTop = { 0.20f, 0.08f, 0.01f, 0.00f };
    static readonly float[] BandBot = { 0.46f, 0.32f, 0.17f, 1.00f };
    static readonly float[] PerScreen = { 6, 7, 6, 11 };   // clouds at 100% cover, per 1920 px of width
    static readonly float[] DriftMul = { 1.0f, 1.35f, 1.9f, 0.45f }; // wind is stronger aloft

    float CloudAmount => Math.Clamp(Cur.Low + Cur.Mid + Cur.High + Cur.Fog, 0, 1.5f);
    float Rainy => MathF.Max(Cur.Rain, Cur.Storm);

    float LayerAmount(int layer) => layer switch
    {
        0 => MathF.Max(Cur.Low, Rainy > 0.05f ? 0.45f + Rainy * 0.45f : 0), // rain needs low clouds to fall from
        1 => Cur.Mid,
        2 => Cur.High,
        _ => Cur.Fog,
    };

    int CloudTarget(Monitor m, int layer) => (int)MathF.Round(LayerAmount(layer) * PerScreen[layer] * (layer == 3 ? 1 : CloudAmt) * m.Bounds.Width / 1920f);

    static float LayerBottom(Monitor m, int layer) => m.Bounds.Top + m.Bounds.Height * BandBot[layer];

    /// <summary>Lowest point of the sky clouds (fog excluded): the cursor only matters above it, unless there is fog.</summary>
    static float CloudBandBottom(Monitor m) => LayerBottom(m, 0);

    void SpawnCloud(int mi, int layer)
    {
        var m = Monitors[mi];
        float s = m.Scale, h = m.Bounds.Height;
        float w, ch;
        switch (layer)
        {
            case 0: { float big = 1.1f + Rainy * 0.6f + Cur.Low * 0.3f; w = (220 + Rf() * 300) * s * big; ch = w * (0.32f + Rf() * 0.08f); break; }
            case 1: w = (160 + Rf() * 240) * s; ch = w * (0.28f + Rf() * 0.08f); break;
            case 2: w = (200 + Rf() * 320) * s; ch = w * (0.09f + Rf() * 0.05f); break;
            default: w = (380 + Rf() * 420) * s; ch = w * (0.30f + Rf() * 0.10f); break;
        }
        float top = m.Bounds.Top + h * BandTop[layer], bot = LayerBottom(m, layer);
        var c = new Cloud
        {
            Mon = mi, Layer = layer, W = w, Fade = 0, Rainy = layer == 0 ? Rainy : 0,
            X = m.Bounds.Left + Rf() * m.Bounds.Width,
            Y = top + Rf() * MathF.Max(1, bot - top - ch * 0.5f) + ch * 0.4f,
        };
        Puff[] p;
        if (layer == 2)
        {
            // Cirrus: a row of thin streaks (drawn stretched sideways), no towering tops.
            int n = Math.Clamp((int)(w / (55 * s)), 3, 8);
            p = new Puff[n];
            for (int i = 0; i < n; i++)
                p[i] = new Puff
                {
                    Ox = -w / 2 + w * (i + 0.5f) / n + (Rf() - .5f) * 30 * s,
                    Oy = (Rf() - .5f) * ch * 0.8f + (i - n / 2f) * ch * 0.12f, // a slight diagonal sweep
                    R = ch * (0.9f + Rf() * 0.5f), A = 0.6f + Rf() * 0.4f, Kind = (byte)_rnd.Next(2),
                };
        }
        else
        {
            int nb = Math.Clamp((int)(w / (62 * s)), 3, 8), nt = layer == 3 ? 1 : Math.Clamp(nb / 2 + _rnd.Next(2), 1, 4);
            p = new Puff[nb + nt];
            for (int i = 0; i < nb; i++)
                p[i] = new Puff
                {
                    Ox = -w / 2 + w * (i + 0.5f) / nb + (Rf() - .5f) * 18 * s,
                    Oy = ch * 0.12f + (Rf() - .5f) * 10 * s,
                    R = ch * (0.55f + Rf() * 0.22f), A = 0.75f + Rf() * 0.25f, Kind = (byte)_rnd.Next(2),
                };
            for (int i = 0; i < nt; i++)
                p[nb + i] = new Puff
                {
                    Ox = (Rf() - .5f) * w * 0.55f,
                    Oy = -ch * (0.18f + Rf() * 0.2f),
                    R = ch * (0.6f + Rf() * 0.3f), A = 0.8f + Rf() * 0.2f, Kind = (byte)_rnd.Next(2),
                };
        }
        c.P = p;
        // Keep the whole resting cloud inside its layer's band.
        float low = 0; foreach (var q in p) low = MathF.Max(low, q.Oy + q.R * 0.7f);
        c.Y = MathF.Min(c.Y, bot - low);
        c.Bottom = low * 0.75f;
        _clouds.Add(c);
    }

    void StepClouds(float dt)
    {
        for (int mi = 0; mi < Monitors.Count; mi++)
        {
            var m = Monitors[mi];
            for (int layer = 0; layer < 4; layer++)
            {
                int have = 0;
                foreach (var c in _clouds) if (c.Mon == mi && c.Layer == layer && !c.Leaving) have++;
                int want = CloudTarget(m, layer);
                if (have < want && (have == 0 || Rf() < dt * 0.8f)) SpawnCloud(mi, layer);
                else if (have > want)
                    foreach (var c in _clouds) if (c.Mon == mi && c.Layer == layer && !c.Leaving) { c.Leaving = true; break; }
            }
        }
        _cloudsBusy = false; _cloudsReforming = false;
        for (int ci = _clouds.Count - 1; ci >= 0; ci--)
        {
            var c = _clouds[ci];
            if (c.Mon >= Monitors.Count) { _clouds.RemoveAt(ci); continue; }
            var m = Monitors[c.Mon];
            float s = m.Scale;
            // Drift with the wind (faster higher up); after a shove the cloud eases back to the drift speed.
            float drift = (WindX * 0.5f + (WindX >= 0 ? 6 : -6)) * s * DriftMul[c.Layer];
            c.Vx += (drift - c.Vx) * MathF.Min(1, dt * 0.25f);
            c.X += c.Vx * dt;
            if (c.X - c.W > m.Bounds.Right) c.X = m.Bounds.Left - c.W * 0.5f;
            else if (c.X + c.W < m.Bounds.Left) c.X = m.Bounds.Right + c.W * 0.5f;
            c.Fade = c.Leaving ? c.Fade - dt / 6f : MathF.Min(1, c.Fade + dt / 6f);
            if (c.Leaving && c.Fade <= 0) { _clouds.RemoveAt(ci); continue; }
            if (MathF.Abs(c.Vx - drift) > 3 * s) _cloudsReforming = true;
            var p = c.P;
            float drag = MathF.Exp(-dt * 1.5f);
            float bandBottom = LayerBottom(m, c.Layer);
            for (int i = 0; i < p.Length; i++)
            {
                ref var q = ref p[i];
                q.Dx += q.Vx * dt; q.Dy += q.Vy * dt;
                // A puff never wanders more than a few of its own sizes from its cloud.
                float dm = MathF.Sqrt(q.Dx * q.Dx + q.Dy * q.Dy), lim3 = q.R * 3;
                if (dm > lim3) { q.Dx *= lim3 / dm; q.Dy *= lim3 / dm; q.Vx *= 0.5f; q.Vy *= 0.5f; }
                q.Vx *= drag; q.Vy *= drag;
                float v2 = q.Vx * q.Vx + q.Vy * q.Vy;
                if (v2 < 400 * s * s)
                {
                    // Once the shove has died down the puff drifts home: the cloud re-forms over ~20 s.
                    float k = MathF.Min(1, dt / 5f);
                    q.Dx -= q.Dx * k; q.Dy -= q.Dy * k;
                }
                // Puffs stay in their layer's band: a downward shove stops at its bottom.
                float maxDy = bandBottom - c.Y - q.Oy;
                if (q.Dy > maxDy) { q.Dy = maxDy; if (q.Vy > 0) q.Vy = 0; }
                if (v2 > 16 * s * s) _cloudsBusy = true;
                else if (q.Dx * q.Dx + q.Dy * q.Dy > 4) _cloudsReforming = true;
            }
        }
    }

    /// <summary>The cursor moves through clouds: puffs it touches get shoved along, the cloud slides away.</summary>
    void PushClouds(float x0, float y0, float x1, float y1)
    {
        if (_clouds.Count == 0) return;
        float cvx = Math.Clamp(CursorVx, -3000, 3000), cvy = Math.Clamp(CursorVy, -3000, 3000);
        float sx = x1 - x0, sy = y1 - y0, len2 = MathF.Max(1, sx * sx + sy * sy);
        foreach (var c in _clouds)
        {
            var m = Monitors[Math.Min(c.Mon, Monitors.Count - 1)];
            float s = m.Scale;
            if (MathF.Max(x0, x1) < c.X - c.W || MathF.Min(x0, x1) > c.X + c.W) continue;
            float hit = 0;
            for (int i = 0; i < c.P.Length; i++)
            {
                ref var q = ref c.P[i];
                float px = c.X + q.Ox + q.Dx, py = c.Y + q.Oy + q.Dy;
                float t = Math.Clamp(((px - x0) * sx + (py - y0) * sy) / len2, 0, 1);
                float nx = x0 + sx * t, ny = y0 + sy * t;
                float dx = px - nx, dy = py - ny, d = MathF.Sqrt(dx * dx + dy * dy);
                float reach = q.R * (c.Layer == 2 ? 1.6f : 1.05f);
                if (d >= reach) continue;
                float f = 1 - d / reach;
                // Carried along with the cursor, plus a little sideways so the cloud parts around it.
                q.Vx += cvx * 0.22f * f + (d > 0.5f ? dx / d : 0) * 50 * s * f;
                q.Vy += cvy * 0.15f * f + (d > 0.5f ? dy / d : 0) * 35 * s * f;
                float sp = MathF.Sqrt(q.Vx * q.Vx + q.Vy * q.Vy), cap = 700 * s;
                if (sp > cap) { q.Vx *= cap / sp; q.Vy *= cap / sp; }
                hit += f;
            }
            if (hit > 0) { c.Vx += cvx * 0.06f * MathF.Min(1, hit); _pushes++; Kick(0.6); }
        }
    }

    /// <summary>A flung window pushes the clouds near its top edge out of the way.</summary>
    void PushCloudsByWindow(Tracked w, float vx, float vy)
    {
        foreach (var c in _clouds)
        {
            if (c.X + c.W < w.R.Left || c.X - c.W > w.R.Right) continue;
            for (int i = 0; i < c.P.Length; i++)
            {
                ref var q = ref c.P[i];
                float px = c.X + q.Ox + q.Dx, py = c.Y + q.Oy + q.Dy;
                if (px < w.R.Left - q.R || px > w.R.Right + q.R) continue;
                float d = MathF.Abs(py - w.R.Top);
                if (d > q.R + 60 * w.Scale) continue;
                float f = 1 - d / (q.R + 60 * w.Scale);
                q.Vx += vx * 0.45f * f; q.Vy += vy * 0.35f * f;
            }
        }
        Kick(0.6);
    }

    // ------------------------------------------------------------------ sky colour: day, sunset, dusk, night

    /// <summary>How "sunset" the light is right now (0 = plain day or night, 1 = sun on the horizon).</summary>
    public float SunsetAmount { get; private set; }

    static (float r, float g, float b) Mix3((float r, float g, float b) a, (float r, float g, float b) b, float t) =>
        (a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t);

    /// <summary>Colour of the lit tops and of the undersides of clouds, from the real sun elevation.</summary>
    ((float r, float g, float b) lit, (float r, float g, float b) under, float f) SkyColours()
    {
        var day = (0.95f, 0.96f, 0.975f);
        var gold = (1.00f, 0.80f, 0.58f);
        var pink = (0.97f, 0.63f, 0.68f);
        var dusk = (0.66f, 0.60f, 0.82f);
        var night = (0.70f, 0.74f, 0.84f);
        float e = SunElev;
        (float, float, float) lit; float f;
        if (e >= 14) { lit = day; f = 0; }
        else if (e >= 2) { float t = (14 - e) / 12f; lit = Mix3(day, gold, t * t); f = t * 0.85f; }
        else if (e >= -3) { float t = (2 - e) / 5f; lit = Mix3(gold, pink, t); f = 0.85f + 0.15f * t; }
        else if (e >= -9) { float t = (-3 - e) / 6f; lit = Mix3(pink, dusk, t); f = 1 - 0.6f * t; }
        else if (e >= -14) { float t = (-9 - e) / 5f; lit = Mix3(dusk, night, t); f = 0.4f * (1 - t); }
        else { lit = night; f = 0; }
        // Undersides catch the low sun: deeper orange at sunset, violet in the dusk.
        var under = e >= -3 ? Mix3(lit, (1.0f, 0.56f, 0.40f), f * 0.55f) : Mix3(lit, (0.58f, 0.46f, 0.72f), f * 0.5f);
        return (lit, under, f);
    }

    void AddCloudSprites(Monitor m)
    {
        var b = m.Bounds;
        float s = m.Scale;
        var (lit, under, f) = SkyColours();
        SunsetAmount = f;
        float side = SunX >= 0 ? 1 : -1;
        // Overcast: a faint sheet across the top that fades out by mid-screen.
        if (Cur.Haze > 0.3f)
            _sprites.Add(Atlas.Band, b.Left, b.Top, b.Right, LayerBottom(m, 0), Pm(lit.r, lit.g, lit.b, (Cur.Haze - 0.3f) * 0.08f));
        // Fog: the whole screen, thicker the shorter the real visibility.
        if (Cur.Fog > 0.01f)
            _sprites.Add(Atlas.Solid, b.Left, b.Top, b.Right, b.Bottom, Pm(lit.r * 0.97f, lit.g * 0.98f, lit.b, Cur.Fog * 0.15f));
        // Sunset: warm light washing in from the sun's side, only when there are clouds to catch it.
        if (f > 0.05f && _clouds.Count > 0)
        {
            float wx = side > 0 ? b.Right : b.Left, wy = b.Top + b.Height * 0.24f;
            float rw = b.Width * 0.75f, rh = b.Height * 0.48f;
            _sprites.Add(Atlas.Flare, wx - rw, wy - rh, wx + rw, wy + rh, Pm(1f, 0.62f, 0.38f, 0.17f * f));
        }
        int mi = Monitors.IndexOf(m);
        foreach (var c in _clouds)
        {
            if (c.Mon != mi) continue;
            float amount = MathF.Min(1, 0.45f + LayerAmount(c.Layer));
            float layerA = c.Layer switch { 0 => 1.12f, 1 => 1f, 2 => 0.62f, _ => 0.70f };
            float ca = (0.13f + 0.06f * Cur.Haze) * amount * layerA * c.Fade * (1 + 0.35f * f);
            if (ca < 0.003f) continue;
            // Rain clouds are a heavier grey.
            float k = c.Rainy * 0.6f;
            for (int i = 0; i < c.P.Length; i++)
            {
                ref var q = ref c.P[i];
                float px = c.X + q.Ox + q.Dx, py = c.Y + q.Oy + q.Dy;
                // A shoved puff thins out as it is pulled away from its cloud.
                float disp = MathF.Sqrt(q.Dx * q.Dx + q.Dy * q.Dy);
                float thin = 1 - MathF.Min(0.7f, disp / (q.R * 2.2f));
                float a = ca * q.A * thin * (1 + k * 0.5f);
                // The puff body stays close to neutral so its outline melts into whatever is behind it
                // (no obvious orange blob). The sunset colour is added as light inside the cloud below.
                var hue = q.Oy > 0 || c.Layer == 2 ? under : lit;
                var neutral = SunElev > -6 ? (0.93f, 0.94f, 0.96f) : (0.70f, 0.73f, 0.80f);
                var col = Mix3(neutral, hue, 0.30f);
                float u = Math.Clamp((px - c.X) / (c.W * 0.5f), -1, 1);
                float bright = 1 + 0.22f * f * u * side;
                float r = MathF.Min(1, (col.r - k * 0.28f) * bright), g = MathF.Min(1, (col.g - k * 0.27f) * bright), bl = MathF.Min(1, (col.b - k * 0.24f) * bright);
                float rw = q.R * (c.Layer == 2 ? 2.6f : 1.25f), rh = q.R * (c.Layer == 2 ? 0.45f : 1f);
                _sprites.Add(q.Kind == 0 ? Atlas.Puff : Atlas.Puff2, px - rw, py - rh, px + rw, py + rh, Pm(r, g, bl, a));
                if (f > 0.04f)
                {
                    // Sunset light glowing in the cloud's core: additive, so it fades to nothing before the edge.
                    float lw = rw * 0.72f, lh = rh * 0.72f;
                    float glow = f * 0.42f * q.A * thin * c.Fade * MathF.Min(1, amount) * (c.Layer == 2 ? 1.2f : 1f);
                    _sprites.Add(Atlas.PuffLight, px - lw, py - lh, px + lw, py + lh, Pm(hue.r, hue.g * 0.92f, hue.b * 0.85f, glow * bright));
                }
            }
        }
    }

    /// <summary>Where a new raindrop starts: under a low (or else mid) cloud when there are any (rain comes from them).</summary>
    bool RainSource(int mi, out float x, out float y)
    {
        x = y = 0;
        for (int layer = 0; layer <= 1; layer++)
        {
            float tot = 0;
            foreach (var c in _clouds) if (c.Mon == mi && c.Layer == layer && !c.Leaving && c.Fade > 0.4f) tot += c.W;
            if (tot <= 0) continue;
            float r = Rf() * tot;
            foreach (var c in _clouds)
            {
                if (c.Mon != mi || c.Layer != layer || c.Leaving || c.Fade <= 0.4f) continue;
                r -= c.W;
                if (r > 0) continue;
                x = c.X + (Rf() - 0.5f) * c.W * 0.8f;
                y = c.Y + c.Bottom;
                return true;
            }
        }
        return false;
    }

    string CloudPushDebug()
    {
        float maxD = 0, maxV = 0;
        foreach (var c in _clouds) foreach (var q in c.P) { maxD = MathF.Max(maxD, MathF.Sqrt(q.Dx * q.Dx + q.Dy * q.Dy)); maxV = MathF.Max(maxV, MathF.Sqrt(q.Vx * q.Vx + q.Vy * q.Vy)); }
        return $"cloudMaxD={maxD:0} cloudMaxV={maxV:0} pushes={_pushes} cloudsAt=[{string.Join(" ", _clouds.Select(c => $"{c.X:0},{c.Y:0}"))}]";
    }
    int _pushes;

    // ------------------------------------------------------------------ puddles

    void DepositWater(Tracked w, int c, float v)
    {
        var wt = w.Wt;
        wt[c] += v * 0.5f;
        if (c > 0) wt[c - 1] += v * 0.25f; else wt[c] += v * 0.25f;
        if (c + 1 < wt.Length) wt[c + 1] += v * 0.25f; else wt[c] += v * 0.25f;
        w.WaterTotal += v;
    }

    void AddRipple(float x, float y, float s)
    {
        if (_ripples.Count < 80) _ripples.Add(new Ripple { X = x, Y = y, T = 0, S = s });
    }

    void StepWater(float dt)
    {
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var r = _ripples[i]; r.T += dt; _ripples[i] = r;
            if (r.T > 0.55f) _ripples.RemoveAt(i);
        }
        bool drying = MathF.Max(Cur.Rain, Cur.Storm) < 0.05f;
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.Wt.Length == 0 || w.WaterTotal < 0.05f) continue;
            var h = w.Wt; int n = h.Length;
            float s = w.Scale;
            // Water levels out: almost no slope allowed, so it spreads into a flat puddle.
            float slope = 0.06f * s, kk = MathF.Min(1, dt * 30);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n - 1; i++)
                {
                    float d = h[i] - h[i + 1];
                    if (d > slope) { float mv = (d - slope) * 0.5f * kk; h[i] -= mv; h[i + 1] += mv; }
                    else if (d < -slope) { float mv = (-d - slope) * 0.5f * kk; h[i] += mv; h[i + 1] -= mv; }
                }
            float max = (w.IsTaskbar ? 5f : 3.5f) * s;
            for (int i = 0; i < n; i++) if (h[i] > max) h[i] = max;
            // Window tops drip off at both ends (the taskbar runs into the screen edges instead).
            if (!w.IsTaskbar)
                for (int e = 0; e < 2; e++)
                {
                    int i = e == 0 ? 0 : n - 1;
                    if (h[i] > 0.6f * s)
                    {
                        h[i] -= MathF.Min(h[i], 1.2f * s * dt * 4);
                        if (_rnd.Next(6) == 0)
                            AddDebris(e == 0 ? w.R.Left + 1 : w.R.Right - 1, w.R.Top, (e == 0 ? -12 : 12) * s, 20, (2.4f + Rf()) * s, true);
                    }
                }
            if (drying)
            {
                float ev = dt * 0.02f * s;
                for (int i = 0; i < n; i++) h[i] = MathF.Max(0, h[i] - ev);
            }
            float tot = 0;
            for (int i = 0; i < n; i++) tot += h[i];
            w.WaterTotal = tot;
        }
    }

    void SpillWater(Tracked w, float vx, float vy)
    {
        if (w.Wt.Length == 0 || w.WaterTotal < 0.3f) { if (w.Wt.Length > 0) Array.Clear(w.Wt); w.WaterTotal = 0; return; }
        int budget = 500;
        for (int i = 0; i < w.Wt.Length && budget > 0; i += 2)
        {
            float h = w.Wt[i];
            if (h < 0.5f || _rnd.Next(2) == 0) continue;
            budget--;
            AddDebris(w.R.Left + (i + Rf()) * w.ColW, w.R.Top - h, vx * (0.7f + 0.5f * Rf()) + (Rf() - .5f) * 50 * w.Scale,
                vy * 0.5f - Rf() * 70 * w.Scale, (2.2f + Rf() * 2f) * w.Scale, true);
        }
        Array.Clear(w.Wt);
        w.WaterTotal = 0;
    }

    /// <summary>The cursor sweeps through a puddle: it splashes.</summary>
    void BrushWater(float x0, float y0, float x1, float y1)
    {
        foreach (var w in Tracker.Z)
        {
            if (w.Spans.Count == 0 || w.WaterTotal < 0.3f) continue;
            float r = 6 * w.Scale, top = w.R.Top;
            if (MathF.Max(y0, y1) < top - 6 * w.Scale - r || MathF.Min(y0, y1) > top + r) continue;
            float lo = MathF.Min(x0, x1) - r, hi = MathF.Max(x0, x1) + r;
            if (hi < w.R.Left || lo > w.R.Right) continue;
            int c0 = Math.Max(0, (int)((lo - w.R.Left) / w.ColW)), c1 = Math.Min(w.Wt.Length - 1, (int)((hi - w.R.Left) / w.ColW));
            float cvx = Math.Clamp(CursorVx, -2000, 2000);
            bool hit = false;
            for (int c = c0; c <= c1; c++)
            {
                if (w.Wt[c] < 0.3f || !InSpans(w, c)) continue;
                float cx = w.R.Left + (c + 0.5f) * w.ColW;
                if (_rnd.Next(3) == 0)
                    AddDebris(cx, top - w.Wt[c], cvx * 0.4f + (Rf() - .5f) * 60, -(80 + Rf() * 140) * w.Scale, (1.8f + Rf() * 1.5f) * w.Scale, true);
                w.WaterTotal -= w.Wt[c] * 0.8f;
                w.Wt[c] *= 0.2f;
                hit = true;
            }
            if (hit) { AddRipple(MathF.Min(MathF.Max(x1, w.R.Left), w.R.Right), top, w.Scale); Kick(0.4); }
        }
    }

    void AddWaterSprites(Monitor m)
    {
        var b = m.Bounds;
        float s = m.Scale;
        foreach (var w in Tracker.Z)
        {
            if (!w.Eligible || w.Spans.Count == 0 || w.WaterTotal < 0.3f) continue;
            if (w.R.Right < b.Left || w.R.Left > b.Right || w.R.Top < b.Top || w.R.Top > b.Bottom + 6) continue;
            float y = w.R.Top + 0.5f;
            var h = w.Wt;
            float shine = MathF.Max(1f, 0.9f * s);
            foreach (var (ca, cb) in w.Spans)
                for (int c = ca; c < cb; c++)
                {
                    float hh = h[c];
                    if (hh < 0.35f) continue;
                    float x = w.R.Left + c * w.ColW;
                    _sprites.Add(Atlas.Solid, x, y - hh, x + w.ColW + 0.3f, y, Pm(0.52f, 0.66f, 0.86f, 0.40f));
                    _sprites.Add(Atlas.Solid, x, y - hh, x + w.ColW + 0.3f, y - hh + shine, Pm(0.90f, 0.95f, 1f, 0.70f));
                }
        }
        foreach (var r in _ripples)
        {
            if (r.X < b.Left || r.X > b.Right || r.Y < b.Top || r.Y > b.Bottom + 6) continue;
            float p = r.T / 0.55f;
            float rw = (3 + 13 * p) * r.S, rh = rw * 0.28f;
            float a = (1 - p) * 0.55f;
            _sprites.Add(Atlas.Ring, r.X - rw, r.Y - 2 * r.S - rh, r.X + rw, r.Y - 2 * r.S + rh, Pm(0.9f, 0.95f, 1f, a));
        }
    }
}
