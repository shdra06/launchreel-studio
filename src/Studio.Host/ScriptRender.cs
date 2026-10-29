using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Studio.Host;

/// <summary>
/// Renders a film from a plan file without the user interface (--render plan.json). The plan is a normal project
/// whose assets name local files ("source"); they are imported like any other media, then every frame is rendered by
/// the same editor and encoder as Export. Progress and the result go to render-log.txt beside the output.
/// </summary>
public partial class MainWindow
{
    private async Task RunScriptRender(string planPath)
    {
        if(testStarted)return;testStarted=true;
        var plan=JsonNode.Parse(await File.ReadAllTextAsync(planPath))!.AsObject();
        var output=Path.GetFullPath(plan["output"]!.GetValue<string>());var logPath=Path.ChangeExtension(output,".render-log.txt");
        async Task Log(string line){Console.WriteLine(line);await File.AppendAllTextAsync(logPath,$"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");}
        var code=1;var clock=Stopwatch.StartNew();
        try
        {
            await WaitForEditor();
            var project=plan["project"]!.AsObject();var ids=new Dictionary<string,string>();
            foreach(var asset in project["assets"]!.AsArray().Select(a=>a!.AsObject()).ToList())
            {
                var source=asset["source"]?.GetValue<string>();if(source is null)continue;
                var imported=await media.Import(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(planPath)!,source)),asset["name"]?.GetValue<string>());
                ids[asset["id"]!.GetValue<string>()]=imported["id"]!.GetValue<string>();
                foreach(var (key,value) in imported.ToList())asset[key]=value?.DeepClone();asset.Remove("source");
                if(imported["kind"]!.GetValue<string>()=="image"&&asset["width"] is null){var size=System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(media.Assets,imported["file"]!.GetValue<string>())),System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,System.Windows.Media.Imaging.BitmapCacheOption.None);asset["width"]=size.PixelWidth;asset["height"]=size.PixelHeight;}
            }
            string Map(string id)=>ids.GetValueOrDefault(id,id);
            if(project["audioId"] is JsonValue audio)project["audioId"]=Map(audio.GetValue<string>());
            // A plan carries a storyboard (as an AI script would) or finished shots.
            var story=plan["storyboard"]?.AsObject()??new JsonObject{["version"]=1,["shots"]=project["shots"]!.DeepClone()};
            foreach(var shot in story["shots"]!.AsArray()){if(shot!["assetId"] is JsonValue a)shot["assetId"]=Map(a.GetValue<string>());foreach(var layer in (shot["layers"] as JsonArray)??[])if(layer!["assetId"] is JsonValue l)layer["assetId"]=Map(l.GetValue<string>());}
            var settings=new JsonObject{["name"]=project["name"]?.DeepClone(),["width"]=project["width"]?.DeepClone(),["height"]=project["height"]?.DeepClone(),["fps"]=project["fps"]?.DeepClone(),["audioVolume"]=project["audioVolume"]?.DeepClone()??.8,["assets"]=project["assets"]!.DeepClone(),["audioId"]=project["audioId"]?.DeepClone()};
            var error=await Browser.ExecuteScriptAsync("(()=>{try{const plan="+settings.ToJsonString()+";const base=window.studioTest.getProject();const s=base.shots[0];delete s.assetId;delete s.crop;s.layers=[];s.callouts=[];s.highlights=[];base.shots=[s];base.name=plan.name;base.width=plan.width;base.height=plan.height;base.fps=plan.fps;base.audioVolume=plan.audioVolume;base.assets=plan.assets;if(plan.audioId)base.audioId=plan.audioId;else delete base.audioId;window.studioTest.setProject(base);window.studioTest.applyScript("+story.ToJsonString()+");return '';}catch(e){return e.message;}})()");
            if(JsonSerializer.Deserialize<string>(error) is {Length:>0} message)throw new InvalidDataException("The plan is not valid: "+message);
            // The editor validates and fills defaults; render exactly what it accepted.
            project=await EditorProject();
            int width=project["width"]!.GetValue<int>(),height=project["height"]!.GetValue<int>(),fps=project["fps"]!.GetValue<int>();
            var count=(int)Math.Ceiling(project["shots"]!.AsArray().Sum(s=>s!["duration"]!.GetValue<double>())*fps-1e-6);
            await Log($"Rendering {count} frames at {width}x{height}, {fps} fps -> {output}");
            await Browser.ExecuteScriptAsync($"window.studioTest.setExporting(true);window.studioTest.scene.resize({width},{height})");
            var cache=Guid.NewGuid().ToString("N");var decoded=await media.DecodeVideos(project,cache,fps,CancellationToken.None);
            try
            {
                // "stills": [seconds…] renders review frames instead of the film.
                if(plan["stills"] is JsonArray stills)
                {
                    foreach(var at in stills.Select(n=>n!.GetValue<double>()))await File.WriteAllBytesAsync(Path.ChangeExtension(output,null)+$"-{at:00.00}s.png".Replace(',','.'),Convert.FromBase64String(await RenderTestFrame(at,decoded)));
                    await Log($"Rendered {stills.Count} stills");code=0;return;
                }
                await using var job=new ExportJob();job.Begin(media,project,output,count);
                for(var i=0;i<count;i++){await job.Append(i,await RenderTestFrame(i/(double)fps,decoded));if(i%fps==0)await Log($"frame {i}/{count}");}
                await job.Finish();
            }
            finally{var dir=Path.Combine(media.Cache,cache);if(Directory.Exists(dir))Directory.Delete(dir,true);}
            await Log($"Done in {clock.Elapsed.TotalSeconds:0} s: {output}");code=0;
        }
        catch(Exception error){await Log("Failed: "+error);}
        finally{Application.Current.Shutdown(code);}
    }
}
