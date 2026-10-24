using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Control;

/// <summary>
/// What a recording or stream hears: where its sound comes from (the program, a mix or a
/// sound input), whether the program is added on top, and how long the sound is held back so
/// it lines up with the picture. Read from a preset with <see cref="Of"/>. (The Mac passes
/// these to SessionAudioFeed.open as separate arguments.)
/// </summary>
/// <param name="InputUid">The old way presets named an input: a sound device's id rather than an input entry.</param>
/// <param name="DelayMs">0 to 2000 milliseconds.</param>
public sealed record SessionAudioRequest(StreamAudioSource Source, string? InputUid = null, bool IncludesProgram = false, int DelayMs = 0)
{
    public const int MaxDelayMs = 2000;

    public static SessionAudioRequest Program { get; } = new(StreamAudioSource.Program);

    public static SessionAudioRequest Of(StreamRecordPreset preset) => new(
        StreamAudioSource.Of(preset),
        string.IsNullOrEmpty(preset.AudioInputUid) ? null : preset.AudioInputUid,
        preset.AudioIncludesProgram ?? false,
        Math.Clamp(preset.AudioDelayMs ?? 0, 0, MaxDelayMs));

    /// <summary>Program sound with no delay: exactly what the program feed carries anyway.</summary>
    public bool IsPlainProgram => Source.Kind == StreamAudioSourceKind.Program && InputUid is null && DelayMs == 0;

    /// <summary>For the log: "program", "mix:main+program", "input:abc", "program +120ms".</summary>
    public override string ToString()
    {
        var text = Source.Kind switch
        {
            StreamAudioSourceKind.Mix => $"mix:{Source.Id}",
            StreamAudioSourceKind.Input => $"input:{Source.Id}",
            _ => InputUid is not null ? "input-device" : "program",
        };
        if (IncludesProgram && text != "program") text += "+program";
        if (DelayMs > 0) text += $" +{DelayMs}ms";
        return text;
    }
}

/// <summary>Opens a recording's or stream's sound into <paramref name="sink"/> (its WriteAudio); disposing the result stops it.</summary>
public delegate IDisposable SessionAudioOpener(SessionAudioRequest request, IProgramSink sink);

/// <summary>Pictures of one screen (by its id, or "program") at a size and frame rate, or null when the app can't make them.</summary>
public delegate IProgramSource? ScreenPictures(string targetId, int width, int height, int frameRate);

/// <summary>
/// Where recordings and streams get their pictures and sound. The app fills this in:
/// <list type="bullet">
/// <item><see cref="Program"/>: the audience program feed (30 frames a second).</item>
/// <item><see cref="Screens"/>: a picture of any one screen (the stage display, a lower-thirds
/// screen…), or the program at more than 30 frames a second.</item>
/// <item><see cref="Audio"/>: the sound a preset asks for (a mix, a sound input, the program,
/// with or without a delay).</item>
/// </list>
/// Without <see cref="Screens"/> every picture is the program; without <see cref="Audio"/> the
/// sound is whatever the program source carries. Tests use just a program. The Mac renders a
/// separate OutputMirror for each screen a preset names; this is the Windows equivalent.
/// </summary>
public sealed class CaptureSources(IProgramSource program, int programFrameRate = 30)
{
    /// <summary>The target id meaning "the audience program" (also what an empty canvas id means).</summary>
    public const string ProgramTarget = "program";

    public IProgramSource Program { get; } = program;

    /// <summary>The program feed's own frame rate; asking for more goes to <see cref="Screens"/>.</summary>
    public int ProgramFrameRate { get; } = programFrameRate;

    public ScreenPictures? Screens { get; init; }

    public SessionAudioOpener? Audio { get; init; }

    public static string TargetFor(string? canvasScreenId) => string.IsNullOrEmpty(canvasScreenId) ? ProgramTarget : canvasScreenId;

    /// <summary>
    /// Attaches <paramref name="sink"/> to the pictures of <paramref name="targetId"/> (the
    /// program for null or "program"). Only pictures reach the sink: its sound comes from
    /// <see cref="OpenAudio"/>. When the screen can't be pictured, the program is used and the
    /// log says so. Returns the handle that detaches it.
    /// </summary>
    public IDisposable AttachPictures(string? targetId, int width, int height, int frameRate, IProgramSink sink) =>
        PicturesFor(TargetFor(targetId), width, height, frameRate).Attach(new VideoOnly(sink));

    /// <summary>Which source a target's pictures come from (internal so tests can check the choice).</summary>
    internal IProgramSource PicturesFor(string target, int width, int height, int frameRate)
    {
        if (target == ProgramTarget && frameRate <= ProgramFrameRate) return Program;
        try
        {
            if (Screens?.Invoke(target, width, height, frameRate) is { } screen) return screen;
        }
        catch (Exception e)
        {
            BroadcastLog.Error("capture.screen", $"couldn't picture {target}: {e.Message}");
        }
        if (target != ProgramTarget)
            BroadcastLog.Warning("capture.screen", $"screen {target} isn't available — using the audience program");
        return Program;
    }

    /// <summary>
    /// Opens the sound a session asked for, into <paramref name="sink"/>'s WriteAudio. Never
    /// throws: if the app's opener fails, the program's own sound is used instead.
    /// </summary>
    public IDisposable OpenAudio(SessionAudioRequest request, IProgramSink sink)
    {
        if (Audio is { } open)
        {
            try
            {
                return open(request, sink);
            }
            catch (Exception e)
            {
                BroadcastLog.Error("capture.audio", $"{request}: couldn't open the sound ({e.Message}) — using program audio");
            }
        }
        else if (!request.IsPlainProgram)
        {
            BroadcastLog.Info("capture.audio", $"{request}: mixes, inputs and delay need the app — using program audio");
        }
        return Program.Attach(new AudioOnly(sink));
    }

    /// <summary>Passes pictures on and drops sound (a session's sound arrives separately).</summary>
    internal sealed class VideoOnly(IProgramSink inner) : IProgramSink
    {
        public IProgramSink Inner { get; } = inner;

        public void WriteVideo(VideoFrame frame) => Inner.WriteVideo(frame);

        public void WriteAudio(AudioBuffer buffer) { }
    }

    /// <summary>Passes sound on and drops pictures: the program's own sound for a session.</summary>
    internal sealed class AudioOnly(IProgramSink inner) : IProgramSink
    {
        public IProgramSink Inner { get; } = inner;

        public void WriteVideo(VideoFrame frame) { }

        public void WriteAudio(AudioBuffer buffer) => Inner.WriteAudio(buffer);
    }
}

/// <summary>Hands one sound to several sinks (every destination of a stream hears the same thing). Each sink copies what it keeps.</summary>
internal sealed class AudioFanout(IReadOnlyList<IProgramSink> sinks) : IProgramSink
{
    public void WriteVideo(VideoFrame frame) { }

    public void WriteAudio(AudioBuffer buffer)
    {
        foreach (var sink in sinks)
        {
            try { sink.WriteAudio(buffer); }
            catch (Exception e) { BroadcastLog.Error("capture.audio", $"a destination failed to take sound: {e.Message}"); }
        }
    }
}
