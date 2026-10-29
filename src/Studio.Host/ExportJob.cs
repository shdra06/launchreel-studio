using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Studio.Host;

public sealed class ExportJob : IAsyncDisposable
{
    private Process? process;
    private Task<string>? errors;
    private Task<string>? output;
    private string? temporary;
    private string? destination;
    private int expected;
    public int Frames { get; private set; }
    public void Begin(MediaService media,JsonObject project,string target,int count)
    {
        if(process is not null)throw new InvalidOperationException("An export is already running.");
        destination=target;temporary=Path.Combine(Path.GetDirectoryName(target)!,"."+Path.GetFileNameWithoutExtension(target)+"."+Guid.NewGuid().ToString("N")+".partial.mp4");expected=count;Frames=0;
        var fps=project["fps"]!.GetValue<int>();var duration=count/(double)fps;
        var args=new List<string>{"-hide_banner","-loglevel","error","-y","-f","image2pipe","-framerate",fps.ToString(),"-i","pipe:0"};
        var audio=project["audioId"]?.GetValue<string>();if(!string.IsNullOrEmpty(audio))args.AddRange(["-i",media.AssetPath(project,audio)]);
        args.AddRange(["-map","0:v:0","-c:v","libx264","-preset","fast","-crf","18","-pix_fmt","yuv420p","-r",fps.ToString()]);
        if(!string.IsNullOrEmpty(audio))args.AddRange(["-map","1:a:0","-af",$"volume={project["audioVolume"]!.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture)},apad","-c:a","aac","-b:a","192k"]);
        args.AddRange(["-t",duration.ToString(System.Globalization.CultureInfo.InvariantCulture),"-movflags","+faststart",temporary]);
        process=MediaService.Start(MediaService.FindTool("ffmpeg")??throw new FileNotFoundException("FFmpeg is required for video export."),args,true);
        errors=process.StandardError.ReadToEndAsync();output=process.StandardOutput.ReadToEndAsync();
    }
    public async Task Append(int index,string base64)
    {
        if(process is null||index!=Frames||Frames>=expected)throw new InvalidOperationException("Unexpected export frame index.");
        if(base64.Length>100_000_000)throw new InvalidDataException("Frame exceeds the memory limit.");
        var bytes=Convert.FromBase64String(base64);if(bytes.Length<8||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("Frame must be a PNG.");
        await process.StandardInput.BaseStream.WriteAsync(bytes);Frames++;
    }
    public async Task<string> Finish()
    {
        if(process is null||Frames!=expected)throw new InvalidOperationException("The export has missing frames.");
        process.StandardInput.Close();await process.WaitForExitAsync();var error=await errors!;await output!;
        if(process.ExitCode!=0)throw new IOException(error.Length>1500?error[^1500..]:error);
        File.Move(temporary!,destination!,true);var result=destination!;process.Dispose();process=null;temporary=null;return result;
    }
    public async ValueTask DisposeAsync()
    {
        if(process is not null){try{process.Kill(true);await process.WaitForExitAsync();}catch{}process.Dispose();process=null;}
        if(temporary is not null&&File.Exists(temporary))File.Delete(temporary);temporary=null;
    }
}
