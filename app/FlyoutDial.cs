using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace WindowsWeather;

// The corner dial (from the original sketch): a quarter circle sitting on the taskbar against the right edge of the
// screen. Along its rim, the time of day: a track coloured by the real sky (night, dawn, day, dusk from the real
// sunrise and sunset) with a sun / moon knob at the current time; drag it to preview the sky at any hour. Inside: a
// big weather circle (tap for the effect picker), a vertical amount slider for what is falling now, the location bar
// (tap to type a city) and a settings gear for the full panel.
public sealed unsafe partial class Flyout
{
    enum View { Dial, Effects, Settings }
    View _view = View.Dial;

    const float DialSize = 300;              // window: a square whose bottom-right corner is the dial's centre
    const float DialR = 292;                 // the quarter disc
    const float ArcR = 262, ArcW = 9;        // the time-of-day track
    const float ArcA0 = 183, ArcA1 = 267;    // degrees, screen coordinates (180 = left of the centre, 270 = up)
    static Vector2 DialC => new(DialSize, DialSize);

    (float r, float g, float b)[]? _arcSky;  // sky colour along the day, computed once per open
    ID2D1StrokeStyle? _round;

    static Vector2 Polar(float r, float deg)
    {
        float a = deg * MathF.PI / 180;
        return DialC + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
    }

    static float TimeToAngle(float frac) => ArcA0 + (ArcA1 - ArcA0) * Math.Clamp(frac, 0, 1);

    /// <summary>The colour of the sky at each point of the day, from the real sun elevation here.</summary>
    void BuildArcSky()
    {
        var st = App.CurrentState;
        const int n = 96;
        _arcSky = new (float, float, float)[n + 1];
        var day0 = DateTime.Today;
        for (int i = 0; i <= n; i++)
        {
            var t = day0.AddMinutes(1440.0 * i / n).ToUniversalTime();
            double el = st.HasLoc ? WeatherService.Sun(st.Lat, st.Lon, t).elev : (i > n / 4 && i < n * 3 / 4 ? 30 : -30);
            _arcSky[i] = SkyAt((float)el);
        }
    }

    static (float, float, float) SkyAt(float el)
    {
        (float, float, float) night = (0.22f, 0.25f, 0.52f), dawn = (0.98f, 0.55f, 0.34f), gold = (1f, 0.78f, 0.42f), day = (0.42f, 0.70f, 1f);
        static (float, float, float) L((float, float, float) a, (float, float, float) b, float t)
        { t = Math.Clamp(t, 0, 1); return (a.Item1 + (b.Item1 - a.Item1) * t, a.Item2 + (b.Item2 - a.Item2) * t, a.Item3 + (b.Item3 - a.Item3) * t); }
        if (el < -8) return night;
        if (el < -1) return L(night, dawn, (el + 8) / 7);
        if (el < 6) return L(dawn, gold, (el + 1) / 7);
        return L(gold, day, (el - 6) / 10);
    }

    float NowFrac()
    {
        var t = App.PreviewTime ?? DateTime.Now;
        return (float)(t.TimeOfDay.TotalMinutes / 1440.0);
    }

    // ------------------------------------------------------------------ drawing

    void DrawDial(float slide)
    {
        var ctx = _gpu.Ctx;
        var c = DialC;
        _round ??= _gpu.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        if (_arcSky == null) BuildArcSky();
        _hit.Clear();

        // The quarter disc: a tint over the backdrop, with a hairline rim.
        _brush.Color = _light ? C(0.965f, 0.965f, 0.97f, 0.97f) : C(0.118f, 0.118f, 0.13f, 0.96f);
        ctx.FillEllipse(new Ellipse(c, DialR, DialR), _brush);
        _brush.Color = Fg(_light ? 0.12f : 0.10f);
        ctx.DrawEllipse(new Ellipse(c, DialR - 0.5f, DialR - 0.5f), _brush, 1);

        ctx.Transform = Matrix3x2.CreateTranslation(slide, slide) * Matrix3x2.CreateScale(_s);

        // Time of day: the track is drawn in short segments, each the colour of the sky at that hour. It grows in on open.
        float grow = Ease((float)((_clock.Elapsed.TotalSeconds - _openT) / 0.45));
        int n = _arcSky!.Length - 1;
        int shown = (int)(n * grow);
        for (int i = 0; i < shown; i++)
        {
            var (r, g, b) = _arcSky[i];
            _brush.Color = C(r, g, b, 0.9f);
            ctx.DrawLine(Polar(ArcR, TimeToAngle(i / (float)n)), Polar(ArcR, TimeToAngle((i + 1) / (float)n)), _brush, ArcW, _round);
        }
        // Hour ticks every 3 hours, outside the track; labels for 6 am, noon, 6 pm.
        for (int hr = 0; hr <= 24; hr += 3)
        {
            float a = TimeToAngle(hr / 24f);
            _brush.Color = Fg(hr % 6 == 0 ? 0.55f : 0.3f);
            ctx.DrawLine(Polar(ArcR + 9, a), Polar(ArcR + (hr % 6 == 0 ? 17 : 14), a), _brush, 1.3f, _round);
        }
        foreach (var (hr, label) in new[] { (6, "6a"), (12, "12p"), (18, "6p") })
        {
            var p = Polar(ArcR - 23, TimeToAngle(hr / 24f));
            Text(label, _smallCenter, new Rect(p.X - 16, p.Y - 8, 32, 16), Fg(0.5f));
        }
        // The knob: the sun by day, the moon by night, at the current (or previewed) time.
        float nf = NowFrac();
        var kp = Polar(ArcR, TimeToAngle(nf));
        var st = App.CurrentState;
        var when = (App.PreviewTime ?? DateTime.Now).ToUniversalTime();
        double sunEl = st.HasLoc ? WeatherService.Sun(st.Lat, st.Lon, when).elev : 30;
        bool day = sunEl > -2;
        float kh = MathF.Max(H(Id.Arc), _drag == Id.Arc ? 1 : 0);
        float kr = 11 + 2 * kh;
        _brush.Color = day ? C(1f, 0.86f, 0.42f, 0.25f) : C(0.75f, 0.8f, 1f, 0.2f);
        ctx.FillEllipse(new Ellipse(kp, kr + 6, kr + 6), _brush);
        _brush.Color = day ? C(1f, 0.84f, 0.38f, 1) : C(0.86f, 0.89f, 0.98f, 1);
        ctx.FillEllipse(new Ellipse(kp, kr, kr), _brush);
        if (!day)
        {
            // A crescent: bite out of the moon with the panel colour.
            _brush.Color = _light ? C(0.976f, 0.976f, 0.976f, 1) : C(0.12f, 0.12f, 0.13f, 1);
            ctx.FillEllipse(new Ellipse(kp + new Vector2(kr * 0.45f, -kr * 0.3f), kr * 0.82f, kr * 0.82f), _brush);
        }
        _brush.Color = Fg(0.9f);
        ctx.DrawEllipse(new Ellipse(kp, kr, kr), _brush, 1.5f);

        // The time, and a way back to now while previewing.
        var tl = App.PreviewTime ?? DateTime.Now;
        Text(tl.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), _body, new Rect(c.X - 238, c.Y - 42, 90, 20), Fg(0.9f));
        if (App.PreviewTime != null)
        {
            Text("Preview", _small, new Rect(c.X - 238, c.Y - 23, 60, 16), Acc());
            var nowR = new Rect(c.X - 238 + 52, c.Y - 24, 44, 18);
            _hit[Id.Now] = nowR;
            Fill(nowR, 9, Acc(0.9f));
            Text("Now", _smallCenter, nowR, C(_accentText.R, _accentText.G, _accentText.B, 1));
        }
        else Text("Live", _small, new Rect(c.X - 238, c.Y - 23, 60, 16), Fg(0.45f));

        // Settings gear, tucked under the top end of the arc.
        var gear = new Rect(c.X - 58, c.Y - 238, 34, 34);
        _hit[Id.Gear] = gear;
        if (H(Id.Gear) > 0.01f) Fill(gear, 16, Fg(0.08f * H(Id.Gear)));
        Text("", _iconCenter, gear, Fg(0.85f));

        // Weather: the big circle. Condition glyph and temperature; tap for the effect picker.
        var wc = new Vector2(c.X - 92, c.Y - 112);
        float wr = 46;
        _hit[Id.WeatherBtn] = new Rect(wc.X - wr, wc.Y - wr, wr * 2, wr * 2);
        float wh = H(Id.WeatherBtn);
        _brush.Color = Fg((_light ? 0.05f : 0.07f) + wh * 0.05f);
        ctx.FillEllipse(new Ellipse(wc, wr, wr), _brush);
        _brush.Color = Fg(0.12f + wh * 0.1f);
        ctx.DrawEllipse(new Ellipse(wc, wr, wr), _brush, 1.2f);
        Text(App.ConditionEmoji(), _emojiBig, new Rect(wc.X - wr, wc.Y - wr + 6, wr * 2, 44), Fg(1));
        Text(App.TempText(), _smallCenter, new Rect(wc.X - wr, wc.Y + 12, wr * 2, 18), Fg(0.9f));
        string cond = App.CurrentState.Weather is { } w0 ? WeatherService.Describe(w0.Code) : "";
        if (App.CurrentSettings.Effect != "Live") cond = App.CurrentSettings.Effect;
        Text(cond, _smallCenter, new Rect(wc.X - 55, wc.Y - wr - 20, 110, 16), Fg(0.6f));

        // Amount: a vertical slider for what is falling right now.
        var am = AmountRect();
        _hit[Id.Amount] = new Rect(am.Left - 8, am.Top - 6, am.Width + 16, am.Height + 12);
        string what = App.AmountTargetName();
        float v = App.AmountOf(what) / 2f;
        float ah = MathF.Max(H(Id.Amount), _drag == Id.Amount ? 1 : 0);
        Fill(am, am.Width / 2, Fg(_light ? 0.10f : 0.13f));
        float fillH = am.Height * v;
        Fill(new Rect(am.Left, am.Bottom - fillH, am.Width, fillH), am.Width / 2, Acc(0.85f + 0.15f * ah));
        Text(what, _smallCenter, new Rect(am.Left - 24, am.Bottom + 4, am.Width + 48, 14), Fg(0.6f));
        Text($"{MathF.Round(v * 200)}%", _smallCenter, new Rect(am.Left - 24, am.Bottom + 18, am.Width + 48, 14), Fg(ah > 0.1f ? 0.9f : 0.5f));

        // Location: the bar under the weather circle; tap to type a city.
        var loc = new Rect(wc.X - 68, c.Y - 52, 136, 24);
        if (_editing) DrawEditField(new Rect(c.X - 238 + 96, c.Y - 54, 238 - 96 - 10, 28));
        else
        {
            _hit[Id.Location] = loc;
            float lh = H(Id.Location);
            Fill(loc, 12, Fg((_light ? 0.06f : 0.08f) + lh * 0.05f));
            var st2 = App.CurrentState; var s2 = App.CurrentSettings;
            string place = s2.City.Length > 0 && st2.CityNotFound ? $"{s2.City}?" : st2.Place.Length > 0 ? st2.Place : "Set location";
            Text("", _icon, new Rect(loc.Left + 8, loc.Top, 16, loc.Height), Fg(0.7f));
            Text(place, _small, new Rect(loc.Left + 26, loc.Top, loc.Width - 32, loc.Height), Fg(0.85f));
        }
    }

    static Rect AmountRect() => new(DialSize - 184, DialSize - 150, 14, 70);

    // ------------------------------------------------------------------ the effect picker

    float EffectsLayout(bool draw)
    {
        float y = Pad, iw = W - Pad * 2;
        if (draw)
        {
            Text("Effect", _title, new Rect(Pad + 34, y - 2, iw - 34, 26), Fg(0.95f));
            BackButton(new Rect(Pad - 6, y - 3, 30, 30));
        }
        y += 34;
        const int cols = 7;
        float gap = 6, cw = (iw - gap * (cols - 1)) / cols, ch = 54;
        string[] glyphs = { "🌤️", "❄️", "🌧️", "☁️", "🌫️", "☀️", "🌇", "🍂", "🌙", "⛈️", "🧊", "🌪️", "🥶", "🌌", "" };
        for (int i = 0; i < App.EffectNames.Length; i++)
        {
            int col = i % cols, row = i / cols;
            var r = new Rect(Pad + col * (cw + gap), y + row * (ch + gap), cw, ch);
            if (!draw) continue;
            var id = Id.Chip0 + i;
            _hit[id] = r;
            float sel = A(id), hov = H(id);
            Fill(r, 6, Fg((_light ? 0.035f : 0.045f) + hov * 0.05f));
            if (sel > 0.01f) Fill(r, 6, Acc(sel));
            var tc = sel > 0.5f ? C(_accentText.R, _accentText.G, _accentText.B, _accentText.A) : Fg(0.88f);
            if (glyphs[i].Length > 0) Text(glyphs[i], _emoji, new Rect(r.Left, r.Top + 4, r.Width, 26), Fg(1));
            else Text("", _iconCenter, new Rect(r.Left, r.Top + 4, r.Width, 26), tc);
            Text(App.EffectNames[i], _smallCenter, new Rect(r.Left, r.Top + 31, r.Width, 18), tc);
        }
        int rows = (App.EffectNames.Length + cols - 1) / cols;
        y += ch * rows + gap * (rows - 1) + Pad;
        return y;
    }

    void BackButton(Rect r)
    {
        _hit[Id.Back] = r;
        if (H(Id.Back) > 0.01f) Fill(r, 6, Fg(0.07f * H(Id.Back)));
        Text("", _iconCenter, r, Fg(0.85f));
    }

    // ------------------------------------------------------------------ input

    /// <summary>The arc is a ring, not a rectangle: test it by distance and angle.</summary>
    Id HitDial(float x, float y)
    {
        var d = new Vector2(x, y) - DialC;
        float r = d.Length();
        if (r > DialR) return Id.Outside;
        float deg = MathF.Atan2(d.Y, d.X) * 180 / MathF.PI; if (deg < 0) deg += 360;
        if (MathF.Abs(r - ArcR) < 18 && deg >= ArcA0 - 4 && deg <= ArcA1 + 4) return Id.Arc;
        return Id.None;
    }

    void ArcTo(int px, int py)
    {
        var d = new Vector2(px / _s, py / _s) - DialC;
        float deg = MathF.Atan2(d.Y, d.X) * 180 / MathF.PI; if (deg < 0) deg += 360;
        float frac = Math.Clamp((deg - ArcA0) / (ArcA1 - ArcA0), 0, 1);
        int minutes = (int)MathF.Round(frac * 1440 / 5) * 5; // 5-minute steps
        App.SetPreview(DateTime.Today.AddMinutes(Math.Min(minutes, 1439)));
        Render();
    }

    void AmountTo(int py)
    {
        var am = AmountRect();
        float v = Math.Clamp((am.Bottom - py / _s) / am.Height, 0, 1) * 2f;
        v = MathF.Round(v * 20) / 20f;
        App.FlyoutCommand("amount", $"{App.AmountTargetName()}={v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Render();
    }

    /// <summary>Testing: open straight into a view (0 dial, 1 effects, 2 settings).</summary>
    public void ShowView(int v)
    {
        if (!_visible) Open();
        SetView((View)Math.Clamp(v, 0, 2));
    }

    void SetView(View v)
    {
        if (_view == v) return;
        _view = v;
        _editing = false;
        _hover.Clear(); _hot = Id.None; _pressed = Id.None;
        PlaceWindow();
        _openT = _clock.Elapsed.TotalSeconds - 0.08; // a quick settle-in rather than the full open animation
        SyncAnimTargets(snap: true);
        Kick();
    }
}
