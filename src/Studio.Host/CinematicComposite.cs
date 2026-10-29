using System.Globalization;

namespace Studio.Host;

/// <summary>
/// Builds the FFmpeg command that lays the editor's own title layers over Blender's frames. Rendering titles in the
/// editor keeps typography identical between the studio and cinematic renderers and keeps text out of Blender's depth
/// of field. Fades must match <c>titleOpacity</c> in web/project.ts.
/// </summary>
public static class CinematicComposite
{
    public static (double In, double Out) TitleFades(double duration) => (Math.Min(.6, duration * .3), Math.Min(.4, duration * .2));

    public static List<string> Arguments(string framePattern, int fps, IReadOnlyList<string> titles, IReadOnlyList<double> durations, string? audio, double volume, string output)
    {
        if (titles.Count == 0 || titles.Count != durations.Count) throw new ArgumentException("Every shot needs exactly one title layer.");
        string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        var args = new List<string> { "-v", "error", "-y", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", framePattern };
        foreach (var (title, duration) in titles.Zip(durations))
            args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-t", N(duration), "-i", title]);
        if (audio is not null) args.AddRange(["-i", audio]);
        var filter = new List<string>();
        for (var i = 0; i < titles.Count; i++)
        {
            var (fadeIn, fadeOut) = TitleFades(durations[i]);
            filter.Add($"[{i + 1}:v]format=rgba,fade=t=in:st=0:d={N(fadeIn)}:alpha=1,fade=t=out:st={N(durations[i] - fadeOut)}:d={N(fadeOut)}:alpha=1[t{i}]");
        }
        filter.Add(string.Concat(Enumerable.Range(0, titles.Count).Select(i => $"[t{i}]")) + $"concat=n={titles.Count}:v=1:a=0[titles]");
        filter.Add("[0:v][titles]overlay=eof_action=pass:format=auto,format=yuv420p[video]");
        args.AddRange(["-filter_complex", string.Join(';', filter), "-map", "[video]"]);
        var total = durations.Sum();
        if (audio is not null)
            args.AddRange(["-map", $"{titles.Count + 1}:a:0", "-af", $"volume={N(volume)},apad", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-c:v", "libx264", "-crf", "16", "-preset", "slow", "-r", fps.ToString(CultureInfo.InvariantCulture), "-t", N(total), "-movflags", "+faststart", output]);
        return args;
    }
}
