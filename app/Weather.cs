using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsWeather;

public sealed class WeatherNow
{
    public int Code { get; set; }
    public double TempF { get; set; }
    public double Cloud { get; set; }
    public double Precip { get; set; }
    public bool IsDay { get; set; } = true;
    public double WindKmh { get; set; }
    public double WindDir { get; set; }
    public double WindGust { get; set; }
    public double CloudLow { get; set; } = -1;  // % cover by altitude (-1 = unknown, from an older cache)
    public double CloudMid { get; set; } = -1;
    public double CloudHigh { get; set; } = -1;
    public double Visibility { get; set; } = -1; // metres
    public double Humidity { get; set; } = -1;   // %
    public double SnowfallCm { get; set; }       // last hour
    public double SnowDepthM { get; set; }
    public double Aqi { get; set; } = -1;        // US AQI (Open-Meteo air quality)
    public double Kp { get; set; } = -1;         // planetary K index (NOAA SWPC)
    public DateTime At { get; set; }
}

public sealed class CachedState
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public bool HasLoc { get; set; }
    public string Place { get; set; } = "";
    public string Country { get; set; } = "";
    public string LocSource { get; set; } = "";
    public DateTime LocAt { get; set; }
    public string LocCity { get; set; } = ""; // which city override produced this location
    public bool CityNotFound { get; set; }    // the typed city could not be found (shown in the menu)
    public WeatherNow? Weather { get; set; }
}

public sealed class Settings
{
    public string Effect { get; set; } = "Live";      // Live | Snow | Rain | Clouds | Sun | Night | Storm | Off
    public string Intensity { get; set; } = "Medium"; // older setting; the amounts below replace it
    public float SnowAmount { get; set; } = 1;    // 0..2 multipliers set with the flyout sliders
    public float RainAmount { get; set; } = 1;
    public float LeafAmount { get; set; } = 1;
    public float CloudAmount { get; set; } = 1;
    public int Fps { get; set; } = 30;                // 30 | 60
    public bool StartWithWindows { get; set; } = true;
    public string City { get; set; } = "";            // overrides automatic location when set
    public bool HideFromCapture { get; set; }
    public bool Paused { get; set; }
    public bool RestOnBatterySaver { get; set; } = true;
    public bool LighterOnBattery { get; set; } = true;  // 20 fps when unplugged
    public bool Online { get; set; } = true;            // off: no network at all (no location, no weather lookups)
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(CachedState))]
internal partial class JsonCtx : JsonSerializerContext { }

public static class Store
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsWeather");
    static string SettingsPath => Path.Combine(Dir, "settings.json");
    public static bool HasSettings => File.Exists(SettingsPath);
    static string StatePath => Path.Combine(Dir, "state.json");

    public static Settings LoadSettings()
    {
        try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), JsonCtx.Default.Settings) ?? new(); }
        catch (Exception e) { Log.Write("settings unreadable, using defaults: " + e.Message); }
        return new Settings();
    }

    public static void Save(Settings s)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, JsonCtx.Default.Settings)); }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
    }

    public static CachedState LoadState()
    {
        try { if (File.Exists(StatePath)) return JsonSerializer.Deserialize(File.ReadAllText(StatePath), JsonCtx.Default.CachedState) ?? new(); }
        catch { }
        return new CachedState();
    }

    public static void Save(CachedState s)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(StatePath, JsonSerializer.Serialize(s, JsonCtx.Default.CachedState)); }
        catch (Exception e) { Log.Write("state save failed: " + e.Message); }
    }
}

public static class WeatherService
{
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsWeather/1.0");
        return c;
    }

    /// <summary>Locate, fetch the current weather, save the cache, then call <paramref name="done"/> (from a pool thread).</summary>
    public static async Task Refresh(Settings set, CachedState st, Action done)
    {
        try
        {
            if (await Locate(set, st))
            {
                var w = await Fetch(st.Lat, st.Lon);
                if (w != null)
                {
                    var aq = FetchAqi(st.Lat, st.Lon); var kp = FetchKp();
                    w.Aqi = await aq; w.Kp = await kp;
                    st.Weather = w;
                    Log.Write($"weather: {Describe(w.Code)} (code {w.Code}), {w.TempF:0}F, cloud {w.Cloud}%, precip {w.Precip}mm, wind {w.WindKmh:0}km/h from {w.WindDir:0}, day={w.IsDay}");
                }
                Store.Save(st);
            }
        }
        catch (Exception e) { Log.Write("weather fetch: " + e.Message); }
        finally { done(); }
    }

    static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    static async Task<JsonDocument?> GetJson(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Write($"GET {Short(url)} -> {(int)resp.StatusCode}"); return null; }
            var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Write($"GET {Short(url)} failed: {e.GetType().Name} {e.Message}"); return null; }
    }

    static string Short(string url) { int q = url.IndexOf('?'); return q > 0 ? url[..q] : url; }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static double Num(JsonElement e, string name, double def = 0) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

    /// <summary>Work out where the laptop is: typed city, then Windows location, then IP. Updates <paramref name="st"/>.</summary>
    public static async Task<bool> Locate(Settings set, CachedState st)
    {
        var city = set.City.Trim();
        if (city.Length > 0)
        {
            if (st.HasLoc && st.LocSource == "city" && st.LocCity == city) return true;
            // The geocoder wants just the town ("Springfield"), so split off a trailing state or country
            // ("springfield IL", "Denver, CO", "Paris FR") and use it to pick the right match.
            var (name, hint) = SplitCity(city);
            using var doc = await GetJson($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=10&language=en&format=json");
            JsonElement? pick = null;
            if (doc != null && doc.RootElement.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
            {
                foreach (var cand in res.EnumerateArray())
                    if (hint.Length == 0 || MatchesHint(cand, hint)) { pick = cand; break; }
                pick ??= res[0];
            }
            st.CityNotFound = pick == null;
            if (pick is JsonElement r)
            {
                st.Lat = Num(r, "latitude"); st.Lon = Num(r, "longitude");
                st.Country = Str(r, "country_code");
                string admin1 = Str(r, "admin1");
                if (st.Country == "US") admin1 = UsStates.FirstOrDefault(kv => kv.Value.Equals(admin1, StringComparison.OrdinalIgnoreCase)).Key ?? admin1;
                st.Place = PlaceName(Str(r, "name"), admin1, st.Country);
                st.LocSource = "city"; st.LocCity = city; st.LocAt = DateTime.UtcNow; st.HasLoc = true;
                Log.Write($"location from typed city '{city}': {st.Place}");
                return true;
            }
            Log.Write($"could not find typed city '{city}', falling back to automatic location");
        }

        bool fresh = st.HasLoc && st.LocSource is "windows" or "ip" && DateTime.UtcNow - st.LocAt < TimeSpan.FromHours(st.LocSource == "windows" ? 6 : 24);
        if (fresh) return true;

        var pos = await Geo.TryWindowsLocation();
        if (pos != null)
        {
            st.Lat = pos.Value.lat; st.Lon = pos.Value.lon; st.HasLoc = true; st.LocSource = "windows"; st.LocAt = DateTime.UtcNow; st.LocCity = "";
            st.Place = ""; // never keep the old place name for new coordinates
            using var rg = await GetJson($"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={F(st.Lat)}&longitude={F(st.Lon)}&localityLanguage=en");
            if (rg != null)
            {
                var r = rg.RootElement;
                string name = Str(r, "city"); if (name.Length == 0) name = Str(r, "locality");
                string sub = Str(r, "principalSubdivisionCode"); int dash = sub.IndexOf('-'); if (dash >= 0) sub = sub[(dash + 1)..];
                st.Country = Str(r, "countryCode");
                st.Place = PlaceName(name, sub, st.Country);
            }
            if (st.Place.Length == 0)
            {
                // Naming the place failed (often just after waking, before the network is back): show the
                // coordinates and look again at the next refresh instead of in 6 hours.
                st.Place = $"{st.Lat:0.00}, {st.Lon:0.00}";
                st.LocAt = DateTime.UtcNow - TimeSpan.FromHours(5.8);
            }
            Log.Write($"location from Windows: {st.Place} ({st.Lat:0.000},{st.Lon:0.000})");
            return true;
        }

        if (st.HasLoc && st.LocSource == "ip" && DateTime.UtcNow - st.LocAt < TimeSpan.FromDays(1)) return true;
        using (var ip = await GetJson("https://ipwho.is/"))
        {
            if (ip != null && ip.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var r = ip.RootElement;
                st.Lat = Num(r, "latitude"); st.Lon = Num(r, "longitude"); st.Country = Str(r, "country_code");
                st.Place = PlaceName(Str(r, "city"), Str(r, "region_code"), st.Country);
                st.HasLoc = true; st.LocSource = "ip"; st.LocAt = DateTime.UtcNow; st.LocCity = "";
                Log.Write($"location from IP: {st.Place}");
                return true;
            }
        }
        using (var ip2 = await GetJson("https://ipapi.co/json/"))
        {
            if (ip2 != null && ip2.RootElement.TryGetProperty("latitude", out _))
            {
                var r = ip2.RootElement;
                st.Lat = Num(r, "latitude"); st.Lon = Num(r, "longitude"); st.Country = Str(r, "country_code");
                st.Place = PlaceName(Str(r, "city"), Str(r, "region_code"), st.Country);
                st.HasLoc = true; st.LocSource = "ip"; st.LocAt = DateTime.UtcNow; st.LocCity = "";
                Log.Write($"location from IP (fallback): {st.Place}");
                return true;
            }
        }
        return st.HasLoc; // keep whatever we had
    }

    static readonly Dictionary<string, string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California", ["CO"] = "Colorado",
        ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia", ["FL"] = "Florida", ["GA"] = "Georgia",
        ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas",
        ["KY"] = "Kentucky", ["LA"] = "Louisiana", ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts",
        ["MI"] = "Michigan", ["MN"] = "Minnesota", ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana",
        ["NE"] = "Nebraska", ["NV"] = "Nevada", ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico",
        ["NY"] = "New York", ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma",
        ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota",
        ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia", ["WA"] = "Washington",
        ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming",
    };

    static (string name, string hint) SplitCity(string city)
    {
        var parts = city.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string last = parts[^1];
            if (last.Length == 2 || UsStates.ContainsValue(last))
                return (string.Join(' ', parts[..^1]), last);
        }
        return (city.Trim(), "");
    }

    static bool MatchesHint(JsonElement r, string hint)
    {
        string admin1 = Str(r, "admin1"), cc = Str(r, "country_code");
        if (UsStates.TryGetValue(hint, out var state)) return cc == "US" && admin1.Equals(state, StringComparison.OrdinalIgnoreCase);
        return cc.Equals(hint, StringComparison.OrdinalIgnoreCase) || admin1.Equals(hint, StringComparison.OrdinalIgnoreCase);
    }

    static string PlaceName(string city, string region, string country)
    {
        if (city.Length == 0) return region.Length > 0 ? region : country;
        if (country is "US" or "CA" or "AU" && region.Length > 0 && region.Length <= 3) return $"{city} {region}";
        if (region.Length > 0 && region.Length <= 3) return $"{city} {region}";
        return country.Length > 0 && country != "US" ? $"{city}, {country}" : city;
    }

    public static async Task<WeatherNow?> Fetch(double lat, double lon)
    {
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + F(lat) + "&longitude=" + F(lon) +
                     "&current=temperature_2m,weather_code,cloud_cover,precipitation,is_day,wind_speed_10m,wind_direction_10m," +
                     "wind_gusts_10m,cloud_cover_low,cloud_cover_mid,cloud_cover_high,visibility,relative_humidity_2m,snowfall,snow_depth" +
                     "&temperature_unit=fahrenheit&wind_speed_unit=kmh&timezone=auto";
        using var doc = await GetJson(url);
        if (doc == null || !doc.RootElement.TryGetProperty("current", out var c)) return null;
        return new WeatherNow
        {
            Code = (int)Num(c, "weather_code"),
            TempF = Num(c, "temperature_2m"),
            Cloud = Num(c, "cloud_cover"),
            Precip = Num(c, "precipitation"),
            IsDay = Num(c, "is_day", 1) >= 0.5,
            WindKmh = Num(c, "wind_speed_10m"),
            WindDir = Num(c, "wind_direction_10m"),
            WindGust = Num(c, "wind_gusts_10m"),
            CloudLow = Num(c, "cloud_cover_low", -1),
            CloudMid = Num(c, "cloud_cover_mid", -1),
            CloudHigh = Num(c, "cloud_cover_high", -1),
            Visibility = Num(c, "visibility", -1),
            Humidity = Num(c, "relative_humidity_2m", -1),
            SnowfallCm = Num(c, "snowfall"),
            SnowDepthM = Num(c, "snow_depth"),
            At = DateTime.UtcNow,
        };
    }

    // ------------------------------------------------------------------ WMO code -> words and effects

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 => "Light drizzle", 53 => "Drizzle", 55 => "Heavy drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain", 63 => "Rain", 65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow", 73 => "Snow", 75 => "Heavy snow", 77 => "Snow grains",
        80 => "Light showers", 81 => "Showers", 82 => "Heavy showers",
        85 => "Light snow showers", 86 => "Snow showers",
        95 => "Thunderstorm", 96 or 99 => "Thunderstorm with hail",
        _ => "Weather",
    };

    public static Mix MixFor(WeatherNow w, double lat, double lon)
    {
        var m = new Mix();
        double p = w.Precip;
        switch (w.Code)
        {
            case 0: if (w.IsDay) m.Glow = 1; else m.Night = 1; break;
            case 1: if (w.IsDay) m.Glow = 0.85f; else m.Night = 1; m.Wisps = 0.2f; break;
            case 2: if (w.IsDay) m.Glow = 0.55f; else m.Night = 0.5f; m.Wisps = 0.6f; break;
            case 3: m.Haze = 0.75f; break;
            case 45: case 48: m.Haze = 1f; break;
            case 51: case 56: m.Rain = 0.28f; m.Haze = 0.35f; break;
            case 53: m.Rain = 0.4f; m.Haze = 0.4f; break;
            case 55: case 57: m.Rain = 0.55f; m.Haze = 0.45f; break;
            case 61: case 80: m.Rain = 0.5f; m.Haze = 0.35f; break;
            case 63: case 66: case 81: m.Rain = 0.75f; m.Haze = 0.4f; break;
            case 65: case 67: case 82: m.Rain = 1f; m.Haze = 0.45f; break;
            case 71: case 85: m.Snow = 0.5f; m.Haze = 0.15f; break;
            case 77: m.Snow = 0.4f; m.Haze = 0.15f; break;
            case 73: m.Snow = 0.75f; m.Haze = 0.2f; break;
            case 75: case 86: m.Snow = 1f; m.Haze = 0.25f; break;
            case 95: case 96: case 99: m.Rain = 0.9f; m.Storm = 1f; m.Haze = 0.5f; break;
            default:
                if (w.Cloud > 80) m.Haze = 0.6f; else if (w.Cloud > 30) m.Wisps = 0.5f; else if (w.IsDay) m.Glow = 1; else m.Night = 1;
                break;
        }
        // Clouds at their real heights: Open-Meteo reports cover for low, mid and high cloud separately.
        if (w.CloudLow >= 0) { m.Low = (float)(w.CloudLow / 100); m.Mid = (float)(w.CloudMid / 100); m.High = (float)(w.CloudHigh / 100); }
        else
        {
            if (w.Code == 2) { m.Mid = 0.35f; m.High = 0.3f; }
            else if (w.Code == 3) { m.Low = 0.5f; m.Mid = 0.8f; }
            else if (w.Code == 1) m.High = 0.25f;
        }
        // Fog thickness from how far you can see: about 10 km or more is clear, 200 m is as thick as it gets.
        m.Fog = FogFromVisibility(w.Visibility);
        if (w.Code is 45 or 48) m.Fog = MathF.Max(m.Fog, 0.6f);
        // The old flat haze only stays as a faint overcast sheet when it is overcast or foggy.
        m.Wisps = 0;
        // Falling leaves in autumn on dry days; the wind brings more down.
        bool dry = m.Rain == 0 && m.Snow == 0 && m.Storm == 0;
        if (dry) m.Leaves = World.AutumnAmount(lat, DateTime.Now) * (float)Math.Clamp(0.25 + w.WindKmh / 30 + w.WindGust / 60, 0, 1);
        // Hail with thunderstorms (codes 96, 99).
        if (w.Code is 96 or 99) m.Hail = 0.7f;
        // Frost creeps in when it is freezing and the air is humid.
        if (w.TempF <= 34 && w.Humidity >= 0)
            m.Frost = (float)(Math.Clamp((34 - w.TempF) / 10, 0, 1) * Math.Clamp((w.Humidity - 50) / 40, 0.25, 1));
        // Bad air: a faint smoggy tint from AQI 60 up, strongest by 200.
        if (w.Aqi >= 0) m.Smog = (float)Math.Clamp((w.Aqi - 60) / 140, 0, 1);
        // Aurora: strong enough for this geomagnetic latitude, dark and clear enough to see.
        m.Aurora = AuroraAmount(w, lat, lon, m);
        // Real snowfall rate (cm in the last hour) can make it heavier than the code says.
        if (m.Snow > 0) m.Snow = (float)Math.Clamp(Math.Max(m.Snow, w.SnowfallCm / 2.0), 0, 1);
        // Heavier precipitation than the code suggests: scale up.
        if (m.Rain > 0) m.Rain = (float)Math.Clamp(Math.Max(m.Rain, p / 4.0), 0, 1);
        if (m.Snow > 0) m.Snow = (float)Math.Clamp(Math.Max(m.Snow, p / 2.5), 0, 1);
        return m;
    }

    static async Task<double> FetchAqi(double lat, double lon)
    {
        using var doc = await GetJson($"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={F(lat)}&longitude={F(lon)}&current=us_aqi");
        return doc != null && doc.RootElement.TryGetProperty("current", out var c) ? Num(c, "us_aqi", -1) : -1;
    }

    static async Task<double> FetchKp()
    {
        using var doc = await GetJson("https://services.swpc.noaa.gov/products/noaa-planetary-k-index.json");
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return -1;
        var last = doc.RootElement[doc.RootElement.GetArrayLength() - 1];
        if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("Kp", out var k))
            return k.ValueKind == JsonValueKind.Number ? k.GetDouble() : double.TryParse(k.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
        if (last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 1)
        {
            var e = last[1];
            return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
        }
        return -1;
    }

    /// <summary>0..1 how visible an aurora would be here: Kp against geomagnetic latitude, then darkness and cloud.</summary>
    public static float AuroraAmount(WeatherNow w, double lat, double lon, Mix m)
    {
        if (w.Kp < 0) return 0;
        // Geomagnetic latitude from the dipole pole (80.7 N, 72.7 W).
        double d2r = Math.PI / 180, pl = 80.7 * d2r, pn = -72.7 * d2r, la = lat * d2r, lo = lon * d2r;
        double gm = Math.Asin(Math.Sin(la) * Math.Sin(pl) + Math.Cos(la) * Math.Cos(pl) * Math.Cos(lo - pn)) / d2r;
        // The oval's equatorward edge is near 66.5 - 2.6 Kp degrees; seen low on the horizon about 5 degrees beyond.
        double edge = 66.5 - 2.6 * w.Kp - 5;
        float strength = (float)Math.Clamp((Math.Abs(gm) - edge) / 6, 0, 1);
        var (_, el) = Sun(lat, lon, DateTime.UtcNow);
        float dark = (float)Math.Clamp((-el - 10) / 6, 0, 1);
        float clear = Math.Clamp(1 - (m.Low + m.Mid * 0.8f + m.Fog), 0, 1);
        return strength * dark * clear;
    }

    /// <summary>Low-precision moon position and phase (about a degree): azimuth/altitude in degrees, phase 0 new .. 0.5 full.</summary>
    public static (double az, double alt, double phase) Moon(double lat, double lon, DateTime utc)
    {
        double d2r = Math.PI / 180;
        double jd = utc.ToOADate() + 2415018.5, n = jd - 2451545.0;
        double L = (218.316 + 13.176396 * n) % 360, M = ((134.963 + 13.064993 * n) % 360) * d2r, Fm = ((93.272 + 13.229350 * n) % 360) * d2r;
        double lam = (L + 6.289 * Math.Sin(M)) * d2r, beta = 5.128 * Math.Sin(Fm) * d2r, eps = 23.439 * d2r;
        double ra = Math.Atan2(Math.Sin(lam) * Math.Cos(eps) - Math.Tan(beta) * Math.Sin(eps), Math.Cos(lam));
        double dec = Math.Asin(Math.Sin(beta) * Math.Cos(eps) + Math.Cos(beta) * Math.Sin(eps) * Math.Sin(lam));
        double gmst = (18.697374558 + 24.06570982441908 * n) % 24;
        double ha = (gmst * 15 + lon) * d2r - ra, la = lat * d2r;
        double alt = Math.Asin(Math.Sin(la) * Math.Sin(dec) + Math.Cos(la) * Math.Cos(dec) * Math.Cos(ha));
        double az = Math.Atan2(-Math.Sin(ha), Math.Tan(dec) * Math.Cos(la) - Math.Sin(la) * Math.Cos(ha));
        double phase = ((jd - 2451550.1) / 29.530588853) % 1; if (phase < 0) phase += 1;
        return ((az / d2r + 360) % 360, alt / d2r, phase);
    }

    public static string MoonName(double p) => p switch
    {
        < 0.03 or >= 0.97 => "New moon", < 0.22 => "Waxing crescent", < 0.28 => "First quarter", < 0.47 => "Waxing gibbous",
        < 0.53 => "Full moon", < 0.72 => "Waning gibbous", < 0.78 => "Last quarter", _ => "Waning crescent",
    };

    public static float FogFromVisibility(double metres)
    {
        if (metres < 0) return 0;
        return (float)Math.Clamp(1 - Math.Log10(Math.Max(metres, 1) / 200) / Math.Log10(50), 0, 1);
    }

    public static Mix MixForOverride(string effect, bool isDay) => effect switch
    {
        "Snow" => new Mix { Snow = 0.85f, Low = 0.5f, Mid = 0.3f },
        "Rain" => new Mix { Rain = 0.75f, Haze = 0.3f, Low = 0.8f, Mid = 0.4f },
        "Clouds" => new Mix { Haze = 0.6f, Low = 0.35f, Mid = 0.7f, High = 0.35f },
        "Fog" => new Mix { Fog = 0.8f, Low = 0.25f, Haze = 0.4f },
        "Sun" => new Mix { Glow = 1f, High = 0.15f },
        "Sunset" => new Mix { Glow = 0.8f, Low = 0.15f, Mid = 0.45f, High = 0.45f },
        "Leaves" => new Mix { Leaves = 1f, Mid = 0.25f, High = 0.2f, Glow = 0.5f },
        "Night" => new Mix { Night = 1f },
        "Storm" => new Mix { Rain = 0.9f, Storm = 1f, Haze = 0.45f, Low = 1f, Mid = 0.6f },
        "Hail" => new Mix { Hail = 1f, Rain = 0.35f, Low = 0.9f, Mid = 0.4f },
        "Frost" => new Mix { Frost = 1f, High = 0.2f },
        "Aurora" => new Mix { Aurora = 1f, Night = 0.6f },
        _ => new Mix(),
    };

    /// <summary>Horizontal wind in screen px/s (facing south: east is left). + = drifts right.</summary>
    public static float WindX(WeatherNow w)
    {
        double toward = Math.Sin(w.WindDir * Math.PI / 180.0); // wind blowing FROM dir
        return (float)Math.Clamp(toward * w.WindKmh * 1.6, -70, 70);
    }

    // ------------------------------------------------------------------ sun position (NOAA approximation)

    public static (double az, double elev) Sun(double lat, double lon, DateTime utc)
    {
        double d2r = Math.PI / 180;
        double jd = utc.ToOADate() + 2415018.5;
        double n = jd - 2451545.0;
        double L = (280.460 + 0.9856474 * n) % 360;
        double g = ((357.528 + 0.9856003 * n) % 360) * d2r;
        double lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * d2r;
        double eps = (23.439 - 0.0000004 * n) * d2r;
        double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
        double dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
        double gmst = (18.697374558 + 24.06570982441908 * n) % 24;
        double ha = ((gmst * 15 + lon) * d2r) - ra;
        double la = lat * d2r;
        double elev = Math.Asin(Math.Sin(la) * Math.Sin(dec) + Math.Cos(la) * Math.Cos(dec) * Math.Cos(ha));
        double az = Math.Atan2(-Math.Sin(ha), Math.Tan(dec) * Math.Cos(la) - Math.Sin(la) * Math.Cos(ha));
        double azd = (az / d2r + 360) % 360;
        return (azd, elev / d2r);
    }
}

public static class Geo
{
    /// <summary>Windows location (asked once; the system decides via Settings > Privacy > Location).</summary>
    public static async Task<(double lat, double lon)?> TryWindowsLocation()
    {
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
            {
                Log.Write($"Windows location not allowed ({access}); using IP location");
                return null;
            }
            var g = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
            var task = g.GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(12)).AsTask();
            if (await Task.WhenAny(task, Task.Delay(15000)) != task) { Log.Write("Windows location timed out"); return null; }
            var p = task.Result.Coordinate.Point.Position;
            return (p.Latitude, p.Longitude);
        }
        catch (Exception e)
        {
            Log.Write($"Windows location failed: {e.GetType().Name} {e.Message}");
            return null;
        }
    }
}

public static class Log
{
    static readonly object Lock = new();
    static string PathName => System.IO.Path.Combine(Store.Dir, "log.txt");

    public static void Write(string s)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    var keep = File.ReadAllLines(PathName);
                    File.WriteAllLines(PathName, keep.Skip(keep.Length / 2));
                }
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
