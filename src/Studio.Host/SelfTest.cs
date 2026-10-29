using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace Studio.Host;

/// <summary>
/// Automated capture → record → render → encode check. Runs only with --self-test and only against the test fixture
/// passed on the command line; it never touches a real application.
/// </summary>
public partial class MainWindow
{
    private bool testStarted;
    private async Task RunSelfTest(string fixture)
    {
        if(testStarted)return;testStarted=true;var report=new JsonObject();var proof=Path.Combine(root,"proof");Directory.CreateDirectory(proof);int fixturePid=0;var timer=Stopwatch.StartNew();
        try
        {
            await WaitForEditor();report["webLoaded"]=true;
            var connected=await LaunchFixture(fixture);fixturePid=connected["pid"]!.GetValue<int>();
            if(connected["role"]?.GetValue<string>()!="main")throw new IOException("The fixture's main window was not recognised as a main window.");
            for(var i=0;i<80&&capture.Latest is null;i++)await Task.Delay(100);
            var frame=capture.Latest??throw new IOException("Windows capture produced no frame.");await File.WriteAllBytesAsync(Path.Combine(proof,"captured-app.png"),await NativeCapture.Png(frame));report["capture"]=new JsonObject{["width"]=frame.Width,["height"]=frame.Height};
            if(bridgeToken is null)throw new IOException("The development bridge did not connect to the fixture.");
            var layer=await Bridge(new JsonObject{["action"]="frame",["elementId"]="panel"}) as JsonNode??throw new IOException("No layer frame.");await File.WriteAllBytesAsync(Path.Combine(proof,"live-layer.png"),Convert.FromBase64String(layer["png"]!.GetValue<string>()));report["liveLayer"]=true;
            await Bridge(new JsonObject{["action"]="pointerDown",["elementId"]="field",["x"]=20,["y"]=15});
            await Bridge(new JsonObject{["action"]="key",["key"]="i"});await Bridge(new JsonObject{["action"]="key",["key"]="d"});report["liveTextInput"]=true;

            // Wall-clock recording: 2.5 s of real time must produce 2.5 s of video at the requested rate.
            StartRecording(60);var recordClock=Stopwatch.StartNew();await Task.Delay(2500);var recordedSeconds=recordClock.Elapsed.TotalSeconds;
            var recording=JsonSerializer.SerializeToNode(await StopRecording())!.AsObject();
            var probe=JsonNode.Parse(await MediaService.Run(MediaService.FindTool("ffprobe")!,["-v","error","-count_frames","-show_entries","stream=width,height,nb_read_frames,r_frame_rate","-show_entries","format=duration","-of","json",media.AssetPath(new JsonObject{["assets"]=new JsonArray(recording.DeepClone())},recording["id"]!.GetValue<string>())]))!;
            var videoSeconds=double.Parse(probe["format"]!["duration"]!.GetValue<string>(),System.Globalization.CultureInfo.InvariantCulture);
            report["recording"]=new JsonObject{["wallSeconds"]=recordedSeconds,["videoSeconds"]=videoSeconds,["frames"]=recording["recordedFrames"]!.DeepClone(),["uniqueFrames"]=recording["uniqueFrames"]!.DeepClone(),["probe"]=probe.DeepClone()};
            if(probe["streams"]![0]!["r_frame_rate"]!.GetValue<string>()!="60/1")throw new IOException("Recording is not 60 fps.");
            if(Math.Abs(videoSeconds-recordedSeconds)>.25)throw new IOException($"Recording lasted {videoSeconds:0.00} s for {recordedSeconds:0.00} s of real time.");
            if(recording["uniqueFrames"]!.GetValue<long>()<3)throw new IOException("The recording did not follow the app's changing picture.");

            var still=JsonSerializer.SerializeToNode(await Handle("capture.snapshot",new JsonObject()))!;
            var project=await EditorProject();
            project["width"]=640;project["height"]=360;project["assets"]=new JsonArray(still.DeepClone(),recording.DeepClone());
            var shots=project["shots"]!.AsArray();while(shots.Count>2)shots.RemoveAt(shots.Count-1);
            foreach(var shot in shots){shot!["duration"]=.75;shot["camera"]![1]!["time"]=.75;}
            shots[0]!["assetId"]=still["id"]!.GetValue<string>();shots[1]!["assetId"]=recording["id"]!.GetValue<string>();shots[1]!["sourceStart"]=.5;
            await Browser.ExecuteScriptAsync("window.studioTest.setProject("+project.ToJsonString()+");window.studioTest.setExporting(true);window.studioTest.scene.resize(640,360)");
            var cache=Guid.NewGuid().ToString("N");var decoded=await media.DecodeVideos(project,cache,30,CancellationToken.None);
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(proof,"studio-frame.png"),Convert.FromBase64String(await RenderTestFrame(0,decoded)));
                await using var job=new ExportJob();var count=45;job.Begin(media,project,Path.Combine(proof,"studio-proof.mp4"),count);
                for(var i=0;i<count;i++)await job.Append(i,await RenderTestFrame(i/30d,decoded));await job.Finish();
            }
            finally{Directory.Delete(Path.Combine(media.Cache,cache),true);}
            report["video"]=JsonNode.Parse(await MediaService.Run(MediaService.FindTool("ffprobe")!,["-v","error","-show_entries","stream=width,height,nb_frames,r_frame_rate","-show_entries","format=duration","-of","json",Path.Combine(proof,"studio-proof.mp4")]));
            // Visual showcase of devices, stages, sections, callouts and focus, rendered from the fixture's real capture.
            var id=still["id"]!.GetValue<string>();
            var showcase=new JsonObject{["version"]=1,["shots"]=new JsonArray(
                new JsonObject{["assetId"]=id,["duration"]=4,["device"]="laptop",["stage"]="workspace",["motion"]="reveal",["title"]="Your desk.\nYour real app.",["subtitle"]="PC workspace"},
                new JsonObject{["assetId"]=id,["duration"]=4,["device"]="none",["stage"]="gradient",["motion"]="orbit",["explode"]=.9,["title"]="Every section, lifted.",["subtitle"]="Exploded UI",["titleLayout"]="bottom-left",
                    ["sections"]=new JsonArray(new JsonObject{["name"]="Header",["rect"]=new JsonArray(0,0,1,.16)},new JsonObject{["name"]="Panel",["rect"]=new JsonArray(.03,.36,.94,.5)}),
                    ["callouts"]=new JsonArray(new JsonObject{["text"]="Real controls",["x"]=.2,["y"]=.92,["delay"]=.3}),["highlights"]=new JsonArray(new JsonObject{["rect"]=new JsonArray(.02,.2,.96,.13),["start"]=.5,["end"]=3.8,["style"]="spotlight"})},
                new JsonObject{["assetId"]=id,["duration"]=4,["device"]="phone",["stage"]="void",["lighting"]="neon",["motion"]="reveal",["title"]="Anywhere.",["titleLayout"]="center"},
                new JsonObject{["assetId"]=id,["duration"]=4,["device"]="desktop",["stage"]="studio",["lighting"]="soft",["finish"]="graphite",["motion"]="push",["turn"]=-18,["title"]="Look closer.",["highlights"]=new JsonArray(new JsonObject{["rect"]=new JsonArray(.02,.2,.96,.13),["start"]=.5,["end"]=3.8,["style"]="outline"})})};
            await Browser.ExecuteScriptAsync("window.studioTest.applyScript("+showcase.ToJsonString()+");window.studioTest.setExporting(true);window.studioTest.scene.resize(1280,720)");
            for(var i=0;i<4;i++)await File.WriteAllBytesAsync(Path.Combine(proof,$"showcase-{i+1}.png"),Convert.FromBase64String(await RenderTestFrame(i*4+2.6)));
            report["showcase"]=4;
            await Browser.ExecuteScriptAsync("window.studioTest.setExporting(false);window.dispatchEvent(new Event('resize'))");
            using(var stream=File.Create(Path.Combine(proof,"editor.png")))await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
            await Handle("capture.stop",new JsonObject());
            using(var source=Process.GetProcessById(fixturePid))if(source.HasExited)throw new IOException("Disconnecting closed the app.");
            report["appStillRunningAfterDisconnect"]=true;
            report["elapsedSeconds"]=timer.Elapsed.TotalSeconds;report["ok"]=true;
        }
        catch(Exception error){report["ok"]=false;report["error"]=error.ToString();}
        finally{await FinishTest(proof,report,fixture,fixturePid);}
    }
    private async Task WaitForEditor()
    {
        for(var i=0;i<100;i++){if(await Browser.ExecuteScriptAsync("!!window.studioTest")=="true")return;await Task.Delay(100);}
        throw new TimeoutException("The editor did not load.");
    }
    private async Task<JsonObject> LaunchFixture(string fixture)
    {
        if(!File.Exists(fixture))throw new FileNotFoundException("Build the test fixture first (scripts/verify.ps1).",fixture);
        if(AppWindows.IsRunning(fixture))throw new InvalidOperationException("A previous test fixture is still running. Close it first.");
        var result=JsonSerializer.SerializeToNode(await LaunchApplication(fixture))!.AsObject();
        if(result["state"]?.GetValue<string>()!="connected")throw new IOException("The fixture's window was not connected automatically: "+result.ToJsonString());
        return result;
    }
    private async Task<JsonObject> EditorProject()
    {
        var encoded=await Browser.ExecuteScriptAsync("JSON.stringify(window.studioTest.getProject())");return JsonNode.Parse(JsonSerializer.Deserialize<string>(encoded)!)!.AsObject();
    }
    private async Task FinishTest(string proof,JsonObject report,string fixture,int fixturePid)
    {
        try{ReleaseSource();}catch(Exception error){report["restoreError"]=error.Message;report["ok"]=false;}
        // Only the fixture this test started is closed, and only after checking it really is the fixture.
        foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fixture))){using(p){try{if(WindowRules.SamePath(AppWindows.ProcessPath(p.Id),fixture))p.Kill();}catch(InvalidOperationException){}}}
        await File.WriteAllTextAsync(Path.Combine(proof,"report.json"),report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        System.Windows.Application.Current.Shutdown(report["ok"]?.GetValue<bool>()==true?0:1);
    }
    private async Task<string> RenderTestFrame(double time,JsonObject? decoded=null)
    {
        // ExecuteScriptAsync does not unwrap promises. Run the work explicitly and poll a bounded result slot.
        await Browser.ExecuteScriptAsync($"window.__studioFrame=null;window.__studioFrameError=null;window.studioTest.renderAt({time.ToString(System.Globalization.CultureInfo.InvariantCulture)},{decoded?.ToJsonString()??"undefined"}).then(p=>window.__studioFrame=p).catch(e=>window.__studioFrameError=e.message)");
        for(var i=0;i<400;i++){var value=await Browser.ExecuteScriptAsync("JSON.stringify({png:window.__studioFrame,error:window.__studioFrameError})");var result=JsonNode.Parse(JsonSerializer.Deserialize<string>(value)!)!;if(result["error"] is not null)throw new IOException(result["error"]!.GetValue<string>());if(result["png"] is not null)return result["png"]!.GetValue<string>();await Task.Delay(25);}
        throw new TimeoutException("The frame renderer timed out.");
    }
}
