using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Studio.Host;
using Xunit;

namespace Studio.Host.Tests;

public class WindowSelectionTests
{
    private static AppWindow Window(string title, WindowRole role) => new(title.GetHashCode(), title, 1, "FlyShelf", @"C:\Apps\FlyShelf.exe", role, 900, 650, false);

    [Fact]
    public void A_window_that_appears_in_the_taskbar_is_a_main_window()
    {
        Assert.Equal(WindowRole.Main, WindowRules.Classify(hasOwner: false, toolWindow: false, appWindow: false));
        Assert.Equal(WindowRole.Main, WindowRules.Classify(hasOwner: true, toolWindow: false, appWindow: true));
    }

    [Fact]
    public void Owned_and_tool_windows_such_as_a_floating_shelf_are_floating()
    {
        // WPF gives ShowInTaskbar=False windows a hidden owner ("Hidden Window"); FlyShelf's shelf is one of these.
        Assert.Equal(WindowRole.Floating, WindowRules.Classify(hasOwner: true, toolWindow: false, appWindow: false));
        Assert.Equal(WindowRole.Floating, WindowRules.Classify(hasOwner: false, toolWindow: true, appWindow: false));
    }

    [Theory]
    [InlineData(false, false, "FlyShelf Ecosystem", 900, 650, true, false)] // hidden by the app: never a candidate
    [InlineData(true, true, "FlyShelf Ecosystem", 900, 650, true, false)]   // cloaked (another desktop, suspended)
    [InlineData(true, false, "", 900, 650, true, false)]                    // untitled helper
    [InlineData(true, false, "Hidden Window", 0, 0, true, false)]           // zero-size parking window
    [InlineData(true, false, "FlyShelf", 400, 900, false, false)]           // created off-screen, not minimized
    public void Internal_hidden_and_offscreen_windows_are_never_candidates(bool visible, bool cloaked, string title, int width, int height, bool onMonitor, bool minimized) =>
        Assert.False(WindowRules.Eligible(visible, cloaked, title, width, height, onMonitor, minimized));

    [Fact]
    public void Visible_and_minimized_windows_are_candidates()
    {
        Assert.True(WindowRules.Eligible(true, false, "FlyShelf Ecosystem", 900, 650, true, false));
        Assert.True(WindowRules.Eligible(true, false, "FlyShelf Ecosystem", 900, 650, false, true));
    }

    [Fact]
    public void The_single_main_window_is_chosen_over_a_floating_shelf()
    {
        var hub = Window("FlyShelf Ecosystem", WindowRole.Main);
        Assert.Same(hub, WindowRules.AutoChoice([Window("FlyShelf", WindowRole.Floating), hub]));
    }

    [Fact]
    public void Ambiguous_or_floating_only_windows_are_left_for_the_user_to_choose()
    {
        Assert.Null(WindowRules.AutoChoice([Window("Document 1", WindowRole.Main), Window("Document 2", WindowRole.Main)]));
        Assert.Null(WindowRules.AutoChoice([Window("FlyShelf", WindowRole.Floating)]));
        Assert.Null(WindowRules.AutoChoice([]));
    }

    [Fact]
    public void Reconnecting_prefers_the_window_the_user_was_recording()
    {
        var shelf = Window("FlyShelf", WindowRole.Floating);
        Assert.Same(shelf, WindowRules.AutoChoice([Window("FlyShelf Ecosystem", WindowRole.Main), shelf], preferredTitle: "FlyShelf"));
    }

    [Fact]
    public void Another_copy_of_the_same_app_counts_as_already_running()
    {
        const string product = "FlyShelf — Seamless Cross-Device Clipboard";
        Assert.True(WindowRules.SameApp(@"C:\Apps\FlyShelf\Release\FlyShelf.exe", product, @"D:\Builds\FlyShelf\FlyShelf.exe", product));
        Assert.False(WindowRules.SameApp(@"E:\a\FlyShelf.exe", product, @"E:\b\FlyShelf.exe", "Another product"));
        Assert.False(WindowRules.SameApp(@"E:\a\Tool.exe", null, @"E:\b\Tool.exe", null));
        Assert.False(WindowRules.SameApp(@"E:\a\Studio.exe", product, @"E:\b\FlyShelf.exe", product));
    }

    [Fact]
    public void Paths_compare_case_insensitively_and_reject_missing_values()
    {
        Assert.True(WindowRules.SamePath(@"C:\Apps\FlyShelf.exe", @"c:\apps\FLYSHELF.EXE"));
        Assert.False(WindowRules.SamePath(null, @"C:\Apps\FlyShelf.exe"));
        Assert.False(WindowRules.SamePath(@"C:\Apps\FlyShelf.exe", @"C:\Other\FlyShelf.exe"));
    }
}

public class RecordingTests
{
    [Theory]
    [InlineData(0, 30, 1)]
    [InlineData(1, 30, 31)]
    [InlineData(.999, 60, 60)]
    [InlineData(-1, 30, 0)]
    [InlineData(double.NaN, 30, 0)]
    public void Frames_due_follow_wall_clock_time(double seconds, int fps, long expected) => Assert.Equal(expected, WindowRules.FramesDue(seconds, fps));

    [Fact]
    public void Fit_keeps_aspect_ratio_and_centres()
    {
        Assert.Equal((0, 60, 640, 240), WindowRules.Fit(1600, 600, 640, 360));
        Assert.Equal((160, 0, 320, 360), WindowRules.Fit(800, 900, 640, 360));
        Assert.Equal((0, 0, 0, 0), WindowRules.Fit(0, 600, 640, 360));
    }

    private static CapturedFrame Solid(int width, int height, uint bgra, long sequence)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) BitConverter.TryWriteBytes(pixels.AsSpan(i), bgra);
        return new CapturedFrame(pixels, width, height, 0, sequence);
    }

    [Fact]
    public void Same_size_frames_are_copied_exactly_with_odd_pixels_trimmed()
    {
        var frame = Solid(5, 3, 0xFF112233, 1);
        frame.Pixels[(1 * 5 + 2) * 4] = 0x99;
        var canvas = new byte[4 * 2 * 4];
        Recorder.Compose(frame, canvas, 4, 2);
        Assert.Equal(0x99, canvas[(1 * 4 + 2) * 4]);
        Assert.Equal(0x33, canvas[0]);
    }

    [Fact]
    public void A_window_resized_mid_recording_is_letterboxed_not_stretched()
    {
        var canvas = new byte[8 * 4 * 4];
        Recorder.Compose(Solid(4, 4, 0xFFFFFFFF, 1), canvas, 8, 4);
        Assert.Equal(0, canvas[0]);                       // left bar stays black
        Assert.Equal(0xFF, canvas[(1 * 8 + 4) * 4]);       // centre shows the frame
        Assert.Equal(0, canvas[(1 * 8 + 7) * 4]);          // right bar stays black
    }

    [Fact]
    public async Task A_recording_lasts_exactly_as_long_as_the_real_demonstration()
    {
        var ffmpeg = MediaService.FindTool("ffmpeg");
        var ffprobe = MediaService.FindTool("ffprobe");
        if (ffmpeg is null || ffprobe is null) return; // Requires FFmpeg on PATH; verify.ps1 checks for it.
        var path = Path.Combine(Path.GetTempPath(), $"studio-recorder-test-{Guid.NewGuid():N}.mp4");
        // Windows only delivers frames when pixels change, so the source changes rarely, like a real idle UI.
        var clock = Stopwatch.StartNew();
        CapturedFrame Source() { var step = clock.ElapsedMilliseconds / 300; return Solid(320, 180, step % 2 == 0 ? 0xFF203040u : 0xFFD0E0F0u, step); }
        await using var recorder = new Recorder(ffmpeg, path, Source, 60);
        var wall = Stopwatch.StartNew();
        await Task.Delay(1500);
        var result = await recorder.Finish();
        var elapsed = wall.Elapsed.TotalSeconds;
        try
        {
            var probe = JsonNode.Parse(await MediaService.Run(ffprobe, ["-v", "error", "-count_frames", "-show_entries", "stream=nb_read_frames,r_frame_rate", "-show_entries", "format=duration", "-of", "json", path]))!;
            var seconds = double.Parse(probe["format"]!["duration"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            Assert.Equal("60/1", probe["streams"]![0]!["r_frame_rate"]!.GetValue<string>());
            Assert.InRange(seconds, elapsed - .1, elapsed + .1);
            Assert.Equal(result.Frames, long.Parse(probe["streams"]![0]!["nb_read_frames"]!.GetValue<string>(), CultureInfo.InvariantCulture));
            Assert.InRange(result.UniqueFrames, 4, 7);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_recording_cannot_start_before_the_app_has_produced_a_picture()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new Recorder("ffmpeg", "unused.mp4", () => null, 30));
        Assert.Contains("picture", error.Message);
    }
}

public class ImportTests
{
    [Fact]
    public async Task Audio_files_import_without_video_details()
    {
        var ffmpeg = MediaService.FindTool("ffmpeg");
        if (ffmpeg is null || MediaService.FindTool("ffprobe") is null) return;
        var dir = Directory.CreateTempSubdirectory("studio-import-test-").FullName;
        try
        {
            var wav = Path.Combine(dir, "tone.wav");
            await MediaService.Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", wav]);
            var asset = await new MediaService(dir).Import(wav);
            Assert.Equal("audio", asset["kind"]!.GetValue<string>());
            Assert.InRange(asset["duration"]!.GetValue<double>(), .9, 1.1);
            Assert.Null(asset["width"]);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class CinematicTests
{
    [Theory]
    [InlineData(6, .6, .4)]
    [InlineData(.5, .15, .1)]
    public void Title_fades_fit_inside_short_shots(double duration, double fadeIn, double fadeOut)
    {
        var (i, o) = CinematicComposite.TitleFades(duration);
        Assert.Equal(fadeIn, i, 6);
        Assert.Equal(fadeOut, o, 6);
        Assert.True(i + o <= duration);
    }

    [Fact]
    public void Every_shot_needs_a_title_layer() =>
        Assert.Throws<ArgumentException>(() => CinematicComposite.Arguments("f_%08d.png", 30, ["a.png"], [1, 2], null, 1, "out.mp4"));

    [Fact]
    public async Task Titles_are_composited_over_rendered_frames_for_the_full_duration()
    {
        var ffmpeg = MediaService.FindTool("ffmpeg");
        var ffprobe = MediaService.FindTool("ffprobe");
        if (ffmpeg is null || ffprobe is null) return;
        var dir = Directory.CreateTempSubdirectory("studio-cinematic-test-").FullName;
        try
        {
            // Stand-ins for Blender output (opaque frames) and the editor's transparent title layers.
            await MediaService.Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=0x203040:s=320x180:r=30", "-frames:v", "45", Path.Combine(dir, "frame_%08d.png")]);
            for (var i = 0; i < 2; i++)
                await MediaService.Run(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=white@0.0:s=320x180,format=rgba,drawbox=x=20:y=20:w=120:h=30:color=white@1:t=fill:replace=1", "-frames:v", "1", Path.Combine(dir, $"title_{i:D3}.png")]);
            var output = Path.Combine(dir, "film.mp4");
            await MediaService.Run(ffmpeg, CinematicComposite.Arguments(Path.Combine(dir, "frame_%08d.png"), 30, [Path.Combine(dir, "title_000.png"), Path.Combine(dir, "title_001.png")], [.75, .75], null, 1, output));
            var probe = JsonNode.Parse(await MediaService.Run(ffprobe, ["-v", "error", "-count_frames", "-show_entries", "stream=nb_read_frames", "-show_entries", "format=duration", "-of", "json", output]))!;
            Assert.Equal("45", probe["streams"]![0]!["nb_read_frames"]!.GetValue<string>());
            async Task<byte> Brightness(string at)
            {
                var raw = Path.Combine(dir, $"sample-{at}.raw");
                await MediaService.Run(ffmpeg, ["-v", "error", "-ss", at, "-i", output, "-frames:v", "1", "-vf", "crop=1:1:60:30,format=gray", "-f", "rawvideo", raw]);
                return File.ReadAllBytes(raw)[0];
            }
            Assert.True(await Brightness("0") < 60);    // the title fades in from nothing
            Assert.True(await Brightness("0.4") > 240); // fully visible mid-shot
            Assert.True(await Brightness("1.1") > 240); // the second shot's title follows the cut
        }
        finally { Directory.Delete(dir, true); }
    }
}
