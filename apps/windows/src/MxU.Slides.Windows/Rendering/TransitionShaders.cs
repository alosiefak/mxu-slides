using System.Windows.Media;
using System.Windows.Media.Effects;
using MxU.Slides.Core.Animation;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// The pixel shaders that draw the Windows-only transitions (Phase 6), one per family in
/// <see cref="PictureShader"/>. Each reads two pictures, the layer's new picture (s0, the effect's
/// own input) and its old one (s1), and the numbers <see cref="PictureTransitions.Mix"/> works out
/// each frame (c0–c7; each shader's comment says what it reads where).
/// <para>
/// They are all ps_2_0, the oldest and smallest kind of pixel shader, on purpose: WPF can run
/// those without a graphics card too, in software. So the transitions also draw in thumbnails, in
/// the self-test's pictures and in the program feed's software copy (section 17 of HOW-IT-WORKS).
/// ps_2_0 allows 64 sums and 32 texture reads per pixel, which is why the blurs take eight
/// samples and do as much as possible with four numbers at a time.
/// </para>
/// <para>
/// WPF reads a picture's edge colour for any point outside it (clamping), so each shader masks
/// what lies off its pictures itself.
/// </para>
/// </summary>
public static class TransitionShaders
{
    // Both pictures are premultiplied (colour already times alpha), so mixing them is plain arithmetic
    // and a transparent part of a layer (round the words of a slide) stays transparent.
    private const string Header = """
        sampler2D next : register(s0);
        sampler2D prev : register(s1);

        """;

    /// <summary>
    /// Push, Slide Over, Slide Reveal, Zoom In and Out, Cube and Flip: two flat pictures placed on
    /// the screen. c0 = (old brightness, new brightness, pixels across, pixels down); c1–c3 and
    /// c4–c6 = the homographies (screen → picture) of the old and new pictures; c7 = (old opacity,
    /// new opacity, old on top, new on top). A picture's edge is smoothed over one output pixel, and
    /// the two half-pixels where they meet add up to a whole one, so a push shows no seam. The
    /// picture on top hides what's under its whole area (its "cover"), not just under its opaque
    /// parts, so old words go under a new slide even though the slide layer is clear round them.
    /// </summary>
    private const string Planes = Header + """
        float4 look : register(c0);
        float4 prevH0 : register(c1);
        float4 prevH1 : register(c2);
        float4 prevH2 : register(c3);
        float4 nextH0 : register(c4);
        float4 nextH1 : register(c5);
        float4 nextH2 : register(c6);
        float4 blend : register(c7);

        float4 Plane(sampler2D picture, float2 uv, float4 h0, float4 h1, float4 h2, out float cover)
        {
            float3 p = float3(uv, 1);
            float q = dot(h2.xyz, p);
            float2 st = float2(dot(h0.xyz, p), dot(h1.xyz, p)) / q;
            float2 lo = saturate(st * look.zw + 0.5);
            float2 hi = saturate((1 - st) * look.zw + 0.5);
            cover = lo.x * lo.y * hi.x * hi.y * step(0, q);
            return tex2D(picture, st) * cover;
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float coverA, coverB;
            float4 a = Plane(prev, uv, prevH0, prevH1, prevH2, coverA) * blend.x;
            float4 b = Plane(next, uv, nextH0, nextH1, nextH2, coverB) * blend.y;
            a.rgb *= look.x;
            b.rgb *= look.y;
            return a * (1 - blend.w * coverB * blend.y) + b * (1 - blend.z * coverA * blend.x);
        }
        """;

    /// <summary>
    /// Iris, Diamond, Clock, Soft Wipe and Luma Wipe. Every pixel gets a "reveal time" (0 first, 1
    /// last) from one of five patterns, and shows the new picture once the edge has passed it. The
    /// luma pattern is the old picture's brightness laid over mid-grey: on a slide layer, clear round
    /// its words, the bright old words burn away first and the new ones arrive half-way through.
    /// c0 = (edge position, edge width, aspect); c1 = how much each pattern counts (circle, diamond,
    /// straight, brightness); c2 = (direction x, y, 1 / farthest circle, 1 / farthest diamond);
    /// c3 = (clock hand, its turn).
    /// </summary>
    private const string Wipe = Header + """
        float4 edge : register(c0);
        float4 weights : register(c1);
        float4 shape : register(c2);
        float4 clock : register(c3);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 a = tex2D(prev, uv);
            float4 b = tex2D(next, uv);
            float2 p = (uv - 0.5) * float2(edge.z, 1);
            float circle = length(p) * shape.z;
            float diamond = (abs(p.x) + abs(p.y)) * shape.w;
            float straight = 0.5 + dot(uv - 0.5, shape.xy);
            float luma = 1 - (dot(a.rgb, float3(0.299, 0.587, 0.114)) + 0.5 - 0.5 * a.a);
            float hand = frac(atan2(p.x * clock.y, -p.y) / 6.2831853 + 1);
            float v = dot(weights, float4(circle, diamond, straight, luma)) + clock.x * hand;
            float m = 1 - smoothstep(edge.x, edge.x + edge.y, v);
            return lerp(a, b, m);
        }
        """;

    /// <summary>
    /// Zoom Through and Cross Zoom: each picture scaled about a centre and streaked towards it,
    /// eight samples along the streak. c0 = (centre x, y, old scale, new scale); c1 = (old opacity,
    /// new opacity, flash); c2–c3 and c4–c5 = where the old and new pictures' samples sit along their
    /// streak (1 = the pixel itself); c6 = (the noise pattern's step per pixel across and down, the
    /// gap between samples). The samples only move towards the centre, so if the pixel is on the
    /// picture they all are: one mask does for eight. Each pixel slides its samples by its own
    /// share of the gap (a fixed noise pattern), so the streak looks smooth instead of like eight copies.
    /// </summary>
    private const string Radial = Header + """
        float4 zoom : register(c0);
        float4 mixing : register(c1);
        float4 prevSteps0 : register(c2);
        float4 prevSteps1 : register(c3);
        float4 nextSteps0 : register(c4);
        float4 nextSteps1 : register(c5);
        float4 dither : register(c6);

        // On the picture (0–1 both ways) exactly when st − st² ≥ 0 both ways: one comparison.
        float Inside(float2 st) { float2 k = st - st * st; return step(0, min(k.x, k.y)); }

        float4 Streaked(sampler2D picture, float2 uv, float2 c, float scale, float4 s0, float4 s1, float j)
        {
            float2 d = (uv - c) / scale;
            float2 b = c - d * j;
            float4 sum = tex2D(picture, b + d * s0.x);
            sum += tex2D(picture, b + d * s0.y);
            sum += tex2D(picture, b + d * s0.z);
            sum += tex2D(picture, b + d * s0.w);
            sum += tex2D(picture, b + d * s1.x);
            sum += tex2D(picture, b + d * s1.y);
            sum += tex2D(picture, b + d * s1.z);
            sum += tex2D(picture, b + d * s1.w);
            return sum * (0.125 * Inside(c + d));
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float j = frac(52.9829189 * frac(dot(uv, dither.xy))) * dither.z;
            float4 a = Streaked(prev, uv, zoom.xy, zoom.z, prevSteps0, prevSteps1, j) * mixing.x;
            float4 b = Streaked(next, uv, zoom.xy, zoom.w, nextSteps0, nextSteps1, j) * mixing.y;
            float4 c = a + b;
            c.rgb *= 1 + mixing.z;
            return c;
        }
        """;

    /// <summary>
    /// Whip Pan: a push, streaked along its direction by its speed. c0 = (the axis it moves on, the
    /// noise pattern's step per pixel across and down); c1–c2 and c3–c4 = where the old and new
    /// pictures' eight samples are read, as distances along that axis; c5 = (the gap between
    /// samples), dithered as in the zooms. Four samples' masks are worked out at once.
    /// </summary>
    private const string Whip = Header + """
        float4 axis : register(c0);
        float4 prevA : register(c1);
        float4 prevB : register(c2);
        float4 nextA : register(c3);
        float4 nextB : register(c4);
        float4 gap : register(c5);

        float4 Four(sampler2D picture, float2 uv, float along, float4 o)
        {
            // On the picture (0–1 along the axis) exactly when t − t² ≥ 0: one comparison for four samples.
            float4 t = along + o;
            float4 m = step(0, t - t * t);
            float4 sum = tex2D(picture, uv + axis.xy * o.x) * m.x;
            sum += tex2D(picture, uv + axis.xy * o.y) * m.y;
            sum += tex2D(picture, uv + axis.xy * o.z) * m.z;
            sum += tex2D(picture, uv + axis.xy * o.w) * m.w;
            return sum;
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float j = frac(52.9829189 * frac(dot(uv, axis.zw))) * gap.x;
            float2 at = uv + axis.xy * j;
            float along = dot(at, axis.xy);
            float4 sum = Four(prev, at, along, prevA) + Four(prev, at, along, prevB);
            sum += Four(next, at, along, nextA) + Four(next, at, along, nextB);
            return sum * 0.125;
        }
        """;

    /// <summary>
    /// Light Leak: a cross-fade with a band of coloured light sweeping diagonally across, a softer
    /// glow drifting with it, and a little overall bloom. The light is added "screen" fashion (it
    /// brightens but never burns past white) and leaves the alpha alone, so over a transparent part of
    /// the layer it lights up what's beneath. When a layer beneath has the light already (the
    /// background is leaking too), this one lights only its own pixels, so it isn't added twice.
    /// c0 = (cross-fade, glow, aspect, band position); c1 = (colour, 1 = lit beneath); c2 = (band
    /// axis x, y, 1 / width², bloom); c3 = (glow centre x, y, 1 / radius², strength).
    /// </summary>
    private const string Light = Header + """
        float4 sweep : register(c0);
        float4 tint : register(c1);
        float4 band : register(c2);
        float4 blob : register(c3);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 c = lerp(tex2D(prev, uv), tex2D(next, uv), sweep.x);
            float2 p = float2(uv.x * sweep.z, uv.y);
            float along = dot(p, band.xy) - sweep.w;
            float2 q = p - blob.xy;
            float glow = sweep.y * (exp(-along * along * band.z) + blob.w * exp(-dot(q, q) * blob.z)) + band.w;
            glow *= 1 - tint.a * (1 - c.a);
            c.rgb += tint.rgb * glow * (1 - saturate(c.rgb));
            return c;
        }
        """;

    /// <summary>
    /// Glitch: rows jump sideways, the colour channels split, blocks switch to the new picture in a
    /// scattered order, and a few blocks drop out darker. The pattern comes from a cheap hash (no
    /// sine, to fit ps_2_0) seeded from the clock 24 times a second. c0 = (blocks switched, strength,
    /// 0, seed); c1 = (rows, columns, colour split, row shift).
    /// </summary>
    private const string Glitch = Header + """
        float4 timing : register(c0);
        float4 grid : register(c1);

        float Hash(float n)
        {
            n = frac(n * 0.1031);
            n *= n + 33.33;
            n *= n + n;
            return frac(n);
        }

        float4 Split(sampler2D picture, float2 st, float dx)
        {
            float4 c = tex2D(picture, st);
            c.r = tex2D(picture, st + float2(dx, 0)).r;
            c.b = tex2D(picture, st - float2(dx, 0)).b;
            return c;
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float row = floor(uv.y * grid.x);
            float h = Hash(row + timing.w);
            float shift = (h - 0.5) * grid.w * step(0.55, frac(h * 7.31));
            float2 st = float2(uv.x + shift, uv.y);
            float cell = floor(st.x * grid.y) + row * 7.13;
            float k = Hash(cell + timing.w * 0.37);
            float4 c = lerp(Split(prev, st, grid.z), Split(next, st, grid.z), step(k, timing.x));
            c.rgb *= 1 - timing.y * 0.35 * step(0.92, frac(k * 13.7));
            return c;
        }
        """;

    /// <summary>
    /// Ripple: rings of waves bend both pictures, and a soft ring spreading from the middle carries
    /// the new picture out to the edges. c0 = (ring, ring width, aspect, wave phase); c1 = (wave
    /// height, waves per screen height).
    /// </summary>
    private const string Ripple = Header + """
        float4 ring : register(c0);
        float4 wave : register(c1);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float2 p = (uv - 0.5) * float2(ring.z, 1);
            float d = length(p);
            float2 dir = p / max(d, 0.001);
            float2 off = dir * (sin(d * wave.y - ring.w) * wave.x) / float2(ring.z, 1);
            float4 a = tex2D(prev, uv + off);
            float4 b = tex2D(next, uv + off);
            return lerp(a, b, 1 - smoothstep(ring.x - ring.y, ring.x, d));
        }
        """;

    /// <summary>The shader target: ps_2_0, which WPF can also run in software.</summary>
    public const string Target = "ps_2_0";

    public static string Source(PictureShader shader) => shader switch
    {
        PictureShader.Wipe => Wipe,
        PictureShader.Radial => Radial,
        PictureShader.Whip => Whip,
        PictureShader.Light => Light,
        PictureShader.Glitch => Glitch,
        PictureShader.Ripple => Ripple,
        _ => Planes,
    };

    public static string Name(PictureShader shader) => $"Transition{shader}";

    private static bool? supported;

    /// <summary>True when this PC can run ps_2_0 at all, on the graphics card or in software (asked on the UI thread).</summary>
    private static bool PixelShadersSupported => supported ??=
        RenderCapability.IsPixelShaderVersionSupported(2, 0) || RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0);

    /// <summary>The compiled shader for a family, or null when this PC can't run or compile it.</summary>
    public static PixelShader? Shader(PictureShader shader) =>
        PixelShadersSupported ? ShaderCompiler.Compile(Name(shader), Source(shader), target: Target) : null;

    /// <summary>True when this view can draw <paramref name="kind"/> with its shader; otherwise it plays the nearest of the Mac's kinds.</summary>
    public static bool CanDraw(WindowsTransitionKind kind) =>
        WindowsTransitions.IsShared(kind) || Shader(PictureTransitions.ShaderFor(kind)) is not null;

    private static int warmed;

    /// <summary>
    /// Compiles every transition shader, and runs the transition maths once for every kind, on a
    /// thread of its own when the app starts, so the first push of the service doesn't stall a frame
    /// while that happens. Not show timing, so not a <see cref="Core.Threading.WorkQueue"/>, and not
    /// the thread pool either (an import can tie that up for minutes).
    /// </summary>
    public static void WarmUp()
    {
        if (Interlocked.Exchange(ref warmed, 1) == 1) return;
        var thread = new Thread(() =>
        {
            try
            {
                var started = Environment.TickCount64;
                foreach (var shader in Enum.GetValues<PictureShader>()) ShaderCompiler.Compile(Name(shader), Source(shader), target: Target);
                foreach (var kind in WindowsTransitions.WindowsOnly)
                    foreach (var t in (double[])[0, 0.37, 0.5, 1])
                        PictureTransitions.Mix(new WindowsTransition { Kind = kind }, t, 16.0 / 9, t, (1920, 1080));
                Log.Info($"Transition shaders ready in {Environment.TickCount64 - started} ms");
            }
            catch (Exception ex)
            {
                Log.Error("Warming up the transition shaders failed; they'll compile when first used", ex);
            }
        })
        { IsBackground = true, Name = "Transition warm-up", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }
}
