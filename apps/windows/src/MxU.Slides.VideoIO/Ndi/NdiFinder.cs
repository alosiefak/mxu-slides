using System.Runtime.InteropServices;

namespace MxU.Slides.VideoIO.Ndi;

/// <summary>
/// Lists the NDI sources on the network (including this PC's own), for the operator to
/// pick from. Thread-safe; dispose it when the picker closes. Port of NDIKit/NDIFinder.swift.
/// </summary>
public sealed unsafe class NdiFinder : IDisposable
{
    private readonly Lock gate = new();
    private readonly NdiLibrary library;
    private nint finder;

    private NdiFinder(NdiLibrary library, nint finder)
    {
        this.library = library;
        this.finder = finder;
    }

    /// <summary>A finder, or null when NDI isn't available (<paramref name="library"/> is null) or refused one.</summary>
    public static NdiFinder? Create(NdiLibrary? library)
    {
        if (library is null) return null;
        // Enter the gate first, so a finder is never created halfway through a runtime restart.
        NdiLibrary.RetainInstance();
        var settings = new NdiFindCreate { ShowLocalSources = 1 };
        var finder = ((delegate* unmanaged<NdiFindCreate*, nint>)library.Table.FindCreateV2)(&settings);
        if (finder != 0) return new NdiFinder(library, finder);
        NdiLibrary.ReleaseInstance();
        return null;
    }

    /// <summary>Waits up to <paramref name="waitMilliseconds"/> for the list to change, then returns every source's full name.</summary>
    public IReadOnlyList<string> CurrentSourceNames(uint waitMilliseconds = 1500)
    {
        lock (gate)
        {
            if (finder == 0) return [];
            ((delegate* unmanaged<nint, uint, byte>)library.Table.FindWaitForSources)(finder, waitMilliseconds);
            uint count = 0;
            var sources = ((delegate* unmanaged<nint, uint*, NdiSource*>)library.Table.FindGetCurrentSources)(finder, &count);
            if (sources == null) return [];
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                if (Marshal.PtrToStringUTF8(sources[i].NdiName) is { } name) names.Add(name);
            return names;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (finder == 0) return;
            ((delegate* unmanaged<nint, void>)library.Table.FindDestroy)(finder);
            finder = 0;
        }
        NdiLibrary.ReleaseInstance();
        GC.SuppressFinalize(this);
    }

    ~NdiFinder() => Dispose();
}
