using System.Runtime.InteropServices;

// Media Foundation is a COM API. These are hand-written declarations of the few interfaces,
// functions and GUIDs this project uses, copied from the Windows SDK headers (mfapi.h,
// mfidl.h, mfobjects.h, mftransform.h, mfreadwrite.h, codecapi.h, strmif.h). Method order in
// each interface must match the header exactly: COM calls methods by their position in the
// vtable, not by name. Methods we never call are still declared (as placeholders) so the
// ones after them land in the right slot.
#pragma warning disable IDE1006 // COM names keep their Windows spelling.
namespace MxU.Slides.Broadcast.Media.Interop;

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public long hVal;
    [FieldOffset(8)] public nint pointer;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_UI4 = 19;
    public const ushort VT_UI8 = 21;
    public const ushort VT_I8 = 20;
}

/// <summary>A VARIANT (what ICodecAPI takes): a type tag and an 8-byte value, 24 bytes on 64-bit Windows.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct ComVariantValue
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public uint ulVal;
    [FieldOffset(8)] public short boolVal;
    [FieldOffset(8)] public ulong ullVal;

    public const ushort VT_BOOL = 11;
    public const ushort VT_UI4 = 19;
    public const ushort VT_UI8 = 21;

    public static ComVariantValue UInt32(uint value) => new() { vt = VT_UI4, ulVal = value };

    public static ComVariantValue Bool(bool value) => new() { vt = VT_BOOL, boolVal = value ? (short)-1 : (short)0 };
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_REGISTER_TYPE_INFO
{
    public Guid guidMajorType;
    public Guid guidSubtype;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_INPUT_STREAM_INFO
{
    public long hnsMaxLatency;
    public uint dwFlags;
    public uint cbSize;
    public uint cbMaxLookahead;
    public uint cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO
{
    public uint dwFlags;
    public uint cbSize;
    public uint cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public uint dwStreamID;
    public nint pSample;
    public uint dwStatus;
    public nint pEvents;
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid guidKey, ref PropVariant pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, in PropVariant value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);
    [PreserveSig] int SetItem(in Guid guidKey, in PropVariant value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, ref PropVariant pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // IMFAttributes
    [PreserveSig] int GetItem(in Guid guidKey, ref PropVariant pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, in PropVariant value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);
    [PreserveSig] int SetItem(in Guid guidKey, in PropVariant value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, ref PropVariant pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // IMFMediaType
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat(out int pfCompressed);
    [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig] int GetRepresentation(Guid guidRepresentation, out nint ppvRepresentation);
    [PreserveSig] int FreeRepresentation(Guid guidRepresentation, nint pvRepresentation);
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out nint ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // IMFAttributes
    [PreserveSig] int GetItem(in Guid guidKey, ref PropVariant pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, in PropVariant value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);
    [PreserveSig] int SetItem(in Guid guidKey, in PropVariant value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, ref PropVariant pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // IMFSample
    [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    // IMFAttributes
    [PreserveSig] int GetItem(in Guid guidKey, ref PropVariant pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, in PropVariant value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);
    [PreserveSig] int SetItem(in Guid guidKey, in PropVariant value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, ref PropVariant pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // IMFActivate
    [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEvent
{
    // IMFAttributes
    [PreserveSig] int GetItem(in Guid guidKey, ref PropVariant pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out int pType);
    [PreserveSig] int CompareItem(in Guid guidKey, in PropVariant value, out int pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, int matchType, out int pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);
    [PreserveSig] int SetItem(in Guid guidKey, in PropVariant value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, ref PropVariant pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);

    // IMFMediaEvent
    [PreserveSig] int GetEventType(out uint pmet);
    [PreserveSig] int GetExtendedType(out Guid pguidExtendedType);
    [PreserveSig] int GetStatus(out int phrStatus);
    [PreserveSig] int GetValue(ref PropVariant pvValue);
}

[ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint dwFlags, out IMFMediaEvent ppEvent);
    [PreserveSig] int BeginGetEvent(nint pCallback, nint punkState);
    [PreserveSig] int EndGetEvent(nint pResult, out IMFMediaEvent ppEvent);
    [PreserveSig] int QueueEvent(uint met, in Guid guidExtendedType, int hrStatus, nint pvValue);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
    [PreserveSig] int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
    [PreserveSig] int GetStreamIDs(uint dwInputIDArraySize, [Out] uint[] pdwInputIDs, uint dwOutputIDArraySize, [Out] uint[] pdwOutputIDs);
    [PreserveSig] int GetInputStreamInfo(uint dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);
    [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
    [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
    [PreserveSig] int GetInputStreamAttributes(uint dwInputStreamID, out IMFAttributes pAttributes);
    [PreserveSig] int GetOutputStreamAttributes(uint dwOutputStreamID, out IMFAttributes pAttributes);
    [PreserveSig] int DeleteInputStream(uint dwStreamID);
    [PreserveSig] int AddInputStreams(uint cStreams, [In] uint[] adwStreamIDs);
    [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
    [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
    [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType? pType, uint dwFlags);
    [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType? pType, uint dwFlags);
    [PreserveSig] int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
    [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
    [PreserveSig] int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
    [PreserveSig] int GetOutputStatus(out uint pdwFlags);
    [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
    [PreserveSig] int ProcessEvent(uint dwInputStreamID, nint pEvent);
    [PreserveSig] int ProcessMessage(uint eMessage, nint ulParam);
    [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);
    [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount, ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out uint pdwStatus);
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid Api);
    [PreserveSig] int IsModifiable(in Guid Api);
    [PreserveSig] int GetParameterRange(in Guid Api, nint ValueMin, nint ValueMax, nint SteppingDelta);
    [PreserveSig] int GetParameterValues(in Guid Api, out nint Values, out uint ValuesCount);
    [PreserveSig] int GetDefaultValue(in Guid Api, nint Value);
    [PreserveSig] int GetValue(in Guid Api, ref ComVariantValue Value);
    [PreserveSig] int SetValue(in Guid Api, in ComVariantValue Value);
    [PreserveSig] int RegisterForEvent(in Guid Api, nint userData);
    [PreserveSig] int UnregisterForEvent(in Guid Api);
    [PreserveSig] int SetAllDefaults();
    [PreserveSig] int SetValueWithNotify(in Guid Api, nint Value, out nint ChangedParam, out uint ChangedParamCount);
    [PreserveSig] int SetAllDefaultsWithNotify(out nint ChangedParam, out uint ChangedParamCount);
    [PreserveSig] int GetAllSettings(nint pStream);
    [PreserveSig] int SetAllSettings(nint pStream);
    [PreserveSig] int SetAllSettingsWithNotify(nint pStream, out nint ChangedParam, out uint ChangedParamCount);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
    [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
    [PreserveSig] int SendStreamTick(uint dwStreamIndex, long llTimestamp);
    [PreserveSig] int PlaceMarker(uint dwStreamIndex, nint pvContext);
    [PreserveSig] int NotifyEndOfSegment(uint dwStreamIndex);
    [PreserveSig] int Flush(uint dwStreamIndex);
    [PreserveSig] int DoFinalize();
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    [PreserveSig] int GetStatistics(uint dwStreamIndex, nint pStats);
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint dwStreamIndex, out int pfSelected);
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, int fSelected);
    [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, nint pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition(in Guid guidTimeFormat, in PropVariant varPosition);
    [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex, out uint pdwStreamFlags, out long pllTimestamp, out IMFSample? ppSample);
    [PreserveSig] int Flush(uint dwStreamIndex);
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out nint ppvObject);
    [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, in Guid guidAttribute, ref PropVariant pvarAttribute);
}

internal static class MF
{
    public const uint MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_FULL = 0;

    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const uint MFT_ENUM_FLAG_LOCALMFT = 0x00000010;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;
    public const uint MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x00000200;

    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    public const uint MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;
    public const uint METransformDrainComplete = 603;
    public const uint MEError = 1;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const int MF_E_SHUTDOWN = unchecked((int)0xC00D3E85);

    public const uint MF_EVENT_FLAG_NO_WAIT = 0x00000001;

    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    public const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

    public const uint MFVideoInterlace_Progressive = 2;
    public const uint eAVEncH264VProfile_Base = 66;
    public const uint eAVEncH264VProfile_Main = 77;
    public const uint eAVEncH264VProfile_High = 100;
    public const uint eAVEncCommonRateControlMode_CBR = 0;
    public const uint eAVEncCommonRateControlMode_PeakConstrainedVBR = 1;
    public const uint MFNominalRange_16_235 = 2;
    public const uint MFVideoTransferMatrix_BT709 = 1;
    public const uint MFVideoPrimaries_BT709 = 2;
    public const uint MFVideoTransFunc_709 = 5;

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    public static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    public static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    public static readonly Guid MF_MT_USER_DATA = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");

    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MFT_ENUM_HARDWARE_URL_Attribute = new("2fb866ac-b078-4942-ab6c-003d05cda674");
    public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid MFTranscodeContainerType_FMPEG4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004");
    public static readonly Guid MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MFT_CATEGORY_AUDIO_ENCODER = new("91c64bd0-f91e-4d8c-9276-db248279d975");

    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid CODECAPI_AVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAlignedMemoryBuffer(uint cbMaxLength, uint cbAligment, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(
        Guid guidCategory, uint flags, nint pInputType, nint pOutputType, out nint pppMFTActivate, out uint pnumMFTActivate);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(
        string pwszOutputURL, nint pByteStream, IMFAttributes? pAttributes, out IMFSinkWriter ppSinkWriter);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(
        string pwszURL, IMFAttributes? pAttributes, out IMFSourceReader ppSourceReader);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PropVariant pvar);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MediaFoundationException(what, hr);
    }

    public static ulong Pack(uint high, uint low) => (ulong)high << 32 | low;
}
