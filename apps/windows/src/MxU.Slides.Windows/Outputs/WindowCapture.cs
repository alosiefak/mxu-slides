using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Copies a window's picture into memory on every change, through the graphics card
/// (Windows.Graphics.Capture, Windows 10 1903+). This is how the program feed reads the
/// hidden program window: drawing happens on the GPU as for any window, and only the
/// finished frame is copied back, which is far cheaper than WPF's software
/// RenderTargetBitmap. Frames arrive on a capture thread.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice winrtDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly Lock gate = new();
    private ID3D11Texture2D? staging;
    private bool disposed;

    /// <param name="onFrame">Called with the BGRA pixels (top-down rows, <c>width * 4</c> bytes each), width and height. Valid during the call.</param>
    public WindowCapture(IntPtr window, Action<byte[], int, int> onFrame)
    {
        OnFrame = onFrame;
        // The graphics card if there is one; otherwise Windows' software renderer (WARP), e.g. on a server.
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
                out ID3D11Device hardware, out ID3D11DeviceContext hardwareContext).Failure)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                out hardware, out hardwareContext).CheckError();
            UsesSoftware = true;
        }
        device = hardware;
        context = hardwareContext;
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }

        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForWindow(window, GraphicsCaptureItemId);
        item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);

        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        pool.FrameArrived += OnFrameArrived;
        session = pool.CreateCaptureSession(item);
        // The window is off every screen, so Windows' yellow "being captured" border is never seen.
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
    }

    public Action<byte[], int, int> OnFrame { get; }

    /// <summary>True when there's no graphics card and Windows' software renderer is doing the work.</summary>
    public bool UsesSoftware { get; }

    /// <summary>True on Windows builds with window capture.</summary>
    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    private byte[]? buffer;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        lock (gate)
        {
            if (disposed) return;
            try
            {
                var size = frame.ContentSize;
                if (size.Width != item.Size.Width || size.Height != item.Size.Height)
                    sender.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                using var texture = new ID3D11Texture2D(access.GetInterface(typeof(ID3D11Texture2D).GUID));
                var description = texture.Description;
                int width = (int)Math.Min(description.Width, (uint)size.Width), height = (int)Math.Min(description.Height, (uint)size.Height);
                if (staging is null || staging.Description.Width != description.Width || staging.Description.Height != description.Height)
                {
                    staging?.Dispose();
                    staging = device.CreateTexture2D(description with
                    {
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None,
                        MipLevels = 1,
                        ArraySize = 1,
                    });
                }
                context.CopyResource(staging, texture);
                var mapped = context.Map(staging, 0, MapMode.Read);
                try
                {
                    var rowBytes = width * 4;
                    if (buffer is null || buffer.Length != rowBytes * height) buffer = new byte[rowBytes * height];
                    for (var y = 0; y < height; y++)
                        Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, buffer, y * rowBytes, rowBytes);
                }
                finally
                {
                    context.Unmap(staging, 0);
                }
                OnFrame(buffer, width, height);
            }
            catch (Exception ex)
            {
                Log.Error("Capturing the program window failed for one frame", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        pool.FrameArrived -= OnFrameArrived;
        session.Dispose();
        pool.Dispose();
        lock (gate)
        {
            staging?.Dispose();
            context.Dispose();
            device.Dispose();
        }
    }
}
