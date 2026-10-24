using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MxU.Slides.VideoIO.DeckLink;

namespace MxU.Slides.VideoIO.Tests.DeckLink;

/// <summary>
/// Windows: the hand-declared COM interfaces line up with the SDK. COM calls a method by
/// its position in the vtable, so one missing or swapped declaration would call the wrong
/// driver function. Without a card these declarations can't be exercised, so they are
/// checked against the SDK 16.0 header the Mac app vendors, method by method.
/// </summary>
public sealed partial class DeckLinkInteropTests
{
    private static string? HeaderPath()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var header = Path.Combine(folder.FullName, "apps", "mac", "Packages", "DeckLinkKit", "Sources", "CDeckLink", "Vendor", "DeckLinkAPI.h");
            if (File.Exists(header)) return header;
        }
        return null;
    }

    /// <summary>The method names of one interface in the header, in vtable order (after IUnknown's three).</summary>
    private static List<string> HeaderMethods(string headerFolder, string interfaceName)
    {
        foreach (var file in Directory.GetFiles(headerFolder, "DeckLinkAPI*.h"))
        {
            var text = File.ReadAllText(file);
            var start = text.IndexOf($"class BMD_PUBLIC {interfaceName} :", StringComparison.Ordinal);
            if (start < 0) continue;
            var end = text.IndexOf("protected:", start, StringComparison.Ordinal);
            var close = text.IndexOf("};", start, StringComparison.Ordinal);
            if (end < 0 || (close >= 0 && close < end)) end = close;
            var body = text[start..end];
            return [.. VirtualMethod().Matches(body).Select(m => m.Groups[1].Value)];
        }
        throw new InvalidOperationException($"{interfaceName} isn't in the vendored headers");
    }

    [GeneratedRegex(@"virtual\s+[\w\*\s]+?\s(\w+)\s*\(")]
    private static partial Regex VirtualMethod();

    [Theory]
    [InlineData(typeof(IDeckLinkIterator), "IDeckLinkIterator")]
    [InlineData(typeof(IDeckLinkAPIInformation), "IDeckLinkAPIInformation")]
    [InlineData(typeof(IDeckLink), "IDeckLink")]
    [InlineData(typeof(IDeckLinkProfileAttributes), "IDeckLinkProfileAttributes")]
    [InlineData(typeof(IDeckLinkStatus), "IDeckLinkStatus")]
    [InlineData(typeof(IDeckLinkConfiguration), "IDeckLinkConfiguration")]
    [InlineData(typeof(IDeckLinkKeyer), "IDeckLinkKeyer")]
    [InlineData(typeof(IDeckLinkDisplayModeIterator), "IDeckLinkDisplayModeIterator")]
    [InlineData(typeof(IDeckLinkDisplayMode), "IDeckLinkDisplayMode")]
    [InlineData(typeof(IDeckLinkOutput), "IDeckLinkOutput")]
    [InlineData(typeof(IDeckLinkInput), "IDeckLinkInput")]
    [InlineData(typeof(IDeckLinkMutableVideoFrame), "IDeckLinkVideoFrame")]
    [InlineData(typeof(IDeckLinkVideoBuffer), "IDeckLinkVideoBuffer")]
    [InlineData(typeof(IDeckLinkVideoOutputCallback), "IDeckLinkVideoOutputCallback")]
    [InlineData(typeof(IDeckLinkInputCallback), "IDeckLinkInputCallback")]
    public void EveryDeclaredMethodSitsInTheSdkSlot(Type declared, string headerName)
    {
        var header = HeaderPath();
        if (header is null) Assert.Skip("the Mac's vendored DeckLink headers aren't in this checkout");
        var sdk = HeaderMethods(Path.GetDirectoryName(header)!, headerName);
        // A COM interface's vtable follows declaration order, which is metadata order.
        var methods = declared.GetMethods().OrderBy(m => m.MetadataToken).ToList();
        Assert.NotEmpty(methods);
        for (var index = 0; index < methods.Count; index++)
        {
            Assert.True(index < sdk.Count, $"{declared.Name}.{methods[index].Name} is past the end of the SDK interface");
            Assert.Equal(sdk[index], methods[index].Name);
        }
    }

    [Theory]
    [InlineData(typeof(IDeckLinkIterator), "IID_IDeckLinkIterator")]
    [InlineData(typeof(IDeckLinkAPIInformation), "IID_IDeckLinkAPIInformation")]
    [InlineData(typeof(IDeckLink), "IID_IDeckLink")]
    [InlineData(typeof(IDeckLinkProfileAttributes), "IID_IDeckLinkProfileAttributes")]
    [InlineData(typeof(IDeckLinkStatus), "IID_IDeckLinkStatus")]
    [InlineData(typeof(IDeckLinkConfiguration), "IID_IDeckLinkConfiguration")]
    [InlineData(typeof(IDeckLinkKeyer), "IID_IDeckLinkKeyer")]
    [InlineData(typeof(IDeckLinkDisplayModeIterator), "IID_IDeckLinkDisplayModeIterator")]
    [InlineData(typeof(IDeckLinkDisplayMode), "IID_IDeckLinkDisplayMode")]
    [InlineData(typeof(IDeckLinkOutput), "IID_IDeckLinkOutput")]
    [InlineData(typeof(IDeckLinkInput), "IID_IDeckLinkInput")]
    [InlineData(typeof(IDeckLinkMutableVideoFrame), "IID_IDeckLinkMutableVideoFrame")]
    [InlineData(typeof(IDeckLinkVideoBuffer), "IID_IDeckLinkVideoBuffer")]
    [InlineData(typeof(IDeckLinkVideoOutputCallback), "IID_IDeckLinkVideoOutputCallback")]
    [InlineData(typeof(IDeckLinkInputCallback), "IID_IDeckLinkInputCallback")]
    public void EveryIidIsTheSdkOne(Type declared, string iidName)
    {
        var header = HeaderPath();
        if (header is null) Assert.Skip("the Mac's vendored DeckLink headers aren't in this checkout");
        var text = string.Concat(Directory.GetFiles(Path.GetDirectoryName(header)!, "DeckLinkAPI*.h").Select(File.ReadAllText));
        var match = Regex.Match(text, $@"\b{iidName}\s*=\s*/\*\s*([0-9A-F-]+)\s*\*/");
        Assert.True(match.Success, $"{iidName} not found");
        Assert.Equal(new Guid(match.Groups[1].Value), declared.GUID);
    }

    [Fact]
    public void TheHelperIidsAreTheSdkOnes()
    {
        Assert.Equal(typeof(IDeckLinkVideoBuffer).GUID, DeckLinkIds.IDeckLinkVideoBuffer);
        Assert.Equal(new Guid("6502091C-615F-4F51-BAF6-45C4256DD5B0"), DeckLinkIds.IDeckLinkVideoFrame);
    }

    [Fact]
    public unsafe void TheOutputCallbackCanBeCalledFromNativeCode()
    {
        var callback = new DeckLinkOutput.OutputCallback(null);
        var pointer = Marshal.GetComInterfaceForObject(callback, typeof(IDeckLinkVideoOutputCallback));
        try
        {
            var vtable = *(void***)pointer;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)vtable[3])(pointer, 0, 0));
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[4])(pointer));
            var iid = typeof(IDeckLinkVideoOutputCallback).GUID;
            Assert.Equal(0, Marshal.QueryInterface(pointer, in iid, out var again));
            Marshal.Release(again);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [Fact]
    public unsafe void TheInputCallbackCanBeCalledFromNativeCode()
    {
        var callback = new DeckLinkInput.InputCallback(null);
        var pointer = Marshal.GetComInterfaceForObject(callback, typeof(IDeckLinkInputCallback));
        try
        {
            var vtable = *(void***)pointer;
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)vtable[3])(pointer, 0, 0, 0));
            Assert.Equal(0, ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)vtable[4])(pointer, 0, 0));
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [Fact]
    public void EveryCallbackMethodIsDeclared()
    {
        // A callback interface we implement must list every SDK method, or the driver would call past our vtable.
        var header = HeaderPath();
        if (header is null) Assert.Skip("the Mac's vendored DeckLink headers aren't in this checkout");
        var folder = Path.GetDirectoryName(header)!;
        Assert.Equal(HeaderMethods(folder, "IDeckLinkVideoOutputCallback").Count, typeof(IDeckLinkVideoOutputCallback).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length);
        Assert.Equal(HeaderMethods(folder, "IDeckLinkInputCallback").Count, typeof(IDeckLinkInputCallback).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length);
    }
}
