using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Studio.Host;

public sealed record RecordingResult(string Path, long Frames, long UniqueFrames, int Fps, int Width, int Height, double Seconds);

/// <summary>
/// Records the latest captured window image at a constant frame rate. Frames are scheduled from wall-clock time, so
/// the recording always lasts as long as the real demonstration, even though Windows only delivers frames when the
/// window changes. Unchanged moments become repeated frames; the result reports how many frames were unique.
/// </summary>
public sealed class Recorder : IAsyncDisposable
{
    private readonly Process ffmpeg;
    private readonly Task<string> errors;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly Func<CapturedFrame?> source;
    private readonly byte[] canvas;
    private readonly Stopwatch clock = new();
    private long frames, unique, lastSequence = -1;
    private bool finished;

    public string Path { get; }
    public int Fps { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Set when encoding stopped unexpectedly, for example because the disk is full.</summary>
    public string? Error { get; private set; }
    public event Action<string>? Failed;

    public Recorder(string ffmpegPath, string path, Func<CapturedFrame?> source, int fps)
    {
        var first = source() ?? throw new InvalidOperationException("The app has not produced a picture yet. Wait a moment and try again.");
        if (fps is not (30 or 60)) throw new ArgumentOutOfRangeException(nameof(fps));
        Path = path; Fps = fps; this.source = source;
        Width = Math.Max(2, first.Width / 2 * 2);
        Height = Math.Max(2, first.Height / 2 * 2);
        canvas = new byte[Width * Height * 4];
        ffmpeg = MediaService.Start(ffmpegPath, ["-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Width}x{Height}", "-framerate", fps.ToString(), "-i", "pipe:0",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-pix_fmt", "yuv420p", "-movflags", "+faststart", path], true);
        errors = ffmpeg.StandardError.ReadToEndAsync();
        _ = ffmpeg.StandardOutput.ReadToEndAsync();
        loop = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
    }

    public double Seconds => frames / (double)Fps;

    private void Run()
    {
        var input = ffmpeg.StandardInput.BaseStream;
        TimeBeginPeriod(1);
        try
        {
            clock.Start();
            while (!stop.IsCancellationRequested)
            {
                var due = WindowRules.FramesDue(clock.Elapsed.TotalSeconds, Fps);
                if (frames >= due)
                {
                    var next = frames / (double)Fps - clock.Elapsed.TotalSeconds;
                    if (next > .002) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(next, .05)));
                    continue;
                }
                var frame = source();
                if (frame is not null && frame.Sequence != lastSequence)
                {
                    Compose(frame, canvas, Width, Height);
                    lastSequence = frame.Sequence;
                    unique++;
                }
                input.Write(canvas);
                frames++;
            }
        }
        catch (Exception error)
        {
            Error = error is IOException or ObjectDisposedException ? "Recording stopped because the encoder closed unexpectedly. " + error.Message : "Recording stopped: " + error.Message;
            Failed?.Invoke(Error);
        }
        finally
        {
            TimeEndPeriod(1);
            try { input.Close(); } catch { }
        }
    }

    public async Task<RecordingResult> Finish()
    {
        if (finished) throw new InvalidOperationException("This recording has already finished.");
        finished = true;
        stop.Cancel();
        await loop;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await ffmpeg.WaitForExitAsync(timeout.Token);
        var error = await errors;
        if (ffmpeg.ExitCode != 0) throw new IOException("The recording could not be encoded. " + (error.Length > 1500 ? error[^1500..] : error));
        if (frames == 0) throw new InvalidOperationException("The recording is empty.");
        return new RecordingResult(Path, frames, unique, Fps, Width, Height, Seconds);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await loop; } catch { }
        if (!ffmpeg.HasExited) { try { ffmpeg.Kill(true); await ffmpeg.WaitForExitAsync(); } catch { } }
        ffmpeg.Dispose();
        stop.Dispose();
    }

    /// <summary>
    /// Copies a frame into the fixed recording size. Same-size frames (allowing the odd pixel trimmed for the encoder)
    /// are copied exactly; if the window was resized mid-recording, the frame is letterboxed without distortion.
    /// </summary>
    internal static void Compose(CapturedFrame frame, byte[] canvas, int width, int height)
    {
        var rowBytes = width * 4;
        if (frame.Width >= width && frame.Height >= height && frame.Width - width <= 1 && frame.Height - height <= 1)
        {
            for (var y = 0; y < height; y++) Buffer.BlockCopy(frame.Pixels, y * frame.Width * 4, canvas, y * rowBytes, rowBytes);
            return;
        }
        Array.Clear(canvas);
        var (ox, oy, w, h) = WindowRules.Fit(frame.Width, frame.Height, width, height);
        var source = MemoryMarshal.Cast<byte, uint>(frame.Pixels);
        var target = MemoryMarshal.Cast<byte, uint>(canvas.AsSpan());
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Min(frame.Height - 1, (int)((y + .5) * frame.Height / h));
            var row = (oy + y) * width + ox;
            for (var x = 0; x < w; x++) target[row + x] = source[sy * frame.Width + Math.Min(frame.Width - 1, (int)((x + .5) * frame.Width / w))];
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);
}
