namespace MxU.Slides.Audio;

/// <summary>
/// Marks code running on an audio thread (a render or capture callback). Those threads
/// must never wait on a lock the UI might hold, or a busy UI glitches the sound; with
/// this marker a debug build catches the mistake. Port of RealtimeAudioCallback.
/// </summary>
public static class RealtimeAudioCallback
{
    [ThreadStatic] private static int depth;

    public static bool IsActive => depth > 0;

    public static void Scope(Action body)
    {
        depth++;
        try { body(); }
        finally { depth--; }
    }

    public static T Scope<T>(Func<T> body)
    {
        depth++;
        try { return body(); }
        finally { depth--; }
    }
}

/// <summary>
/// The lock around an engine's settings and transport. Control code takes it with
/// <see cref="WithLock(Action)"/>; an audio thread may only <see cref="TryWithLock(Action)"/>
/// (and drop the buffer when it is busy). Port of EngineStateLock.
/// </summary>
public sealed class EngineStateLock
{
    private readonly Lock inner = new();

    public void WithLock(Action body)
    {
        AssertNotRealtime();
        lock (inner) body();
    }

    public T WithLock<T>(Func<T> body)
    {
        AssertNotRealtime();
        lock (inner) return body();
    }

    /// <summary>Runs <paramref name="body"/> only if the lock is free right now.</summary>
    public bool TryWithLock(Action body)
    {
        if (!inner.TryEnter()) return false;
        try { body(); }
        finally { inner.Exit(); }
        return true;
    }

    /// <summary>Runs <paramref name="body"/> only if the lock is free right now.</summary>
    public bool TryWithLock<T>(Func<T> body, out T result)
    {
        if (!inner.TryEnter())
        {
            result = default!;
            return false;
        }
        try { result = body(); }
        finally { inner.Exit(); }
        return true;
    }

    public void Enter() => inner.Enter();

    public void Exit() => inner.Exit();

    [System.Diagnostics.Conditional("DEBUG")]
    private static void AssertNotRealtime()
    {
        if (RealtimeAudioCallback.IsActive)
            throw new InvalidOperationException(
                "Blocking EngineStateLock acquire inside an audio callback. Hop to another thread first, or TryWithLock and drop.");
    }
}
