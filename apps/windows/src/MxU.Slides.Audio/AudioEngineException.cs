namespace MxU.Slides.Audio;

/// <summary>
/// A track the engine can't play: the file is missing or undecodable, or the trim
/// leaves nothing to play. Never thrown for a missing audio device (that is a status,
/// see <see cref="AudioOutputStatus"/>). Port of AudioEngineError.
/// </summary>
public sealed class AudioEngineException : Exception
{
    public enum Kind
    {
        /// <summary>The file couldn't be opened or decoded.</summary>
        Unreadable,

        /// <summary>The trim window (or the file) has no audio in it.</summary>
        EmptySchedule,
    }

    private AudioEngineException(Kind reason, string path, string message) : base(message)
    {
        Reason = reason;
        FilePath = path;
    }

    public Kind Reason { get; }

    public string FilePath { get; }

    public static AudioEngineException Unreadable(string path, string detail) =>
        new(Kind.Unreadable, path, $"Couldn't read {Path.GetFileName(path)}: {detail}");

    public static AudioEngineException EmptySchedule(string path) =>
        new(Kind.EmptySchedule, path, $"Nothing to play in {Path.GetFileName(path)} (check its in and out points).");
}
