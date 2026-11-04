using System.Runtime.InteropServices;

namespace MxU.Slides.Sync.Automerge;

/// <summary>
/// The raw calls into automerge.dll (apps/windows/native/automerge-ffi/src/lib.rs, which explains
/// each one). Only <see cref="AmDocument"/> calls these: it turns status codes into exceptions,
/// copies bytes out and frees them, and owns each document through a <see cref="DocHandle"/>.
/// </summary>
internal static partial class AutomergeNative
{
    private const string Library = "automerge";

    /// <summary>The interface version this code was written for (FFI_VERSION in lib.rs).</summary>
    public const uint ExpectedFfiVersion = 1;

    /// <summary>Bytes the DLL handed out; give them back with <see cref="BytesFree"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Bytes
    {
        public nint Ptr;
        public nuint Len;
    }

    public const int Ok = 0;
    public const int Panicked = 2;

    [LibraryImport(Library, EntryPoint = "mxu_ffi_version")]
    public static partial uint FfiVersion();

    [LibraryImport(Library, EntryPoint = "mxu_version")]
    public static partial int Version(out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_last_error")]
    public static partial int LastError(out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_bytes_free")]
    public static partial void BytesFree(Bytes bytes);

    [LibraryImport(Library, EntryPoint = "mxu_am_new")]
    public static partial int New(out DocHandle doc);

    [LibraryImport(Library, EntryPoint = "mxu_am_load")]
    public static partial int Load(ReadOnlySpan<byte> data, nuint length, out DocHandle doc);

    [LibraryImport(Library, EntryPoint = "mxu_am_free")]
    public static partial void Free(nint doc);

    [LibraryImport(Library, EntryPoint = "mxu_am_save")]
    public static partial int Save(DocHandle doc, out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_am_fork")]
    public static partial int Fork(DocHandle doc, out DocHandle copy);

    [LibraryImport(Library, EntryPoint = "mxu_am_fork_at")]
    public static partial int ForkAt(DocHandle doc, ReadOnlySpan<byte> heads, nuint length, out DocHandle copy);

    [LibraryImport(Library, EntryPoint = "mxu_am_merge")]
    public static partial int Merge(DocHandle doc, DocHandle other);

    [LibraryImport(Library, EntryPoint = "mxu_am_heads")]
    public static partial int Heads(DocHandle doc, out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_am_changes")]
    public static partial int Changes(DocHandle doc, out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_am_get_actor")]
    public static partial int GetActor(DocHandle doc, out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_am_set_actor")]
    public static partial int SetActor(DocHandle doc, ReadOnlySpan<byte> actor, nuint length);

    [LibraryImport(Library, EntryPoint = "mxu_am_read")]
    public static partial int Read(DocHandle doc, out Bytes output);

    [LibraryImport(Library, EntryPoint = "mxu_am_apply")]
    public static partial int Apply(DocHandle doc, ReadOnlySpan<byte> ops, nuint opsLength,
        ReadOnlySpan<byte> message, nuint messageLength, int hasMessage, long time, out ulong applied);

    /// <summary>Copies bytes the DLL handed out into managed memory and frees them.</summary>
    public static byte[] Take(Bytes bytes)
    {
        try
        {
            if (bytes.Ptr == 0 || bytes.Len == 0) return [];
            var copy = new byte[checked((int)bytes.Len)];
            Marshal.Copy(bytes.Ptr, copy, 0, copy.Length);
            return copy;
        }
        finally
        {
            BytesFree(bytes);
        }
    }

    /// <summary>The reason the last call on this thread failed.</summary>
    public static string LastErrorMessage() =>
        LastError(out var bytes) == Ok ? System.Text.Encoding.UTF8.GetString(Take(bytes)) : "unknown Automerge error";
}

/// <summary>
/// Owns one Automerge document in the DLL and frees it exactly once, even if the code using it
/// forgets to (the garbage collector's finalizer calls <see cref="ReleaseHandle"/>). While a call
/// is using it, .NET keeps it alive, so a document can't be freed in the middle of a call.
/// </summary>
internal sealed class DocHandle : SafeHandle
{
    public DocHandle()
        : base(0, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        AutomergeNative.Free(handle);
        return true;
    }
}
