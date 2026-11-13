using System.Runtime.InteropServices;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Openers;
using MxU.Slides.MotionTemplates.Lottie;
using SkiaSharp;

namespace MxU.Slides.MotionTemplates.Openers;

/// <summary>
/// Draws the frames of an opener (Phase 13). <see cref="OpenerPlan"/> says what is on screen at a
/// moment (an <see cref="OpenerFrame"/>: a description, no pixels); this turns one into a picture:
/// <list type="number">
/// <item><b>The motion background</b>, by Phase 6's <see cref="MotionRenderer"/>. For a full-size
/// video it's drawn at half size and enlarged: the backgrounds are soft by design, so nothing is
/// lost, and it's a quarter of the work (which matters for a ten-minute countdown).</item>
/// <item><b>A montage's pictures and clips</b>, each filling the screen with its slow move. One
/// whose shape doesn't fit (a portrait photo) is shown whole over a blurred copy of itself.</item>
/// <item><b>The words</b>, in the look's fonts, with a soft shadow so they read over anything.
/// Each line is drawn once into a small picture of its own (a "sprite") and then just placed,
/// faded, moved and scaled frame by frame, which is far cheaper than drawing text and its
/// blurred shadow thirty times a second.</item>
/// <item><b>Black</b> on top, for the fades at each end.</item>
/// </list>
/// The drawing is Skia's (the library that draws the motion templates, already in this project).
/// The same class draws the window's small live preview and the final video, so what you see is
/// what you get. Use from one thread.
/// </summary>
public sealed class OpenerRenderer : IDisposable
{
    private const int KeptSprites = 96;

    private readonly OpenerPlan plan;
    private readonly OpenerLook look;
    private readonly ILottieFonts? fonts;
    private readonly OpenerPictures pictures;
    private readonly bool ownsPictures;
    private readonly MotionRenderer background;
    private readonly byte[] backgroundPixels;
    private GCHandle backgroundPin;
    private readonly SKBitmap backgroundBitmap;
    private readonly SKTypeface titleFace;
    private readonly SKTypeface textFace;
    private readonly SKColor accent;
    private readonly Dictionary<string, (Sprite Sprite, long Used)> sprites = [];
    private readonly SKBitmap clipBitmap = new();
    private long uses;

    /// <param name="pictures">A picture store to share (the preview keeps one across changes to the words); null to have its own.</param>
    public OpenerRenderer(OpenerPlan plan, int width, int height, RenderCrew crew, ILottieFonts? fonts = null, OpenerPictures? pictures = null)
    {
        this.plan = plan;
        look = plan.Look;
        this.fonts = fonts;
        Width = Math.Max(2, width & ~1);
        Height = Math.Max(2, height & ~1);
        this.pictures = pictures ?? new OpenerPictures(Width, Height);
        ownsPictures = pictures is null;

        // Half size for a big picture (see the class summary); a small preview is drawn as it is.
        var divide = Height >= 720 ? 2 : 1;
        background = new MotionRenderer(plan.Background, Width / divide, Height / divide, crew);
        backgroundPixels = new byte[background.FrameBytes(MotionPixelFormat.Bgra32)];
        backgroundPin = GCHandle.Alloc(backgroundPixels, GCHandleType.Pinned);
        backgroundBitmap = new SKBitmap();
        backgroundBitmap.InstallPixels(new SKImageInfo(background.Width, background.Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
            backgroundPin.AddrOfPinnedObject(), background.Width * 4);

        titleFace = Face(look.TitleFont);
        textFace = Face(look.TextFont);
        // The palette's highlight (its last colour) is the accent: it always goes with the background.
        accent = SKColor.TryParse(plan.Background.Colors[^1], out var highlight) ? highlight : SKColors.White;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Draws frame <paramref name="index"/> of the video into <paramref name="target"/> (which must be this renderer's size).</summary>
    public void Render(long index, LottieFrame target) => Render(plan.Frame(index), index, target);

    /// <summary>
    /// Draws <paramref name="frame"/> into <paramref name="target"/>. <paramref name="grain"/> picks the
    /// background's film grain (the frame's number, so a loop's grain repeats with it).
    /// </summary>
    public void Render(OpenerFrame frame, long grain, LottieFrame target)
    {
        if (target.Width != Width || target.Height != Height) throw new ArgumentException("The picture isn't this renderer's size", nameof(target));
        var canvas = target.Canvas;
        canvas.ResetMatrix();
        if (frame.Black >= 1)
        {
            canvas.Clear(SKColors.Black);
            return;
        }
        if (frame.ShowBackground)
        {
            background.Render(frame.BackgroundPhase, ((grain % plan.FrameCount) + plan.FrameCount) % plan.FrameCount, MotionPixelFormat.Bgra32, backgroundPixels);
            backgroundBitmap.NotifyPixelsChanged();
            using var image = SKImage.FromBitmap(backgroundBitmap);
            canvas.DrawImage(image, new SKRect(0, 0, Width, Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else
        {
            canvas.Clear(SKColors.Black);
        }

        foreach (var shot in frame.Shots) DrawShot(canvas, shot);
        pictures.KeepOnlyClips(frame.Shots.Select(s => s.Path));
        foreach (var card in frame.Cards) DrawCard(canvas, card);

        if (frame.Black > 0)
        {
            using var black = new SKPaint { Color = SKColors.Black.WithAlpha(Alpha(frame.Black)) };
            canvas.DrawRect(0, 0, Width, Height, black);
        }
        canvas.Flush();
    }

    // MARK: A montage's pictures

    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

    private void DrawShot(SKCanvas canvas, OpenerShot shot)
    {
        if (shot.Opacity <= 0) return;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(shot.Opacity)) };
        if (OpenerPictures.IsVideo(shot.Path))
        {
            if (pictures.ClipFrame(shot.Path, shot.ClipSeconds, out var w, out var h) is not { } pixels) return;
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                clipBitmap.InstallPixels(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), handle.AddrOfPinnedObject(), w * 4);
                using var frame = SKImage.FromBitmap(clipBitmap);
                Fill(canvas, frame, null, shot, paint);
            }
            finally
            {
                handle.Free();
            }
            return;
        }
        if (pictures.Get(shot.Path) is { } picture) Fill(canvas, picture.Image, picture.Blurred, shot, paint);
    }

    /// <summary>
    /// Fills the screen with a picture. When its shape is close to the screen's it's cropped to
    /// fit ("cover"); when that would cut away more than a third of it (a portrait photo), it's
    /// shown whole over <paramref name="blurred"/>, a blurred copy that fills the screen instead
    /// of black bars.
    /// </summary>
    private void Fill(SKCanvas canvas, SKImage image, SKImage? blurred, OpenerShot shot, SKPaint paint)
    {
        var cover = Math.Max((double)Width / image.Width, (double)Height / image.Height);
        var contain = Math.Min((double)Width / image.Width, (double)Height / image.Height);
        var whole = contain / cover < 0.67;
        if (whole)
        {
            using var behind = new SKPaint { Color = SKColors.Black.WithAlpha(paint.Color.Alpha) };
            canvas.DrawRect(0, 0, Width, Height, behind);
            if (blurred is not null)
            {
                var spread = Math.Max((double)Width / blurred.Width, (double)Height / blurred.Height) * 1.05;
                // Dimmed, so the picture itself stands out from its own backdrop.
                using var dim = new SKPaint { Color = SKColors.White.WithAlpha(paint.Color.Alpha), ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(140, 140, 140), SKBlendMode.Modulate) };
                canvas.DrawImage(blurred, Centred(blurred.Width * spread, blurred.Height * spread, 0, 0), new SKSamplingOptions(SKCubicResampler.Mitchell), dim);
            }
        }
        var scale = (whole ? contain : cover) * shot.Zoom;
        var (w, h) = (image.Width * scale, image.Height * scale);
        // The pan moves the view within the room the zoom leaves spare (none, for a picture shown whole).
        var panX = whole ? 0 : shot.PanX * Math.Max(0, w - Width) / 2;
        var panY = whole ? 0 : shot.PanY * Math.Max(0, h - Height) / 2;
        canvas.DrawImage(image, Centred(w, h, -panX, -panY), Smooth, paint);
    }

    private SKRect Centred(double w, double h, double dx, double dy)
    {
        var left = (float)((Width - w) / 2 + dx);
        var top = (float)((Height - h) / 2 + dy);
        return new SKRect(left, top, (float)(left + w), (float)(top + h));
    }

    // MARK: Words

    /// <summary>A line of words drawn once, shadow and all, to be placed many times. <see cref="Pad"/> is the room left round it for the shadow.</summary>
    private sealed record Sprite(SKImage Image, float Width, float Height, float Pad) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    private void DrawCard(SKCanvas canvas, OpenerCard card)
    {
        float h = Height, w = Width;
        var margin = 0.08f * w;
        var besidePicture = card is { Picture: not null, PictureBeside: true } ? pictures.Get(card.Picture) : null;
        var abovePicture = card is { Picture: not null, PictureBeside: false } ? pictures.Get(card.Picture) : null;
        // The words' column: the whole width, or the left half when a picture has the right.
        var column = besidePicture is null ? new SKRect(margin, 0, w - margin, h) : new SKRect(margin, 0, 0.5f * w, h);
        var bar = look.Left ? 0.034f * h : 0;
        var wrapWidth = column.Width - bar;

        // Lay the blocks out top to bottom, then centre the stack.
        var blocks = new List<(Sprite? Sprite, OpenerLine? Line, float Height, float GapAfter, bool Rule)>();
        if (abovePicture is not null) blocks.Add((null, null, 0.26f * h, 0.045f * h, false));
        for (var i = 0; i < card.Lines.Count; i++)
        {
            var line = card.Lines[i];
            var sprite = SpriteFor(line.Role, line.Text, wrapWidth);
            var next = i + 1 < card.Lines.Count ? card.Lines[i + 1].Role : (OpenerRole?)null;
            var gap = next is null ? 0 : (line.Role, next) switch
            {
                (OpenerRole.Kicker, _) => 0.034f * h,
                (OpenerRole.Digits, _) => 0.045f * h,
                (OpenerRole.Title, OpenerRole.Body) => 0.05f * h,
                _ => 0.034f * h,
            };
            var rule = look.Rule && line.Role == OpenerRole.Title && next is not null;
            blocks.Add((sprite, line, sprite.Height, rule ? gap * 2f : gap, rule));
        }
        var total = blocks.Sum(b => b.Height + b.GapAfter);
        var y = (h - total) / 2;
        var top = y;

        foreach (var (sprite, line, blockHeight, gapAfter, rule) in blocks)
        {
            if (sprite is null || line is null)
            {
                DrawContained(canvas, abovePicture!, new SKRect(column.MidX - 0.3f * w, y, column.MidX + 0.3f * w, y + blockHeight), card.PictureOpacity);
            }
            else if (line.Opacity > 0)
            {
                var x = look.Left ? column.Left + bar : column.MidX - sprite.Width / 2;
                var centre = new SKPoint(x + sprite.Width / 2 + (float)(line.Slide * h), y + blockHeight / 2 + (float)(line.Rise * h));
                var (sw, sh) = ((sprite.Width + 2 * sprite.Pad) * (float)line.Scale, (sprite.Height + 2 * sprite.Pad) * (float)line.Scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(line.Opacity)) };
                canvas.DrawImage(sprite.Image, new SKRect(centre.X - sw / 2, centre.Y - sh / 2, centre.X + sw / 2, centre.Y + sh / 2), Smooth, paint);
            }
            if (rule && card.Accent > 0)
            {
                // A short thin rule under the title, drawing outward from its middle.
                var half = 0.05f * h * (float)card.Accent;
                var ruleY = y + blockHeight + gapAfter / 2;
                using var paint = new SKPaint { Color = accent.WithAlpha(Alpha(card.Accent)), IsAntialias = true };
                canvas.DrawRect(new SKRect(column.MidX - half, ruleY - 0.002f * h, column.MidX + half, ruleY + 0.002f * h), paint);
            }
            y += blockHeight + gapAfter;
        }

        if (look.Left && card.Accent > 0 && total > 0)
        {
            // The accent bar beside the words, growing downward.
            using var paint = new SKPaint { Color = accent.WithAlpha(Alpha(card.Accent)), IsAntialias = true };
            var inset = 0.012f * h;
            canvas.DrawRect(new SKRect(column.Left, top + inset, column.Left + 0.009f * h, top + inset + (total - 2 * inset) * (float)card.Accent), paint);
        }

        if (besidePicture is not null && card.PictureOpacity > 0)
        {
            var panel = new SKRect(0.56f * w, 0.17f * h, w - margin, 0.83f * h);
            if (besidePicture.HasAlpha)
            {
                DrawContained(canvas, besidePicture, panel, card.PictureOpacity);
            }
            else
            {
                // A photo: cropped to fill a panel with rounded corners.
                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(panel, 0.022f * h), antialias: true);
                var image = besidePicture.Image;
                var scale = Math.Max(panel.Width / image.Width, panel.Height / image.Height);
                var (pw, ph) = (image.Width * scale, image.Height * scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(card.PictureOpacity)) };
                canvas.DrawImage(image, new SKRect(panel.MidX - pw / 2, panel.MidY - ph / 2, panel.MidX + pw / 2, panel.MidY + ph / 2), Smooth, paint);
                canvas.Restore();
            }
        }
    }

    /// <summary>A picture shown whole inside <paramref name="box"/> (a logo is never cropped).</summary>
    private static void DrawContained(SKCanvas canvas, OpenerPictures.Picture picture, SKRect box, double opacity)
    {
        if (opacity <= 0) return;
        var image = picture.Image;
        var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var (w, h) = (image.Width * scale, image.Height * scale);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
        canvas.DrawImage(image, new SKRect(box.MidX - w / 2, box.MidY - h / 2, box.MidX + w / 2, box.MidY + h / 2), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    /// <summary>The font, size, colour and spacing a role gets in this look. Sizes are fractions of the picture's height.</summary>
    private (SKTypeface Face, float Size, SKColor Colour, float Tracking, bool Caps, float Leading) StyleOf(OpenerRole role)
    {
        float h = Height;
        var soft = SKColors.White.WithAlpha(225);
        return role switch
        {
            OpenerRole.Title => (titleFace, (float)look.TitleSize * h, SKColors.White, (float)look.TitleTracking, look.Caps, look.Caps ? 1.08f : 1.16f),
            OpenerRole.Kicker => (textFace, 0.034f * h, accent, 0.22f, true, 1.3f),
            OpenerRole.Subtitle => (textFace, 0.044f * h, soft, look.Id == "minimal" ? 0.08f : 0.005f, false, 1.3f),
            OpenerRole.Body => (textFace, 0.042f * h, soft, 0.005f, false, 1.42f),
            // The clock, in the title's font. Bebas Neue is narrow, so it can stand taller.
            _ => (titleFace, (look.Id == "bold" ? 0.40f : 0.27f) * h, SKColors.White, 0, false, 1f),
        };
    }

    private Sprite SpriteFor(OpenerRole role, string text, float wrapWidth)
    {
        var key = $"{(int)role}|{wrapWidth:0}|{text}";
        if (sprites.TryGetValue(key, out var held))
        {
            sprites[key] = (held.Sprite, ++uses);
            return held.Sprite;
        }
        if (sprites.Count >= KeptSprites)
        {
            // The clock makes a new sprite every second; let the ones longest unused go.
            foreach (var old in sprites.OrderBy(s => s.Value.Used).Take(KeptSprites / 4).ToList())
            {
                old.Value.Sprite.Dispose();
                sprites.Remove(old.Key);
            }
        }
        var sprite = MakeSprite(role, text, wrapWidth);
        sprites[key] = (sprite, ++uses);
        return sprite;
    }

    private Sprite MakeSprite(OpenerRole role, string text, float wrapWidth)
    {
        var (face, size, colour, tracking, caps, leading) = StyleOf(role);
        if (caps) text = text.ToUpperInvariant();
        // A character the look's font hasn't got (another alphabet, an emoji): the whole line goes in a Windows font that has it.
        using (var probe = new SKFont(face, size))
        {
            if (!probe.ContainsGlyphs(text.Replace("\n", "").Replace("\r", "")))
            {
                var missing = text.EnumerateRunes().FirstOrDefault(r => !System.Text.Rune.IsControl(r) && !System.Text.Rune.IsWhiteSpace(r) && !probe.ContainsGlyph(r.Value));
                face = SKFontManager.Default.MatchCharacter(missing.Value) ?? face;
            }
        }

        // A title too long for three lines is set smaller, rather than running off the screen.
        var maxLines = role switch { OpenerRole.Title => 3, OpenerRole.Body => 7, OpenerRole.Digits => 1, _ => 2 };
        SKFont font;
        List<(string Text, float Width)> lines;
        var shrink = 1f;
        while (true)
        {
            font = new SKFont(face, size * shrink) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
            lines = role == OpenerRole.Digits ? [(text, DigitsWidth(font, text))] : Wrap(font, text, wrapWidth, tracking);
            if ((lines.Count <= maxLines && lines.All(l => l.Width <= wrapWidth + 1)) || shrink < 0.45f) break;
            font.Dispose();
            shrink *= 0.88f;
        }

        using (font)
        {
            var lineHeight = font.Size * leading;
            var metrics = font.Metrics;
            var width = Math.Max(1, lines.Max(l => l.Width));
            // The block's height is from the top of the capitals of the first line to the baseline of the
            // last (plus a little for descenders), so lines of different sizes stack by what the eye sees.
            // Measured from a real letter, not read from the font's own table, which some fonts leave empty or wrong.
            font.MeasureText(role == OpenerRole.Digits ? "0" : "H", out var capital);
            var capHeight = capital.Height > 0 ? -capital.Top : -metrics.Ascent * 0.72f;
            var height = capHeight + lineHeight * (lines.Count - 1) + (role is OpenerRole.Digits ? 0 : metrics.Descent * 0.5f);
            var blur = 0.011f * Height;
            var pad = (float)Math.Ceiling(blur * 3 + 0.02f * Height);
            using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(width + 2 * pad), (int)Math.Ceiling(height + 2 * pad), SKColorType.Bgra8888, SKAlphaType.Premul))
                                ?? throw new InvalidOperationException("Skia can't make a picture for a line of words");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint
            {
                Color = colour, IsAntialias = true,
                ImageFilter = SKImageFilter.CreateDropShadow(0, 0.004f * Height, blur, blur, SKColors.Black.WithAlpha(150)),
            };
            for (var i = 0; i < lines.Count; i++)
            {
                var x = pad + (look.Left ? 0 : (width - lines[i].Width) / 2);
                var baseline = pad + capHeight + lineHeight * i;
                if (role == OpenerRole.Digits) DrawDigits(canvas, font, lines[i].Text, x, baseline, paint);
                else DrawTracked(canvas, font, lines[i].Text, x, baseline, tracking, paint);
            }
            canvas.Flush();
            return new Sprite(surface.Snapshot(), width, height, pad);
        }
    }

    /// <summary>Breaks text into lines no wider than <paramref name="maxWidth"/>, at spaces. A line break typed in is kept.</summary>
    private static List<(string Text, float Width)> Wrap(SKFont font, string text, float maxWidth, float tracking)
    {
        var lines = new List<(string, float)>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var trial = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && Measure(font, trial, tracking) > maxWidth)
                {
                    lines.Add((current, Measure(font, current, tracking)));
                    current = word;
                }
                else
                {
                    current = trial;
                }
            }
            if (current.Length > 0) lines.Add((current, Measure(font, current, tracking)));
        }
        if (lines.Count == 0) lines.Add(("", 1));
        return lines;
    }

    /// <summary>A line's width with <paramref name="tracking"/> ems added between its letters (not after the last).</summary>
    private static float Measure(SKFont font, string text, float tracking) =>
        text.Length == 0 ? 0 : font.MeasureText(text) + tracking * font.Size * Math.Max(0, font.CountGlyphs(text) - 1);

    /// <summary>Draws a line with extra (or less) space between its letters: each glyph is placed by hand, its own width plus the tracking along from the last.</summary>
    private static void DrawTracked(SKCanvas canvas, SKFont font, string text, float x, float baseline, float tracking, SKPaint paint)
    {
        if (text.Length == 0) return;
        var widths = font.GetGlyphWidths(text);
        if (tracking == 0 || widths.Length != font.CountGlyphs(text))
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
            return;
        }
        var positions = new SKPoint[widths.Length];
        var at = 0f;
        for (var i = 0; i < widths.Length; i++)
        {
            positions[i] = new SKPoint(at, 0);
            at += widths[i] + tracking * font.Size;
        }
        using var blob = SKTextBlob.CreatePositioned(text, font, positions);
        if (blob is not null) canvas.DrawText(blob, x, baseline, paint);
    }

    /// <summary>
    /// The clock's figures each sit in a cell as wide as the widest figure, so "1:11" is as wide as
    /// "0:00" and the clock doesn't shuffle sideways as it counts.
    /// </summary>
    private static float DigitCell(SKFont font) => "0123456789".Max(c => font.MeasureText(c.ToString())) * 1.04f;

    private static float DigitsWidth(SKFont font, string text)
    {
        var cell = DigitCell(font);
        return text.Sum(c => char.IsAsciiDigit(c) ? cell : font.MeasureText(c.ToString()) * 1.3f);
    }

    private static void DrawDigits(SKCanvas canvas, SKFont font, string text, float x, float baseline, SKPaint paint)
    {
        var cell = DigitCell(font);
        foreach (var c in text)
        {
            var glyph = c.ToString();
            var own = font.MeasureText(glyph);
            var room = char.IsAsciiDigit(c) ? cell : own * 1.3f;
            canvas.DrawText(glyph, x + (room - own) / 2, baseline, SKTextAlign.Left, font, paint);
            x += room;
        }
    }

    /// <summary>A font the app ships, from the library's fonts folder; failing that the installed one, or Windows' default.</summary>
    private SKTypeface Face(OpenerFont font)
    {
        if (fonts?.Find(new LottieFont(font.PostScriptName, font.Family, font.Style, null)) is { } bytes)
        {
            using var data = SKData.CreateCopy(bytes);
            if (SKTypeface.FromData(data) is { } shipped) return shipped;
        }
        return SKTypeface.FromFamilyName(font.Family) ?? SKTypeface.Default;
    }

    private static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

    public void Dispose()
    {
        foreach (var held in sprites.Values) held.Sprite.Dispose();
        sprites.Clear();
        clipBitmap.Dispose();
        backgroundBitmap.Dispose();
        if (backgroundPin.IsAllocated) backgroundPin.Free();
        if (ownsPictures) pictures.Dispose();
        if (!ReferenceEquals(titleFace, SKTypeface.Default)) titleFace.Dispose();
        if (!ReferenceEquals(textFace, SKTypeface.Default)) textFace.Dispose();
    }
}
