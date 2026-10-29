using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Studio.Host;

public sealed class MediaService(string root)
{
    public string Root { get; } = root;
    public string Assets => Path.Combine(Root, "assets");
    public string Cache => Path.Combine(Root, "cache");
    public string? BlenderPath { get; set; }
    public static string? FindTool(string name)
    {
        var file = name + ".exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        { var path = Path.Combine(dir.Trim('"'), file); if (File.Exists(path)) return path; }
        if (name == "blender")
        {
            var local = Path.Combine(AppContext.BaseDirectory, "Tools", "Blender", file); if (File.Exists(local)) return local;
            // The workspace keeps a portable Blender in tools\blender, two levels above artifacts\studio.
            foreach (var parent in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","tools","blender")), Path.Combine(Environment.CurrentDirectory,"tools","blender") })
                if (Directory.Exists(parent)) { var candidate = Directory.EnumerateFiles(parent, file, SearchOption.AllDirectories).FirstOrDefault(); if(candidate is not null)return candidate; }
        }
        return null;
    }
    /// <summary>Asset IDs name cache folders: letters, digits, '-' and '_' only.</summary>
    public static bool SafeId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static Process Start(string file, IEnumerable<string> args, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = redirectInput, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException($"Could not start {Path.GetFileName(file)}.");
    }
    public static async Task<string> Run(string file, IEnumerable<string> args, CancellationToken token = default)
    {
        using var p = Start(file,args);using var registration=token.Register(()=>{try{p.Kill(true);}catch{}});
        var output = p.StandardOutput.ReadToEndAsync(token);var error = p.StandardError.ReadToEndAsync(token);
        await p.WaitForExitAsync(token);var result = await output;var err = await error;if(p.ExitCode!=0)throw new IOException(err.Length>1500?err[^1500..]:err);return result;
    }
    /// <summary>Copies media into the managed asset folder. Files Studio itself wrote there are adopted in place.</summary>
    public async Task<JsonObject> Import(string path,string? name=null)
    {
        var ext=Path.GetExtension(path).ToLowerInvariant();var kind=ext switch {".png" or ".jpg" or ".jpeg" or ".webp"=>"image",".mp4" or ".mov" or ".mkv" or ".webm"=>"video",".wav" or ".mp3" or ".m4a" or ".ogg"=>"audio",".glb"=>"model",_=>throw new InvalidDataException("Unsupported media format.")};
        if(new FileInfo(path).Length>4L*1024*1024*1024)throw new InvalidDataException("Import files smaller than 4 GB.");
        Directory.CreateDirectory(Assets);string id,file,target;
        if(string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),Path.GetFullPath(Assets),StringComparison.OrdinalIgnoreCase)&&Guid.TryParse(Path.GetFileNameWithoutExtension(path),out var existing)){id=existing.ToString();file=Path.GetFileName(path);target=path;}
        else{id=Guid.NewGuid().ToString();file=id+ext;target=Path.Combine(Assets,file);await Task.Run(()=>File.Copy(path,target));}
        var asset=new JsonObject{["id"]=id,["name"]=name??Path.GetFileName(path),["kind"]=kind,["file"]=file,["url"]="https://assets.studio.local/"+file};
        if(kind is "video" or "audio")
        {
            var probe=FindTool("ffprobe");
            if(probe is not null)
            {
                var info=JsonNode.Parse(await Run(probe,["-v","error","-select_streams","v:0","-show_entries","format=duration:stream=width,height","-of","json",target]));
                if(double.TryParse(info?["format"]?["duration"]?.GetValue<string>(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds))asset["duration"]=seconds;
                if(info?["streams"] is JsonArray {Count:>0} streams&&streams[0] is JsonObject stream&&stream["width"]?.GetValue<int>() is int w&&stream["height"]?.GetValue<int>() is int h&&w>0&&h>0){asset["width"]=w;asset["height"]=h;}
            }
        }
        return asset;
    }
    public string AssetPath(JsonObject project,string id)
    {
        var asset=(project["assets"] as JsonArray)?.FirstOrDefault(a=>a?["id"]?.GetValue<string>()==id)??throw new InvalidDataException("Missing asset.");
        var file=asset["file"]?.GetValue<string>()??"";if(file!=Path.GetFileName(file)||string.IsNullOrWhiteSpace(file))throw new InvalidDataException("Invalid asset path.");
        var path=Path.Combine(Assets,file);if(!File.Exists(path))throw new FileNotFoundException("Asset is unavailable. Relink or import it again.",path);return path;
    }
    /// <summary>Rough disk space for the PNG frames DecodeVideos writes (PNG is about a third of raw BGRA for UI footage).</summary>
    public long DecodedSizeEstimate(JsonObject project,int fps)
    {
        long total=0;var shots=((project["shots"] as JsonArray)??[]).ToList();
        foreach(var node in (project["assets"] as JsonArray)??[])
        {
            if(node?["kind"]?.GetValue<string>()!="video")continue;var id=node["id"]!.GetValue<string>();if(!SafeId(id))throw new InvalidDataException("Invalid asset id.");
            var used=shots.Where(s=>s?["assetId"]?.GetValue<string>()==id||(s?["layers"] as JsonArray)?.Any(l=>l?["assetId"]?.GetValue<string>()==id)==true).ToList();if(used.Count==0)continue;
            var seconds=used.Max(s=>(s?["sourceStart"]?.GetValue<double>()??0)+(s?["duration"]?.GetValue<double>()??0));
            var pixels=(long)(node["width"]?.GetValue<int>()??1920)*(node["height"]?.GetValue<int>()??1080);
            total+=(long)(pixels*4/3*Math.Ceiling(seconds*fps));
        }
        return total;
    }
    public async Task<JsonObject> DecodeVideos(JsonObject project,string job,int fps,CancellationToken token)
    {
        var result=new JsonObject();foreach(var node in (project["assets"] as JsonArray)??[])
        {
            if(node?["kind"]?.GetValue<string>()!="video")continue;var id=node["id"]!.GetValue<string>();if(!SafeId(id))throw new InvalidDataException("Invalid asset id.");
            var used=((project["shots"] as JsonArray)??[]).Where(s=>s?["assetId"]?.GetValue<string>()==id||(s?["layers"] as JsonArray)?.Any(l=>l?["assetId"]?.GetValue<string>()==id)==true).ToList();if(used.Count==0)continue;
            var end=used.Max(s=>(s?["sourceStart"]?.GetValue<double>()??0)+(s?["duration"]?.GetValue<double>()??0));
            var folder=Path.Combine(Cache,job,id);Directory.CreateDirectory(folder);
            await Run(FindTool("ffmpeg")??throw new FileNotFoundException("Install FFmpeg to export video."),["-v","error","-i",AssetPath(project,id),"-t",end.ToString(System.Globalization.CultureInfo.InvariantCulture),"-vf",$"fps={fps}","-vsync","0",Path.Combine(folder,"frame_%08d.png")],token);
            var count=Directory.EnumerateFiles(folder,"frame_*.png").Count();if(count==0)throw new InvalidDataException("Video contains no readable frames.");result[id]=new JsonObject{["url"]=$"https://cache.studio.local/{job}/{id}",["fps"]=fps,["count"]=count};
        }return result;
    }
}
