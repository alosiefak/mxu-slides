using System.Runtime.InteropServices;

namespace MxU.Slides.VideoIO.DeckLink;

// The slice of Blackmagic's DeckLink API (Desktop Video SDK 16.0) this app uses,
// declared by hand so no SDK files ship. The Desktop Video driver installs and
// registers the COM classes; when it isn't installed, creating the iterator simply
// fails and the rest of the app sees no devices.
//
// Rules for reading these declarations:
// - The IIDs are the 16.0 ones from the SDK headers vendored with the Mac app
//   (apps/mac/Packages/DeckLinkKit/Sources/CDeckLink/Vendor). A driver older than 16.0
//   doesn't know them and answers E_NOINTERFACE, which DeckLinkRuntime reports as
//   "too old".
// - Methods are listed in vtable order (the order of the SDK's IDL), because COM finds
//   a method by its position, not its name. A method this app never calls still has to
//   be declared if a later one is used; trailing unused methods are left out.
// - Windows types: BOOL is a four-byte int, strings are BSTRs, "long" is 32 bits.
// - Every method is [PreserveSig]: it returns its HRESULT (0 = S_OK, 1 = S_FALSE),
//   and nothing here throws on a failed call.
// - Interface pointers that arrive in callbacks are IntPtr, never RCWs: those objects
//   belong to the driver's pools, and an RCW would hold them until the garbage
//   collector got round to it, starving the card of buffers.

internal static class DeckLinkIds
{
    public static readonly Guid CDeckLinkIterator = new("BA6C6F44-6DA5-4DCE-94AA-EE2D1372A676");
    public static readonly Guid CDeckLinkAPIInformation = new("263CA19F-ED09-482E-9F9D-84005783A237");

    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IDeckLinkVideoFrame = new("6502091C-615F-4F51-BAF6-45C4256DD5B0");
    public static readonly Guid IDeckLinkVideoBuffer = new("81F03D70-DE13-4B17-873A-C8AC9689C682");
}

/// <summary>DeckLink enum values (BMD… in the SDK). Most are four-character codes.</summary>
internal static class Bmd
{
    // BMDDeckLinkAttributeID
    public const uint SupportsInternalKeying = 0x6B657969; // 'keyi'
    public const uint SupportsExternalKeying = 0x6B657965; // 'keye'
    public const uint SupportsInputFormatDetection = 0x696E6664; // 'infd'
    public const uint MaximumAudioChannels = 0x6D616368; // 'mach'
    public const uint PersistentID = 0x70656964; // 'peid'
    public const uint TopologicalID = 0x746F6964; // 'toid'
    public const uint AudioInputConnections = 0x6169636E; // 'aicn'
    public const uint VideoIOSupport = 0x76696F73; // 'vios'

    // BMDDeckLinkAPIInformationID
    public const uint APIVersion = 0x76657273; // 'vers'

    // BMDDeckLinkStatusID
    public const uint StatusReferenceSignalLocked = 0x7265666C; // 'refl'

    // BMDDeckLinkConfigurationID
    public const uint ConfigDefaultVideoOutputMode = 0x64766F6D; // 'dvom'
    public const uint ConfigSDIOutputLinkConfiguration = 0x736F6C63; // 'solc'
    public const uint ConfigAudioInputConnection = 0x6169636E; // 'aicn'

    // BMDVideoIOSupport, BMDAudioConnection
    public const long DeviceSupportsCapture = 1 << 0;
    public const long DeviceSupportsPlayback = 1 << 1;
    public const long AudioConnectionEmbedded = 1 << 0;

    // BMDPixelFormat
    public const uint Format8BitYUV = 0x32767579; // '2vuy' (UYVY)
    public const uint Format8BitBGRA = 0x42475241; // 'BGRA'

    // BMDDisplayMode
    public const uint ModeHD1080p2997 = 0x48703239; // 'Hp29'

    // BMDFieldDominance
    public const uint LowerFieldFirst = 0x6C6F7772; // 'lowr'
    public const uint UpperFieldFirst = 0x75707072; // 'uppr'
    public const uint ProgressiveFrame = 0x70726F67; // 'prog'

    // Flags
    public const uint VideoConnectionUnspecified = 0;
    public const uint NoVideoConversion = 0x6E6F6E65; // 'none'
    public const uint SupportedVideoModeDefault = 0;
    public const uint SupportedVideoModeKeying = 1 << 0;
    public const uint VideoOutputFlagDefault = 0;
    public const uint VideoInputFlagDefault = 0;
    public const uint VideoInputEnableFormatDetection = 1 << 0;
    public const uint FrameFlagDefault = 0;
    public const uint FrameHasNoInputSource = 1u << 31;
    public const uint BufferAccessRead = 1 << 0;
    public const uint BufferAccessWrite = 1 << 1;
    public const uint AudioSampleRate48kHz = 48000;
    public const uint AudioSampleType32bitInteger = 32;

    // BMDOutputFrameCompletionResult
    public const uint OutputFrameCompleted = 0;
    public const uint OutputFrameDisplayedLate = 1;
    public const uint OutputFrameDropped = 2;
    public const uint OutputFrameFlushed = 3;

    // BMDLinkConfiguration
    public const uint LinkSingle = 0x6C63736C; // 'lcsl'
    public const uint LinkDual = 0x6C63646C; // 'lcdl'
    public const uint LinkQuad = 0x6C63716C; // 'lcql'
}

[ComImport, Guid("50FB36CD-3063-4B73-BDBB-958087F2D8BA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkIterator
{
    [PreserveSig] int Next(out IDeckLink? deckLinkInstance);
}

[ComImport, Guid("7BEA3C68-730D-4322-AF34-8A7152B532A4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkAPIInformation
{
    [PreserveSig] int GetFlag(uint cfgID, out int value);
    [PreserveSig] int GetInt(uint cfgID, out long value);
}

[ComImport, Guid("C418FBDD-0587-48ED-8FE5-640F0A14AF91"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLink
{
    [PreserveSig] int GetModelName([MarshalAs(UnmanagedType.BStr)] out string? modelName);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.BStr)] out string? displayName);
}

[ComImport, Guid("F47551D7-AD22-47AF-BCFD-6BE88AA879D9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkProfileAttributes
{
    [PreserveSig] int GetFlag(uint cfgID, out int value);
    [PreserveSig] int GetInt(uint cfgID, out long value);
    [PreserveSig] int GetFloat(uint cfgID, out double value);
    [PreserveSig] int GetString(uint cfgID, [MarshalAs(UnmanagedType.BStr)] out string? value);
}

[ComImport, Guid("2A04A635-ED42-41EF-9342-0E11F8CF6B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkStatus
{
    [PreserveSig] int GetFlag(uint statusID, out int value);
    [PreserveSig] int GetInt(uint statusID, out long value);
}

[ComImport, Guid("5A68FFD4-1C12-4EDE-A6D2-45451D385FC1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkConfiguration
{
    [PreserveSig] int SetFlag(uint cfgID, int value);
    [PreserveSig] int GetFlag(uint cfgID, out int value);
    [PreserveSig] int SetInt(uint cfgID, long value);
    [PreserveSig] int GetInt(uint cfgID, out long value);
}

[ComImport, Guid("89AFCAF5-65F8-421E-98F7-96FE5F5BFBA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkKeyer
{
    [PreserveSig] int Enable(int isExternal);
    [PreserveSig] int SetLevel(byte level);
    [PreserveSig] int RampUp(uint numberOfFrames);
    [PreserveSig] int RampDown(uint numberOfFrames);
    [PreserveSig] int Disable();
}

[ComImport, Guid("9C88499F-F601-4021-B80B-032E4EB41C35"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkDisplayModeIterator
{
    [PreserveSig] int Next(out IDeckLinkDisplayMode? deckLinkDisplayMode);
}

[ComImport, Guid("3EB2C1AB-0A3D-4523-A3AD-F40D7FB14E78"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkDisplayMode
{
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string? name);
    [PreserveSig] uint GetDisplayMode();
    [PreserveSig] int GetWidth();
    [PreserveSig] int GetHeight();
    [PreserveSig] int GetFrameRate(out long frameDuration, out long timeScale);
    [PreserveSig] uint GetFieldDominance();
    [PreserveSig] uint GetFlags();
}

[ComImport, Guid("5F227C95-39D7-46C7-8B7D-9C81795FBBE4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkOutput
{
    [PreserveSig] int DoesSupportVideoMode(uint connection, uint requestedMode, uint requestedPixelFormat, uint conversionMode, uint flags, out uint actualMode, out int supported);
    [PreserveSig] int GetDisplayMode(uint displayMode, out IDeckLinkDisplayMode? resultDisplayMode);
    [PreserveSig] int GetDisplayModeIterator(out IDeckLinkDisplayModeIterator? iterator);
    [PreserveSig] int SetScreenPreviewCallback(IntPtr previewCallback);
    [PreserveSig] int EnableVideoOutput(uint displayMode, uint flags);
    [PreserveSig] int DisableVideoOutput();
    [PreserveSig] int CreateVideoFrame(int width, int height, int rowBytes, uint pixelFormat, uint flags, out IDeckLinkMutableVideoFrame? outFrame);
    [PreserveSig] int CreateVideoFrameWithBuffer(int width, int height, int rowBytes, uint pixelFormat, uint flags, IntPtr buffer, out IntPtr outFrame);
    [PreserveSig] int RowBytesForPixelFormat(uint pixelFormat, int width, out int rowBytes);
    [PreserveSig] int CreateAncillaryData(uint pixelFormat, out IntPtr outBuffer);
    [PreserveSig] int DisplayVideoFrameSync(IntPtr theFrame);
    [PreserveSig] int ScheduleVideoFrame(IntPtr theFrame, long displayTime, long displayDuration, long timeScale);
    [PreserveSig] int SetScheduledFrameCompletionCallback(IDeckLinkVideoOutputCallback? theCallback);
    [PreserveSig] int GetBufferedVideoFrameCount(out uint bufferedFrameCount);
    [PreserveSig] int EnableAudioOutput(uint sampleRate, uint sampleType, uint channelCount, uint streamType);
    [PreserveSig] int DisableAudioOutput();
    [PreserveSig] int WriteAudioSamplesSync(IntPtr buffer, uint sampleFrameCount, out uint sampleFramesWritten);
    [PreserveSig] int BeginAudioPreroll();
    [PreserveSig] int EndAudioPreroll();
    [PreserveSig] int ScheduleAudioSamples(IntPtr buffer, uint sampleFrameCount, long streamTime, long timeScale, out uint sampleFramesWritten);
    [PreserveSig] int GetBufferedAudioSampleFrameCount(out uint bufferedSampleFrameCount);
    [PreserveSig] int FlushBufferedAudioSamples();
    [PreserveSig] int SetAudioCallback(IntPtr theCallback);
    [PreserveSig] int StartScheduledPlayback(long playbackStartTime, long timeScale, double playbackSpeed);
    [PreserveSig] int StopScheduledPlayback(long stopPlaybackAtTime, out long actualStopTime, long timeScale);
    [PreserveSig] int IsScheduledPlaybackRunning(out int active);
    [PreserveSig] int GetScheduledStreamTime(long desiredTimeScale, out long streamTime, out double playbackSpeed);
    [PreserveSig] int GetReferenceStatus(out uint referenceStatus);
    [PreserveSig] int GetHardwareReferenceClock(long desiredTimeScale, out long hardwareTime, out long timeInFrame, out long ticksPerFrame);
    [PreserveSig] int GetFrameCompletionReferenceTimestamp(IntPtr theFrame, long desiredTimeScale, out long frameCompletionTimestamp);
}

[ComImport, Guid("6A515F8A-FBCE-4853-B0F7-2A09DB1ECA0B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkInput
{
    [PreserveSig] int DoesSupportVideoMode(uint connection, uint requestedMode, uint requestedPixelFormat, uint conversionMode, uint flags, out uint actualMode, out int supported);
    [PreserveSig] int GetDisplayMode(uint displayMode, out IDeckLinkDisplayMode? resultDisplayMode);
    [PreserveSig] int GetDisplayModeIterator(out IDeckLinkDisplayModeIterator? iterator);
    [PreserveSig] int SetScreenPreviewCallback(IntPtr previewCallback);
    [PreserveSig] int EnableVideoInput(uint displayMode, uint pixelFormat, uint flags);
    [PreserveSig] int EnableVideoInputWithAllocatorProvider(uint displayMode, uint pixelFormat, uint flags, IntPtr allocatorProvider);
    [PreserveSig] int DisableVideoInput();
    [PreserveSig] int GetAvailableVideoFrameCount(out uint availableFrameCount);
    [PreserveSig] int EnableAudioInput(uint sampleRate, uint sampleType, uint channelCount);
    [PreserveSig] int DisableAudioInput();
    [PreserveSig] int GetAvailableAudioSampleFrameCount(out uint availableSampleFrameCount);
    [PreserveSig] int StartStreams();
    [PreserveSig] int StopStreams();
    [PreserveSig] int PauseStreams();
    [PreserveSig] int FlushStreams();
    [PreserveSig] int SetCallback(IDeckLinkInputCallback? theCallback);
    [PreserveSig] int GetHardwareReferenceClock(long desiredTimeScale, out long hardwareTime, out long timeInFrame, out long ticksPerFrame);
}

/// <summary>A frame the app made with CreateVideoFrame. Only the IDeckLinkVideoFrame half is called.</summary>
[ComImport, Guid("CF9EB134-0374-4C5B-95FA-1EC14819FF62"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkMutableVideoFrame
{
    [PreserveSig] int GetWidth();
    [PreserveSig] int GetHeight();
    [PreserveSig] int GetRowBytes();
    [PreserveSig] uint GetPixelFormat();
    [PreserveSig] uint GetFlags();
    [PreserveSig] int GetTimecode(uint format, out IntPtr timecode);
    [PreserveSig] int GetAncillaryData(out IntPtr ancillary);
}

/// <summary>The pixels of a frame: lock them with StartAccess, read or write via GetBytes, unlock with EndAccess.</summary>
[ComImport, Guid("81F03D70-DE13-4B17-873A-C8AC9689C682"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkVideoBuffer
{
    [PreserveSig] int GetBytes(out IntPtr buffer);
    [PreserveSig] int GetSize(out ulong size);
    [PreserveSig] int StartAccess(uint flags);
    [PreserveSig] int EndAccess(uint flags);
}

/// <summary>Implemented by <see cref="DeckLinkOutput"/>: the card calls it each time a scheduled frame has been shown.</summary>
[ComImport, Guid("5BE6DF26-02CE-433E-99D9-9A87C3AC171F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkVideoOutputCallback
{
    [PreserveSig] int ScheduledFrameCompleted(IntPtr completedFrame, uint result);
    [PreserveSig] int ScheduledPlaybackHasStopped();
}

/// <summary>Implemented by <see cref="DeckLinkInput"/>: the card calls it for each captured frame and when the signal format changes.</summary>
[ComImport, Guid("3A94F075-C37D-4BA8-BCC0-1D778C8F881B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeckLinkInputCallback
{
    [PreserveSig] int VideoInputFormatChanged(uint notificationEvents, IntPtr newDisplayMode, uint detectedSignalFlags);
    [PreserveSig] int VideoInputFrameArrived(IntPtr videoFrame, IntPtr audioPacket);
}

/// <summary>
/// Calls on interface pointers the driver lends us inside callbacks, by vtable slot
/// (0–2 are IUnknown's QueryInterface, AddRef, Release; the interface's own methods
/// follow in declaration order). No RCW is made, so nothing outlives the callback.
/// </summary>
internal static unsafe class ComCall
{
    private static void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];

    public static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        IntPtr result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, &iid, &result);
        return hr == 0 ? result : 0;
    }

    public static void Release(IntPtr instance) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2))(instance);

    // IDeckLinkVideoFrame / IDeckLinkVideoInputFrame
    public static int FrameWidth(IntPtr frame) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(frame, 3))(frame);
    public static int FrameHeight(IntPtr frame) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(frame, 4))(frame);
    public static int FrameRowBytes(IntPtr frame) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(frame, 5))(frame);
    public static uint FramePixelFormat(IntPtr frame) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(frame, 6))(frame);
    public static uint FrameFlags(IntPtr frame) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(frame, 7))(frame);

    public static bool HardwareReferenceTimestamp(IntPtr inputFrame, long timeScale, out long frameTime, out long frameDuration)
    {
        long time = 0, duration = 0;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, long, long*, long*, int>)Slot(inputFrame, 11))(inputFrame, timeScale, &time, &duration);
        frameTime = time;
        frameDuration = duration;
        return hr == 0;
    }

    // IDeckLinkVideoBuffer
    public static IntPtr BufferBytes(IntPtr buffer)
    {
        IntPtr bytes = 0;
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(buffer, 3))(buffer, &bytes) == 0 ? bytes : 0;
    }

    public static bool StartAccess(IntPtr buffer, uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 5))(buffer, flags) == 0;
    public static void EndAccess(IntPtr buffer, uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 6))(buffer, flags);

    // IDeckLinkAudioInputPacket
    public static int AudioFrameCount(IntPtr packet) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(packet, 3))(packet);

    public static IntPtr AudioBytes(IntPtr packet)
    {
        IntPtr bytes = 0;
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(packet, 4))(packet, &bytes) == 0 ? bytes : 0;
    }

    // IDeckLinkDisplayMode
    public static string ModeName(IntPtr mode)
    {
        IntPtr bstr = 0;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(mode, 3))(mode, &bstr) != 0 || bstr == 0) return "";
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { Marshal.FreeBSTR(bstr); }
    }

    public static uint ModeId(IntPtr mode) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(mode, 4))(mode);
    public static int ModeWidth(IntPtr mode) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(mode, 5))(mode);
    public static int ModeHeight(IntPtr mode) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(mode, 6))(mode);

    public static void ModeFrameRate(IntPtr mode, out long frameDuration, out long timeScale)
    {
        long duration = 0, scale = 0;
        ((delegate* unmanaged[Stdcall]<IntPtr, long*, long*, int>)Slot(mode, 7))(mode, &duration, &scale);
        frameDuration = duration;
        timeScale = scale;
    }

    public static uint ModeFieldDominance(IntPtr mode) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(mode, 8))(mode);
}

/// <summary>
/// Runs DeckLink work on a multithreaded-apartment thread. COM objects remember the
/// apartment they were made in; one made on the WPF UI thread (a single-threaded
/// apartment) would have every later call from the card's own threads marshalled back
/// through the UI thread. Making and using them on MTA threads avoids that.
/// </summary>
internal static class Mta
{
    public static T Run<T>(Func<T> work) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA ? work() : Task.Run(work).GetAwaiter().GetResult();

    public static void Run(Action work) => Run(() =>
    {
        work();
        return true;
    });
}

/// <summary>Creating the driver's COM objects and letting them go.</summary>
internal static class DeckLinkCom
{
    /// <summary>CoCreateInstance through the runtime; null when the class isn't registered (no driver).</summary>
    public static T? Create<T>(Guid clsid) where T : class
    {
        try
        {
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            var instance = type is null ? null : Activator.CreateInstance(type);
            if (instance is T typed) return typed;
            Release(instance);
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives back one reference to an RCW now rather than whenever the garbage collector
    /// runs. The runtime keeps one RCW per driver object and counts each time the object
    /// is handed to us, so two enumerations (or an enumeration and an open output) can
    /// share an RCW; each releases only its own count. (FinalReleaseComObject would
    /// pull it out from under the other.)
    /// </summary>
    public static void Release(object? rcw)
    {
        if (rcw is not null && Marshal.IsComObject(rcw)) Marshal.ReleaseComObject(rcw);
    }
}
