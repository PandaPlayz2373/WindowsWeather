using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using DxgiAlpha = Vortice.DXGI.AlphaMode;

namespace WindowsWeather;

/// <summary>Regions of the sprite atlas. Every effect is drawn from these few pre-rendered sprites.</summary>
public static class Atlas
{
    public const int Size = 512;
    public static RawRect Flake32, Flake16, Flake8, Cap, Body, Streak, Bead, Glint, Glow, Vignette, Dot, Solid, Ring, Puff, Puff2, Band, EdgeLight, Flare, Leaf, Leaf2, PuffLight, Frost, Moon, AuroraRay;
}

/// <summary>One growable list of sprites, submitted to D2D in a single sprite-batch call per overlay.</summary>
public sealed class SpriteList
{
    public RawRectF[] Dest = new RawRectF[4096];
    public RawRect[] Src = new RawRect[4096];
    public Color4[] Col = new Color4[4096];
    public Matrix3x2[] Xf = new Matrix3x2[4096];
    public int Count;
    public bool AnyTransform;

    public void Clear() { Count = 0; AnyTransform = false; }

    void Grow()
    {
        int n = Dest.Length * 2;
        Array.Resize(ref Dest, n); Array.Resize(ref Src, n); Array.Resize(ref Col, n); Array.Resize(ref Xf, n);
    }

    public void Add(in RawRect src, float l, float t, float r, float b, Color4 premul)
    {
        if (Count == Dest.Length) Grow();
        Dest[Count] = new RawRectF(l, t, r, b); Src[Count] = src; Col[Count] = premul; Xf[Count] = Matrix3x2.Identity;
        Count++;
    }

    public void AddRotated(in RawRect src, float l, float t, float r, float b, Color4 premul, in Matrix3x2 xf)
    {
        if (Count == Dest.Length) Grow();
        Dest[Count] = new RawRectF(l, t, r, b); Src[Count] = src; Col[Count] = premul; Xf[Count] = xf;
        AnyTransform = true;
        Count++;
    }
}

public sealed class Gpu : IDisposable
{
    public ID3D11Device D3D = null!;
    public IDXGIDevice Dxgi = null!;
    public IDXGIFactory2 Factory = null!;
    public ID2D1Factory1 D2DFactory = null!;
    public ID2D1Device D2DDevice = null!;
    public ID2D1DeviceContext3 Ctx = null!;
    public IDCompositionDevice DComp = null!;
    public ID2D1Bitmap1 AtlasBmp = null!;
    public ID2D1SpriteBatch Batch = null!;
    public bool SpriteBatchOk;
    public ID2D1SolidColorBrush FrostBrush = null!;
    public ID2D1StrokeStyle RoundStroke = null!;

    public Gpu()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        // BGRA support (needed by D2D) + single-threaded (no internal locking; everything runs on one thread).
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.Singlethreaded;
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out ID3D11Device? dev, out _, out ID3D11DeviceContext? imm);
        if (hr.Failure || dev == null)
        {
            hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out dev, out _, out imm);
            hr.CheckError();
        }
        imm?.Dispose();
        D3D = dev!;
        Dxgi = D3D.QueryInterface<IDXGIDevice>();
        // Keep at most one frame queued: less latency, less memory.
        using (var d1 = Dxgi.QueryInterfaceOrNull<IDXGIDevice1>()) d1?.SetMaximumFrameLatency(1);
        Factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        D2DDevice = D2DFactory.CreateDevice(Dxgi);
        using (var ctx = D2DDevice.CreateDeviceContext(DeviceContextOptions.None))
            Ctx = ctx.QueryInterface<ID2D1DeviceContext3>();
        Ctx.AntialiasMode = AntialiasMode.Aliased; // required by sprite batches
        DComp = DComp_Create(Dxgi);
        try { Batch = Ctx.CreateSpriteBatch(); SpriteBatchOk = true; } catch { SpriteBatchOk = false; }

        var atlasPx = BuildAtlas();
        int Alpha(RawRect r) => atlasPx[(((r.Top + r.Bottom) / 2) * Atlas.Size + (r.Left + r.Right) / 2) * 4 + 3];
        Log.Write($"atlas centre alpha: puff={Alpha(Atlas.Puff)} puff2={Alpha(Atlas.Puff2)} flake32={Alpha(Atlas.Flake32)} band={Alpha(Atlas.Band)} solid={Alpha(Atlas.Solid)}");
        AtlasBmp = MakeBitmap(Atlas.Size, Atlas.Size, atlasPx);
        FrostBrush = Ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        RoundStroke = D2DFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
    }

    static IDCompositionDevice DComp_Create(IDXGIDevice dxgi) => Vortice.DirectComposition.DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);

    public unsafe ID2D1Bitmap1 MakeBitmap(int w, int h, byte[] bgra)
    {
        fixed (byte* p = bgra)
            return Ctx.CreateBitmap(new SizeI(w, h), (nint)p, (uint)(w * 4),
                new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
    }

    public ID2D1Bitmap1 MakeEmptyBitmap(int w, int h) => MakeBitmap(w, h, new byte[w * h * 4]);

    /// <summary>Paint the moon for a phase (0 new .. 0.5 full .. 1) into its atlas slot: lit part only, soft terminator.</summary>
    public void PaintMoon(float phase)
    {
        var r = Atlas.Moon; int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var px = new byte[w * h * 4];
        float c2 = MathF.Cos(2 * MathF.PI * (phase < 0.5f ? phase : phase - 0.5f));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0, shade = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                    {
                        float u = ((x + (sx + 0.5f) / 3f) / w) * 2 - 1, v = ((y + (sy + 0.5f) / 3f) / h) * 2 - 1;
                        float d2 = u * u + v * v;
                        if (d2 > 0.92f) continue;
                        float half = MathF.Sqrt(MathF.Max(0, 0.92f - v * v));
                        // Waxing: lit to the right of the terminator; waning: lit to the left.
                        float t = c2 * half;
                        float litv = phase < 0.5f ? Smooth(t - 0.06f, t + 0.06f, u) : 1 - Smooth(-t - 0.06f, -t + 0.06f, u);
                        acc += litv;
                        // Faint maria so it reads as the moon, not a disc.
                        float mare = MathF.Exp(-((u + 0.25f) * (u + 0.25f) + (v + 0.2f) * (v + 0.2f)) * 9) + 0.7f * MathF.Exp(-((u - 0.2f) * (u - 0.2f) + (v - 0.15f) * (v - 0.15f)) * 14);
                        shade += litv * (1 - 0.22f * mare);
                    }
                float a = acc / 9f, c = acc > 0 ? shade / acc : 1;
                int i = (y * w + x) * 4;
                byte bv = (byte)(c * a * 255), av = (byte)(a * 255);
                px[i] = bv; px[i + 1] = bv; px[i + 2] = bv; px[i + 3] = av;
            }
        AtlasBmp.CopyFromMemory(new System.Drawing.Rectangle(r.Left, r.Top, w, h), px, (uint)(w * 4));
    }

    public void Dispose()
    {
        AtlasBmp?.Dispose(); FrostBrush?.Dispose(); RoundStroke?.Dispose();
        Batch?.Dispose(); DComp?.Dispose(); Ctx?.Dispose(); D2DDevice?.Dispose(); D2DFactory?.Dispose();
        Factory?.Dispose(); Dxgi?.Dispose(); D3D?.Dispose();
    }

    // ---------------------------------------------------------------- atlas painting (CPU, once at start)

    delegate (float r, float g, float b, float a) Shader(float u, float v); // u,v in [0,1], returns straight colour

    static int _px, _py, _rowH;

    static RawRect Place(byte[] img, int w, int h, Shader f)
    {
        if (_px + w + 2 > Atlas.Size) { _px = 1; _py += _rowH + 2; _rowH = 0; }
        int x0 = _px, y0 = _py;
        _px += w + 2; _rowH = Math.Max(_rowH, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // 4x4 supersample for clean edges
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        var c = f((x + (sx + 0.5f) / 4f) / w, (y + (sy + 0.5f) / 4f) / h);
                        float ca = Math.Clamp(c.a, 0, 1);
                        r += c.r * ca; g += c.g * ca; b += c.b * ca; a += ca;
                    }
                int i = ((y0 + y) * Atlas.Size + x0 + x) * 4;
                img[i + 0] = (byte)Math.Clamp(b / 16f * 255f + 0.5f, 0, 255);
                img[i + 1] = (byte)Math.Clamp(g / 16f * 255f + 0.5f, 0, 255);
                img[i + 2] = (byte)Math.Clamp(r / 16f * 255f + 0.5f, 0, 255);
                img[i + 3] = (byte)Math.Clamp(a / 16f * 255f + 0.5f, 0, 255);
            }
        return new RawRect(x0, y0, x0 + w, y0 + h);
    }

    static RawRect Inset(RawRect r, int dx, int dy) => new(r.Left + dx, r.Top + dy, r.Right - dx, r.Bottom - dy);

    static float Smooth(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }

    static byte[] BuildAtlas()
    {
        var img = new byte[Atlas.Size * Atlas.Size * 4];
        _px = 1; _py = 1; _rowH = 0;

        // Flake: white core, soft falloff, and a faint blue-grey rim so it still reads over a white window.
        Shader flake = (u, v) =>
        {
            float d = MathF.Sqrt((u - .5f) * (u - .5f) + (v - .5f) * (v - .5f)) * 2f;
            float core = 1 - Smooth(0.35f, 0.62f, d);
            float rim = (1 - Smooth(0.62f, 1f, d)) * 0.55f;
            float a = MathF.Max(core, rim);
            float k = core >= rim ? 1f : 0.62f;
            return (k + (1 - k) * 0.80f, k + (1 - k) * 0.84f, k + (1 - k) * 0.92f, a);
        };
        Atlas.Flake32 = Place(img, 32, 32, flake);
        Atlas.Flake16 = Place(img, 16, 16, flake);
        Atlas.Flake8 = Place(img, 8, 8, flake);

        // Pile cap: a near-solid white disc with a thin cool rim; overlapping caps make the lumpy top.
        Atlas.Cap = Place(img, 24, 24, (u, v) =>
        {
            float d = MathF.Sqrt((u - .5f) * (u - .5f) + (v - .5f) * (v - .5f)) * 2f;
            float a = 1 - Smooth(0.86f, 1f, d);
            float shade = 1 - 0.20f * Smooth(0.55f, 0.95f, d) - 0.06f * Smooth(0.2f, 1f, v);
            return (shade * 0.98f, shade * 0.99f, shade, a);
        });
        // Pile body: white at the top, a touch of blue shadow towards the window edge.
        Atlas.Body = Place(img, 8, 32, (u, v) =>
        {
            float s = 1 - 0.13f * Smooth(0.3f, 1f, v);
            return (s * 0.95f, s * 0.97f, s, 1);
        });
        // Stretched sprites must not sample the transparent gutter at their edges (it shows as seams).
        Atlas.Body = Inset(Atlas.Body, 2, 1);
        // Rain streak: thin, bright head at the bottom, fading tail.
        Atlas.Streak = Place(img, 6, 48, (u, v) =>
        {
            float x = MathF.Abs(u - .5f) * 2f;
            float a = (1 - Smooth(0.25f, 1f, x)) * Smooth(0f, 0.85f, v) * (1 - Smooth(0.92f, 1f, v) * 0.6f);
            return (0.78f, 0.85f, 0.95f, a);
        });
        // Bead: a water drop with a dark rim and a highlight.
        Atlas.Bead = Place(img, 16, 16, (u, v) =>
        {
            float d = MathF.Sqrt((u - .5f) * (u - .5f) + (v - .55f) * (v - .55f)) * 2.1f;
            float a = 1 - Smooth(0.75f, 1f, d);
            float hl = 1 - Smooth(0f, 0.28f, MathF.Sqrt((u - .38f) * (u - .38f) + (v - .38f) * (v - .38f)) * 2f);
            float rim = Smooth(0.45f, 0.9f, d);
            float c = 0.72f - rim * 0.25f + hl * 0.3f;
            return (c * 0.92f, c * 0.96f, MathF.Min(1, c * 1.05f), a * (0.55f + 0.4f * rim + 0.4f * hl));
        });
        // Glint / twinkle: a soft four-point star.
        Atlas.Glint = Place(img, 32, 32, (u, v) =>
        {
            float x = (u - .5f) * 2f, y = (v - .5f) * 2f;
            float d = MathF.Sqrt(x * x + y * y);
            float glow = MathF.Exp(-d * d * 9f);
            float rays = MathF.Exp(-MathF.Abs(x) * 14f) * (1 - Smooth(0f, 1f, MathF.Abs(y))) +
                         MathF.Exp(-MathF.Abs(y) * 14f) * (1 - Smooth(0f, 1f, MathF.Abs(x)));
            return (1, 1, 1, Math.Clamp(glow + rays * 0.7f, 0, 1));
        });
        // Sun glow band: the window edge sits at 2/3 of the height. Light spills mostly upward.
        Atlas.Glow = Place(img, 8, 48, (u, v) =>
        {
            float e = 2f / 3f, a = v < e ? MathF.Exp(-MathF.Pow((e - v) / 0.30f, 2)) : MathF.Exp(-MathF.Pow((v - e) / 0.12f, 2));
            return (1, 1, 1, a);
        });
        Atlas.Glow = Inset(Atlas.Glow, 3, 0);
        // Storm flash: transparent centre, light at the screen edges.
        Atlas.Vignette = Place(img, 64, 64, (u, v) =>
        {
            float x = (u - .5f) * 2f, y = (v - .5f) * 2f;
            float d = MathF.Max(MathF.Abs(x), MathF.Abs(y)) * 0.6f + MathF.Sqrt(x * x + y * y) * 0.4f;
            return (1, 1, 1, Smooth(0.45f, 1.05f, d));
        });
        // Small solid-ish dot for splashes and twinkles.
        Atlas.Dot = Place(img, 8, 8, (u, v) =>
        {
            float d = MathF.Sqrt((u - .5f) * (u - .5f) + (v - .5f) * (v - .5f)) * 2f;
            return (0.85f, 0.9f, 1, 1 - Smooth(0.4f, 1f, d));
        });
        // Flat white for puddle bodies and their shine line (inset so stretching never samples the gutter).
        Atlas.Solid = Inset(Place(img, 8, 8, (u, v) => (1, 1, 1, 1)), 2, 2);
        // Ripple ring, squashed flat when drawn on a puddle.
        Atlas.Ring = Place(img, 32, 32, (u, v) =>
        {
            float d = MathF.Sqrt((u - .5f) * (u - .5f) + (v - .5f) * (v - .5f)) * 2f;
            return (1, 1, 1, MathF.Exp(-MathF.Pow((d - 0.78f) / 0.11f, 2)));
        });
        // Cloud puffs: soft round blobs with a cauliflower edge (fractal noise), two variants.
        Atlas.Puff = Place(img, 96, 80, PuffShader(11));
        Atlas.Puff2 = Place(img, 96, 80, PuffShader(23));
        // Overcast sheet: fades from the top of the screen down to nothing.
        Atlas.Band = Inset(Place(img, 8, 64, (u, v) => (1, 1, 1, MathF.Pow(1 - v, 1.6f))), 2, 0);
        // Leaves: white-ish silhouettes with darker veins, tinted autumn colours when drawn.
        Atlas.Leaf = Place(img, 32, 32, (u, v) =>
        {
            // A five-lobed (maple-like) leaf, stem down.
            float x = (u - .5f) * 2f, y = (.55f - v) * 2f;
            float r = MathF.Sqrt(x * x + y * y), th = MathF.Atan2(x, y);
            float lobes = 0.52f + 0.30f * MathF.Pow(MathF.Abs(MathF.Cos(th * 2.5f)), 0.7f) - 0.18f * MathF.Max(0, -MathF.Cos(th));
            float a = 1 - Smooth(lobes - 0.05f, lobes + 0.02f, r);
            float stem = (MathF.Abs(x) < 0.05f && y < -0.2f && y > -0.95f) ? 1 : 0;
            float vein = MathF.Exp(-MathF.Pow(MathF.Sin(th * 2.5f) * r / 0.05f, 2)) * (r < lobes - 0.08f ? 1 : 0);
            float c = 0.95f - 0.25f * vein - 0.12f * Smooth(0.2f, 0.8f, r);
            return (c, c, c, MathF.Max(a, stem * 0.9f));
        });
        Atlas.Leaf2 = Place(img, 32, 32, (u, v) =>
        {
            // A simple pointed oval leaf with a midrib.
            float x = (u - .5f) * 2f, y = (v - .5f) * 2f;
            float half = 0.46f * MathF.Pow(MathF.Max(0, 1 - y * y), 0.75f);
            float a = 1 - Smooth(half - 0.06f, half + 0.02f, MathF.Abs(x));
            float rib = MathF.Exp(-MathF.Pow(x / 0.035f, 2));
            float side = MathF.Exp(-MathF.Pow((MathF.Abs(x) - (y + 1) * 0.18f) / 0.03f, 2)) * 0.5f;
            float c = 0.95f - 0.28f * MathF.Max(rib, side) - 0.12f * Smooth(0.1f, 0.5f, MathF.Abs(x));
            return (c, c, c, a);
        });
        // Light sprites: colour with zero alpha (premultiplied "additive" texels). Composited over the desktop they
        // only add brightness, which is what sunlight on an edge looks like.
        Atlas.EdgeLight = PlaceLight(img, 8, 32, (u, v) =>
        {
            const float e = 0.25f;
            float rim = MathF.Exp(-MathF.Pow((v - e) / 0.035f, 2));                   // thin bright line on the edge
            float bloom = v >= e ? 0.42f * MathF.Exp(-(v - e) / 0.22f) : 0.30f * MathF.Exp(-MathF.Pow((e - v) / 0.12f, 2));
            return MathF.Min(1, rim + bloom);
        });
        Atlas.EdgeLight = Inset(Atlas.EdgeLight, 3, 0);
        // Frost: a corner piece (the corner at the texture's top-left), feathery crystals thinning out inwards.
        {
            var rnd = new Random(5); var lat = new float[32 * 32];
            for (int i = 0; i < lat.Length; i++) lat[i] = (float)rnd.NextDouble();
            float N(float x, float y)
            {
                int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
                float fx = x - x0, fy = y - y0; fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
                float L(int ix, int iy) => lat[(iy & 31) * 32 + (ix & 31)];
                float a = L(x0, y0), b = L(x0 + 1, y0), c = L(x0, y0 + 1), d = L(x0 + 1, y0 + 1);
                return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
            }
            Atlas.Frost = Place(img, 128, 128, (u, v) =>
            {
                float d = MathF.Sqrt(u * u + v * v);
                float n = 0.5f * N(u * 6, v * 6) + 0.3f * N(u * 14 + 5, v * 14 + 2) + 0.2f * N(u * 30, v * 30 + 9);
                float th = MathF.Atan2(v, u);
                float crystal = MathF.Pow(MathF.Abs(MathF.Sin(th * 23 + n * 7)), 6) * 0.6f + MathF.Pow(1 - MathF.Abs(n * 2 - 1), 3) * 0.5f;
                float body = 1 - Smooth(0.25f, 1.0f, d + 0.30f * (n - 0.5f));
                float a = body * (0.25f + 0.75f * MathF.Min(1, crystal + 0.35f * body * body));
                return (0.95f, 0.98f, 1f, a);
            });
        }
        // The moon: painted at run time for the current phase (see PaintMoon).
        Atlas.Moon = Place(img, 64, 64, (u, v) => (0, 0, 0, 0));
        // Aurora ray: light, bright and sharp at the bottom edge, fading upwards and to the sides.
        Atlas.AuroraRay = PlaceLight(img, 12, 64, (u, v) =>
        {
            float side = MathF.Pow(MathF.Max(0, 1 - MathF.Abs(u - .5f) * 2), 0.9f);
            return side * Smooth(0f, 0.9f, v) * MathF.Pow(v, 1.4f) * (1 - Smooth(0.94f, 1f, v));
        });
        // Light inside a cloud (sunset colour): strongest in the middle, nothing at the outline.
        Atlas.PuffLight = PlaceLight(img, 48, 40, (u, v) =>
        {
            float x = (u - .5f) * 2f, y = (v - .5f) * 2f, d2 = x * x + y * y;
            return MathF.Max(0, MathF.Exp(-d2 * 3.2f) - 0.04f);
        });
        Atlas.Flare = PlaceLight(img, 48, 48, (u, v) =>
        {
            float x = (u - .5f) * 2f, y = (v - .5f) * 2f, d2 = x * x + y * y;
            return MathF.Min(1, 0.55f * MathF.Exp(-d2 * 5f) + 0.45f * MathF.Exp(-d2 * 40f));
        });
        return img;
    }

    /// <summary>Paint a light sprite: rgb = intensity, alpha = 0.</summary>
    static RawRect PlaceLight(byte[] img, int w, int h, Func<float, float, float> f)
    {
        var r = Place(img, w, h, (u, v) => (0, 0, 0, 0)); // reserve the space
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                        s += f((x + (sx + 0.5f) / 4f) / w, (y + (sy + 0.5f) / 4f) / h);
                byte v = (byte)Math.Clamp(s / 16f * 255f + 0.5f, 0, 255);
                int i = ((r.Top + y) * Atlas.Size + r.Left + x) * 4;
                img[i] = v; img[i + 1] = v; img[i + 2] = v; img[i + 3] = 0;
            }
        return r;
    }

    static Shader PuffShader(int seed)
    {
        var rnd = new Random(seed);
        var lat = new float[16 * 16];
        for (int i = 0; i < lat.Length; i++) lat[i] = (float)rnd.NextDouble();
        float N(float x, float y)
        {
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0; fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float L(int ix, int iy) => lat[(iy & 15) * 16 + (ix & 15)];
            float a = L(x0, y0), b = L(x0 + 1, y0), c = L(x0, y0 + 1), d = L(x0 + 1, y0 + 1);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }
        return (u, v) =>
        {
            float x = (u - .5f) * 2f, y = (v - .5f) * 2f;
            float d = MathF.Sqrt(x * x + y * y);
            float n = 0.5f * N(u * 4, v * 4) + 0.3f * N(u * 8 + 3, v * 8 + 7) + 0.2f * N(u * 16 + 9, v * 16 + 1);
            float edge = 0.62f + 0.32f * n;                // lumpy outline
            float a = 1 - Smooth(edge - 0.55f, edge + 0.04f, d); // long, soft falloff to that outline
            a = MathF.Pow(a, 1.4f);                             // feathered edge: it melts into the background
            a *= 0.78f + 0.22f * n;                             // a little internal texture
            float shade = 1 - 0.10f * Smooth(-0.2f, 1f, y); // slightly darker underside
            return (shade, shade, shade, a);
        };
    }
}

/// <summary>A click-through, topmost, never-activated window covering one monitor, showing a DComp swap chain.</summary>
public sealed unsafe class Overlay : IDisposable
{
    public nint Hwnd;
    public RECT Bounds;
    public float Scale;
    public IDXGISwapChain1 Swap = null!;
    public ID2D1Bitmap1 Target = null!;
    IDCompositionTarget _dtarget = null!;
    IDCompositionVisual _visual = null!;

    public bool Visible;
    public bool HasContent; // last presented frame had something on it

    public static nint ClassAtom;

    // One pixel shorter than the monitor: a topmost window covering the whole monitor makes Windows
    // believe a full-screen app is running (QUNS_BUSY), which mutes notifications and trips our own pause.
    public int WinH => Math.Max(1, Bounds.Height - 1);

    public Overlay(Gpu gpu, RECT bounds, float scale, bool hideFromCapture)
    {
        Bounds = bounds; Scale = scale;
        fixed (char* cls = "WindowsWeatherOverlay")
        fixed (char* title = "Windows Weather overlay")
        {
            Hwnd = Native.CreateWindowExW(
                Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                Native.WS_EX_TOPMOST | Native.WS_EX_NOREDIRECTIONBITMAP,
                cls, title, Native.WS_POPUP, bounds.Left, bounds.Top, bounds.Width, WinH, 0, 0, App.HInstance, 0);
        }
        if (Hwnd == 0) throw new InvalidOperationException("CreateWindowEx failed for overlay");
        Native.SetLayeredWindowAttributes(Hwnd, 0, 255, Native.LWA_ALPHA);
        SetCaptureExclusion(hideFromCapture);

        var desc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, bounds.Width),
            Height = (uint)WinH,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = DxgiAlpha.Premultiplied,
            Flags = SwapChainFlags.None,
        };
        Swap = gpu.Factory.CreateSwapChainForComposition(gpu.D3D, desc, null);
        using (var surface = Swap.GetBuffer<IDXGISurface>(0))
            Target = gpu.Ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
        gpu.DComp.CreateTargetForHwnd(Hwnd, true, out _dtarget!).CheckError();
        gpu.DComp.CreateVisual(out _visual!).CheckError();
        _visual.SetContent(Swap);
        _dtarget.SetRoot(_visual);
        gpu.DComp.Commit();

    }

    public void SetCaptureExclusion(bool on) =>
        Native.SetWindowDisplayAffinity(Hwnd, on ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);

    public void Show()
    {
        if (Visible) return;
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, WinH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Visible = true;
        RaiseTopmost(); // then settle below Halo if it is up
    }

    public void Hide()
    {
        if (!Visible) return;
        Native.ShowWindow(Hwnd, Native.SW_HIDE);
        Visible = false;
    }

    public void RaiseTopmost()
    {
        if (!Visible) return;
        nint after = ZAnchor(Hwnd);
        if (after == -2) return; // already where it should be
        Native.SetWindowPos(Hwnd, after, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // Halo (a floating-island app) must stay above the weather. Re-raising to HWND_TOPMOST covered it
    // for a few milliseconds each time and it flickered . So: if the only visible
    // windows above the overlay are Halo's or ours, leave it alone; otherwise go directly below Halo's lowest window.
    static bool IsHalo(nint h) => Native.ClassName(h).StartsWith("HwndWrapper[Halo;", StringComparison.Ordinal);

    static nint ZAnchor(nint self)
    {
        bool othersAbove = false;
        for (nint h = Native.GetWindow(self, 3 /*GW_HWNDPREV*/); h != 0; h = Native.GetWindow(h, 3))
        {
            if (Native.IsWindowVisible(h) == 0 || Native.IsCloaked(h)) continue;
            uint pid; Native.GetWindowThreadProcessId(h, &pid);
            if (pid == (uint)Environment.ProcessId || IsHalo(h)) continue;
            othersAbove = true; break;
        }
        if (!othersAbove) return -2;
        nint lowestHalo = 0;
        for (nint h = Native.GetWindow(self, 0 /*GW_HWNDFIRST*/); h != 0; h = Native.GetWindow(h, 2 /*GW_HWNDNEXT*/))
        {
            if ((Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) break; // past the topmost band
            if (Native.IsWindowVisible(h) != 0 && IsHalo(h)) lowestHalo = h;
        }
        return lowestHalo != 0 ? lowestHalo : Native.HWND_TOPMOST;
    }

    public void Dispose()
    {
        try { Target?.Dispose(); _visual?.Dispose(); _dtarget?.Dispose(); Swap?.Dispose(); } catch { }
        if (Hwnd != 0) Native.DestroyWindow(Hwnd);
        Hwnd = 0;
    }
}