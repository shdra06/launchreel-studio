using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Studio.Host;

/// <summary>
/// Windows.Graphics.Capture of a single window. Windows only delivers a frame when the window's pixels change, so
/// consumers must treat <see cref="Latest"/> as "what the window looks like now", not as a frame clock.
/// </summary>
public sealed class NativeCapture : IDisposable
{
    private readonly object gate = new();
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private IDirect3DDevice? device;
    private GraphicsCaptureItem? item;
    private SizeInt32 size;
    private int generation;
    private int busy;
    private int pending;
    private long last;
    private long sequence;
    private CapturedFrame? latest;

    public CapturedFrame? Latest => Volatile.Read(ref latest);
    /// <summary>Upper bound on frames copied from the GPU per second. Raised while recording.</summary>
    public double MaxRate { get; set; } = 30;
    public event Action<CapturedFrame>? Frame;
    public event Action<string>? Failed;
    public nint WindowHandle { get; private set; }

    public void Start(nint hwnd) => Begin(hwnd, false);

    /// <summary>
    /// Captures a whole display: the app together with its pop-ups, previews and menus, which are separate windows a
    /// window capture never includes. Frame the part you need with a shot crop in the editor.
    /// </summary>
    public void StartMonitor(nint monitor) => Begin(monitor, true);

    private void Begin(nint hwnd, bool monitor)
    {
        Dispose();
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("Windows capture is unavailable on this device.");
        var name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        nint factory = 0, result = 0;
        GraphicsCaptureItem created;
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(monitor ? interop.CreateForMonitor(hwnd, ref itemIid, out result) : interop.CreateForWindow(hwnd, ref itemIid, out result));
            created = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally { if (result != 0) Marshal.Release(result); if (factory != 0) Marshal.Release(factory); WindowsDeleteString(hstring); }
        nint d3d = 0, context = 0, dxgi = 0, inspectable = 0;
        IDirect3DDevice createdDevice;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out d3d, out _, out context));
            var dxgiIid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in dxgiIid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            createdDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally { foreach (var pointer in new[] { inspectable, dxgi, context, d3d }) if (pointer != 0) Marshal.Release(pointer); }
        lock (gate)
        {
            item = created;
            device = createdDevice;
            size = item.Size;
            WindowHandle = monitor ? 0 : hwnd;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            pool.FrameArrived += OnFrame;
            item.Closed += OnClosed;
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = true;
            // Windows 11 draws a yellow capture border around the source by default. It never appears in captured
            // frames, but it distracts while demonstrating the app in App view.
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                try { session.IsBorderRequired = false; } catch { }
            session.StartCapture();
        }
    }

    // Windows only sends a frame when the window changes. A frame that arrives while the previous one is still being
    // copied, or faster than MaxRate, is therefore never discarded outright: the newest pending frame is always
    // processed afterwards, otherwise the final state after a click or keystroke could be missing until the next change.
    private async void OnFrame(Direct3D11CaptureFramePool sender, object? args)
    {
        Volatile.Write(ref pending, 1);
        if (Interlocked.Exchange(ref busy, 1) != 0) return;
        var current = Volatile.Read(ref generation);
        try
        {
            while (Interlocked.Exchange(ref pending, 0) == 1 && current == Volatile.Read(ref generation))
            {
                var wait = .85 / Math.Max(1, MaxRate) - Stopwatch.GetElapsedTime(last).TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                await ProcessNewest(sender, current);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception e) when (current == Volatile.Read(ref generation)) { Failed?.Invoke(e.Message); }
        catch { }
        finally { Interlocked.Exchange(ref busy, 0); }
        if (Volatile.Read(ref pending) == 1 && current == Volatile.Read(ref generation)) OnFrame(sender, null);
    }

    private async Task ProcessNewest(Direct3D11CaptureFramePool sender, int current)
    {
        Direct3D11CaptureFrame? frame = null;
        for (var next = sender.TryGetNextFrame(); next is not null; next = sender.TryGetNextFrame()) { frame?.Dispose(); frame = next; }
        if (frame is null) return;
        using (frame)
        {
            if (frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0) return;
            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
            {
                lock (gate)
                {
                    if (current != generation || device is null) return;
                    size = frame.ContentSize;
                    sender.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                }
                return;
            }
            last = Stopwatch.GetTimestamp();
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            // A frame copied for the previous source must never surface after the source changed.
            if (current != Volatile.Read(ref generation)) return;
            var captured = new CapturedFrame(pixels, bitmap.PixelWidth, bitmap.PixelHeight, last, Interlocked.Increment(ref sequence));
            Volatile.Write(ref latest, captured);
            Frame?.Invoke(captured);
        }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        if (!ReferenceEquals(sender, item)) return;
        Dispose();
        Failed?.Invoke("The captured window closed. Choose another source.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            Interlocked.Increment(ref generation);
            if (pool is not null) pool.FrameArrived -= OnFrame;
            if (item is not null) item.Closed -= OnClosed;
            session?.Dispose(); pool?.Dispose(); device?.Dispose();
            session = null; pool = null; device = null; item = null;
            Volatile.Write(ref latest, null);
            WindowHandle = 0;
        }
    }

    public static Task<byte[]> Png(CapturedFrame frame) => Encode(frame, BitmapEncoder.PngEncoderId, null);
    public static Task<byte[]> Jpeg(CapturedFrame frame, double quality) => Encode(frame, BitmapEncoder.JpegEncoderId, quality);

    private static async Task<byte[]> Encode(CapturedFrame frame, Guid encoderId, double? quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = quality is double q ? new[] { new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(q, Windows.Foundation.PropertyType.Single)) } : null;
        var encoder = options is null ? await BitmapEncoder.CreateAsync(encoderId, stream) : await BitmapEncoder.CreateAsync(encoderId, stream, options);
        // Window captures are opaque apart from rounded corners; store them straight so viewers show true colours.
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, quality is null ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, ref Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, ref Guid iid, out nint item);
    }
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string text, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint value, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int type, nint software, uint flags, nint levels, uint levelCount, uint version, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
}
