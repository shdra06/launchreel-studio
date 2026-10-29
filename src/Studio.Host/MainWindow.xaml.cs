using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Studio.Host;

public partial class MainWindow : Window
{
    private readonly string root;
    private readonly MediaService media;
    private readonly NativeCapture capture=new();
    private readonly Dictionary<int,string> tokens=[];
    private readonly DispatcherTimer preview=new(){Interval=TimeSpan.FromMilliseconds(66)};
    private readonly string? selfTest,appViewTest,renderPlan,mirrorTest;
    private JsonObject? autosave;
    private ExportJob? export;
    private CancellationTokenSource? exportCancellation;
    private string? exportCache;
    private Recorder? recorder;
    private int sourcePid;
    private string? bridgeToken;
    private string? bridgePipe;
    private bool closing,previewBusy;
    private long previewSequence=-1;

    public MainWindow()
    {
        InitializeComponent();var args=Environment.GetCommandLineArgs();
        string? Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        root=App.DataRoot(args);
        selfTest=Option("--self-test");appViewTest=Option("--app-view-test");renderPlan=Option("--render");mirrorTest=Option("--mirror-test");
        if(selfTest is not null||appViewTest is not null||renderPlan is not null||mirrorTest is not null)PrepareTestWindow();
        media=new MediaService(root);Directory.CreateDirectory(root);Directory.CreateDirectory(media.Assets);Directory.CreateDirectory(media.Cache);LoadToolPaths();
        if(renderPlan is null)_=Task.Run(()=>{foreach(var stale in Directory.EnumerateDirectories(media.Cache)){try{Directory.Delete(stale,true);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}}});
        InitializeAppWorkspace();
        // Studio stays up through its own mistakes: UI errors are logged and shown, background errors are logged,
        // and only a truly fatal error ends Studio, after handing the user's app back to its own place first.
        Application.Current.DispatcherUnhandledException+=(_,e)=>{CrashLog.Write(root,e.Exception,"ui");e.Handled=true;try{Event("studio.error",new{message="Something went wrong, but Studio kept running: "+e.Exception.Message});}catch{}};
        TaskScheduler.UnobservedTaskException+=(_,e)=>{CrashLog.Write(root,e.Exception,"background");e.SetObserved();};
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{CrashLog.Write(root,e.ExceptionObject as Exception,"fatal");ReleaseQuietly();};
        capture.Failed+=message=>Dispatcher.BeginInvoke(()=>{if(!closing)Event("capture.error",new{message});});
        preview.Tick+=(_,_)=>SendPreview();preview.Start();
        Loaded+=async (_,_)=>{try{await Initialize();}catch(Exception e){MessageBox.Show(e.ToString(),"Studio startup error");}};
        Closing+=(_,_)=>
        {
            // Closing always succeeds. The app is handed back first and is never terminated.
            closing=true;StopWaiting();
            try{ReleaseSource();}catch(Exception error){Trace.WriteLine(error);}
            appWatch?.Stop();preview.Stop();recordingFailed=null;exportCancellation?.Cancel();
            if(recorder is not null){var r=recorder;recorder=null;_=r.DisposeAsync().AsTask().ContinueWith(_=>{try{File.Delete(r.Path);}catch{}});}
            if(export is not null)_=export.DisposeAsync();
        };
    }
    private void PrepareTestWindow()
    {
        // Automated checks never take focus and stay behind every other window, so they do not interrupt someone
        // using the desktop. They must stay on a monitor: Windows stops delivering capture frames for windows that
        // are entirely off-screen, which would make the recording checks meaningless.
        WindowState=WindowState.Normal;WindowStartupLocation=WindowStartupLocation.Manual;ShowActivated=false;ShowInTaskbar=false;Width=1440;Height=900;
        Left=SystemParameters.WorkArea.Left;Top=SystemParameters.WorkArea.Top;
        SourceInitialized+=(_,_)=>SendToBack();ContentRendered+=(_,_)=>SendToBack();
    }
    private void SendToBack()=>TestPlaceBottom(new System.Windows.Interop.WindowInteropHelper(this).Handle,1,0,0,0,0,0x0013);
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SetWindowPos")]private static extern bool TestPlaceBottom(nint window,nint after,int x,int y,int width,int height,uint flags);
    private async Task Initialize()
    {
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"browser"));await Browser.EnsureCoreWebView2Async(environment);
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("studio.local",Path.Combine(AppContext.BaseDirectory,"Web"),CoreWebView2HostResourceAccessKind.DenyCors);
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("assets.studio.local",media.Assets,CoreWebView2HostResourceAccessKind.Allow);
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("cache.studio.local",media.Cache,CoreWebView2HostResourceAccessKind.Allow);
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled=false;Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled=false;Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled=false;
        Browser.CoreWebView2.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://studio.local/",StringComparison.Ordinal))e.Cancel=true;};
        Browser.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
        Browser.CoreWebView2.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        Browser.CoreWebView2.WebMessageReceived+=OnMessage;
        Browser.CoreWebView2.ProcessFailed+=(_,e)=>{var kind=e.ProcessFailedKind;Dispatcher.BeginInvoke(()=>RecoverEditor(kind));};
        if(selfTest is not null)Browser.CoreWebView2.NavigationCompleted+=async(_,e)=>{if(e.IsSuccess)await RunSelfTest(selfTest);};
        if(appViewTest is not null)Browser.CoreWebView2.NavigationCompleted+=async(_,e)=>{if(e.IsSuccess)await RunAppViewTest(appViewTest);};
        if(mirrorTest is not null)Browser.CoreWebView2.NavigationCompleted+=async(_,e)=>{if(e.IsSuccess)await RunMirrorTest(mirrorTest);};
        if(renderPlan is not null)Browser.CoreWebView2.NavigationCompleted+=async(_,e)=>{if(e.IsSuccess)await RunScriptRender(Path.GetFullPath(renderPlan));};
        if(Environment.GetCommandLineArgs() is var probeArgs&&Array.IndexOf(probeArgs,"--probe-app") is var pi&&pi>=0&&pi+1<probeArgs.Length)Browser.CoreWebView2.NavigationCompleted+=async(_,e)=>{if(e.IsSuccess)await RunAppProbe(probeArgs[pi+1]);};
        Browser.Source=new Uri("https://studio.local/index.html");
    }
    private readonly Queue<DateTime> recoveries=new();
    /// <summary>The editor runs in WebView2 child processes. If one dies, reload or rebuild it; the project autosave and the app connection survive.</summary>
    private async void RecoverEditor(CoreWebView2ProcessFailedKind kind)
    {
        CrashLog.Write(root,null,"editor process failed: "+kind);
        if(closing||kind is CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)return;
        while(recoveries.Count>0&&DateTime.UtcNow-recoveries.Peek()>TimeSpan.FromMinutes(2))recoveries.Dequeue();
        recoveries.Enqueue(DateTime.UtcNow);
        if(recoveries.Count>3){MessageBox.Show(this,"The editor keeps stopping. Your project is autosaved and your app is untouched. Please restart Studio.","LaunchReel Studio");return;}
        try
        {
            if(kind==CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                var old=Browser;var fresh=new Microsoft.Web.WebView2.Wpf.WebView2();Content=fresh;Browser=fresh;old.Dispose();await Initialize();
            }
            else Browser.CoreWebView2?.Reload();
        }
        catch(Exception error){CrashLog.Write(root,error,"editor recovery");}
    }
    private async void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(!e.Source.StartsWith("https://studio.local/",StringComparison.Ordinal))return;
        string? id=null;
        try
        {
            var request=JsonNode.Parse(e.WebMessageAsJson) as JsonObject??throw new InvalidDataException("Invalid request.");id=request["id"]?.GetValue<string>();
            var method=request["method"]?.GetValue<string>()??"";var data=request["data"] as JsonObject??new JsonObject();var result=await Handle(method,data);Reply(id,true,result);
        }
        catch(Exception error){Reply(id,false,new{message=error.Message});}
    }
    private void Reply(string? id,bool ok,object? data){if(!closing)Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok,data}));}
    private void Event(string name,object? data){if(!closing)Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="event",name,data}));}
    /// <summary>Live preview for the 3D view. Skipped in App view, where the real window is on screen instead.</summary>
    private void SendPreview()
    {
        // Copy only as many frames from the GPU as are needed: recording rate while recording, a smooth preview in
        // 3D view, and just enough for instant stills while the user operates the app in App view.
        capture.MaxRate=recorder is not null?recorder.Fps:appView?6:24;
        if(previewBusy||appView||closing||Browser.CoreWebView2 is null)return;var frame=SourceFrame();if(frame is null||frame.Sequence==previewSequence)return;
        previewBusy=true;previewSequence=frame.Sequence;
        _=Task.Run(async()=>{try{var jpeg=await NativeCapture.Jpeg(frame,.92);await Dispatcher.InvokeAsync(()=>{if(SourceFrame() is not null)Event("capture.frame",new{jpeg=Convert.ToBase64String(jpeg),width=frame.Width,height=frame.Height});});}catch(Exception error){Trace.WriteLine(error);}finally{previewBusy=false;}});
    }
    private JsonObject Project(JsonObject data)
    {
        var project=data["project"] as JsonObject??throw new InvalidDataException("Missing project.");
        if(project["version"]?.GetValue<int>()!=1||project.ToJsonString().Length>2_000_000)throw new InvalidDataException("Invalid project format.");
        var w=project["width"]?.GetValue<int>()??0;var h=project["height"]?.GetValue<int>()??0;var fps=project["fps"]?.GetValue<int>()??0;
        if(w<256||h<256||w>4096||h>4096||w%2!=0||h%2!=0||fps is not (30 or 60))throw new InvalidDataException("Invalid output dimensions or frame rate.");
        return project;
    }
    private async Task<object?> Handle(string method,JsonObject data)
    {
        switch(method)
        {
            case "ready":
                // A reloaded editor starts with no export or recording on screen, so the host must not keep one running.
                exportCancellation?.Cancel();if(!exportBusy)await CleanupExport();
                object? recovered=null;if(recorder is not null&&demo is null){try{recovered=await StopRecording();}catch(Exception error){CrashLog.Write(root,error,"recording after editor reload");}}
                JsonNode? saved=null;try{saved=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,"autosave.json")));}catch(FileNotFoundException){}catch(JsonException){}
                // After an editor reload, tell the new page about the app that is still connected.
                if(sourceHandle!=0&&connection is not null){var current=connection;_=Dispatcher.BeginInvoke(()=>Event("app.connected",current),System.Windows.Threading.DispatcherPriority.Background);}
                return new{project=saved,tools=new{ffmpeg=MediaService.FindTool("ffmpeg"),blender=media.BlenderPath??MediaService.FindTool("blender"),adb=AndroidTools.Adb,scrcpy=AndroidTools.Scrcpy},root,recent=await Task.Run(DescribeRecentApps),build=BuildInfo(),recovered};
            case "project.autosave":
                autosave=Project(data).DeepClone().AsObject();var temp=Path.Combine(root,"autosave.tmp");await File.WriteAllTextAsync(temp,autosave.ToJsonString());File.Move(temp,Path.Combine(root,"autosave.json"),true);return null;
            case "project.save":
            {
                var project=Project(data).DeepClone().AsObject();var dialog=new SaveFileDialog{Filter="LaunchReel project|*.launchreel",FileName=SafeName(project["name"]?.GetValue<string>()??"Project")+".launchreel"};if(dialog.ShowDialog(this)!=true)return new{cancelled=true};
                var folder=Path.Combine(Path.GetDirectoryName(dialog.FileName)!,Path.GetFileNameWithoutExtension(dialog.FileName)+".assets");Directory.CreateDirectory(folder);
                foreach(var asset in project["assets"]!.AsArray()){var path=media.AssetPath(project,asset!["id"]!.GetValue<string>());await Task.Run(()=>File.Copy(path,Path.Combine(folder,Path.GetFileName(path)),true));}
                await File.WriteAllTextAsync(dialog.FileName,project.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));return new{path=dialog.FileName};
            }
            case "project.open":
            {
                var dialog=new OpenFileDialog{Filter="LaunchReel project|*.launchreel;*.flystudio"};if(dialog.ShowDialog(this)!=true)return null;
                if(new FileInfo(dialog.FileName).Length>2_000_000)throw new InvalidDataException("Project file is too large.");var project=JsonNode.Parse(await File.ReadAllTextAsync(dialog.FileName))!.AsObject();
                var folder=Path.Combine(Path.GetDirectoryName(dialog.FileName)!,Path.GetFileNameWithoutExtension(dialog.FileName)+".assets");
                foreach(var asset in project["assets"]!.AsArray()){var file=asset!["file"]!.GetValue<string>();if(file!=Path.GetFileName(file))throw new InvalidDataException("Invalid asset path.");if(!MediaService.SafeId(asset["id"]?.GetValue<string>()))throw new InvalidDataException("Invalid asset id.");var from=Path.Combine(folder,file);var to=Path.Combine(media.Assets,file);if(File.Exists(from))await Task.Run(()=>File.Copy(from,to,true));else if(!File.Exists(to))throw new FileNotFoundException($"Missing asset: {asset["name"]}. Keep the .assets folder beside your project.");asset["url"]="https://assets.studio.local/"+file;}
                return project;
            }
            case "assets.import":
            {
                var dialog=new OpenFileDialog{Filter="Studio media|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.wav;*.mp3;*.m4a;*.ogg;*.glb",Multiselect=true};if(dialog.ShowDialog(this)!=true)return Array.Empty<object>();
                var result=new JsonArray();foreach(var file in dialog.FileNames)result.Add(await media.Import(file));return result;
            }
            case "assets.relink":
            {
                var dialog=new OpenFileDialog{Filter="Media|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.wav;*.mp3;*.m4a;*.ogg;*.glb"};return dialog.ShowDialog(this)==true?await media.Import(dialog.FileName):null;
            }
            case "windows.list":return await ListWindows();
            case "apps.recent":return await Task.Run(DescribeRecentApps);
            case "apps.installed":return (await AppCatalog.Installed(data["refresh"]?.GetValue<bool>()==true)).Select(a=>new{id=a.Id,name=a.Name,kind=a.Kind}).ToArray();
            case "app.launchInstalled":appSurface?.Suspend();try{return await LaunchInstalled(data);}catch{PlaceAppSurface();throw;}
            case "android.status":return await AndroidStatus();
            case "android.apps":return await AndroidTools.Apps(data["serial"]?.GetValue<string>()??"");
            case "android.launch":appSurface?.Suspend();try{return await LaunchAndroid(data["serial"]?.GetValue<string>()??"",data["package"]?.GetValue<string>()??"",data["model"]?.GetValue<string>(),data["mirror"]?.GetValue<bool>()!=true);}catch{PlaceAppSurface();throw;}
            case "android.emulator":AndroidTools.StartEmulator(data["avd"]?.GetValue<string>()??"");return null;
            case "tools.scrcpy":
            {
                var dialog=new OpenFileDialog{Filter="scrcpy|scrcpy.exe",Title="Select scrcpy.exe"};if(dialog.ShowDialog(this)==true){AndroidTools.ScrcpyOverride=dialog.FileName;SaveToolPaths();}return new{path=AndroidTools.Scrcpy};
            }
            case "capture.start":return data["handle"]?.GetValue<string>()=="screen"?ConnectScreen():await ConnectApplication(nint.Parse(data["handle"]!.GetValue<string>()),data["mirror"]?.GetValue<bool>()!=true);
            case "capture.stop":if(recorder is not null)throw new InvalidOperationException("Finish recording before disconnecting.");StopWaiting();ReleaseSource();Event("app.disconnected",null);return null;
            case "app.cancelWait":StopWaiting();return null;
            case "app.view":return SetAppView(data);
            case "app.viewport":UpdateAppViewport(data);return null;
            case "app.unminimize":appSurface?.Unminimize();return null;
            case "capture.snapshot":
            {
                var frame=SourceFrame()??throw new InvalidOperationException("The app has not produced a picture yet. Wait a moment and try again.");var path=Path.Combine(media.Assets,Guid.NewGuid()+".png");await File.WriteAllBytesAsync(path,await NativeCapture.Png(frame));
                var asset=await media.Import(path,$"{sourceTitle} · still {DateTime.Now:HH.mm.ss}");asset["width"]=frame.Width;asset["height"]=frame.Height;return asset;
            }
            case "bridge.freeze":
            {
                var frame=await Bridge(new JsonObject{["action"]="frame",["elementId"]=data["elementId"]?.DeepClone()}) as JsonNode??throw new IOException("Bridge returned no image.");
                var path=Path.Combine(media.Assets,Guid.NewGuid()+".png");await File.WriteAllBytesAsync(path,Convert.FromBase64String(frame["png"]!.GetValue<string>()));return await media.Import(path,"UI layer · "+(data["name"]?.GetValue<string>()??"Captured element"));
            }
            case "capture.record":StartRecording(data["fps"]?.GetValue<int>()??30);return new{recording=true};
            case "capture.finish":return await StopRecording();
            case "input":
                if(bridgeToken is not null)return await Bridge(data);
                // Live 3D: drive the real app through window messages; its window, focus and the real cursor stay put.
                if(mirror is null)throw new InvalidOperationException("Launch or choose an app window first.");
                // Android mirrors are operated on the device itself, through adb's input command.
                if(androidInput is {} droid&&droid.Window==mirror.Window){var isKey=data["action"]?.GetValue<string>()=="key";await Task.Run(()=>{if(isKey)droid.Key(data);else droid.Pointer(data);});return null;}
                // WPF, WinUI and UWP ignore posted input while inactive; drive them through their own UI Automation instead.
                {
                    var target=mirror;var key=data["action"]?.GetValue<string>()=="key";
                    if(UiaInput.Needed(target.Window))
                    {
                        var origin=WindowInput.Origin(target.Window);var windows=target.Windows;
                        await Task.Run(()=>{if(key)uiaInput.Key(data);else uiaInput.Pointer(windows,(origin.X+(int)Math.Round(data["x"]!.GetValue<double>()),origin.Y+(int)Math.Round(data["y"]!.GetValue<double>())),data);});
                    }
                    else if(key)windowInput.Key(target.Pid,target.Window,data);
                    else windowInput.Pointer(target.Pid,WindowInput.Origin(target.Window),target.Windows,data);
                }
                return null;
            case "bridge.layers":return await Bridge(new JsonObject{["action"]="layers"});
            case "bridge.frame":return await Bridge(new JsonObject{["action"]="frame",["elementId"]=data["elementId"]?.DeepClone()});
            case "app.launch":
            {
                appSurface?.Suspend();
                // Shortcuts are followed to the program they start (DereferenceLinks).
                var dialog=new OpenFileDialog{Filter="App or shortcut|*.exe;*.lnk",Title="Choose your app or its shortcut",DereferenceLinks=true};
                if(dialog.ShowDialog(this)!=true){PlaceAppSurface();return null;}
                try{return await LaunchApplication(dialog.FileName,data["mirror"]?.GetValue<bool>()!=true);}catch{PlaceAppSurface();throw;}
            }
            case "app.launchRecent":appSurface?.Suspend();try{return await LaunchRecent(data);}catch{PlaceAppSurface();throw;}
            case "snapshot.save":
            {
                var dialog=new SaveFileDialog{Filter="PNG image|*.png",FileName=SafeName(data["name"]?.GetValue<string>()??"Studio")+".png"};if(dialog.ShowDialog(this)!=true)return null;await File.WriteAllBytesAsync(dialog.FileName,Convert.FromBase64String(data["png"]!.GetValue<string>()));return new{path=dialog.FileName};
            }
            case "export.begin":return await BeginExport(Project(data));
            case "export.frame":if(export is null)throw new InvalidOperationException("No export is running.");await export.Append(data["index"]!.GetValue<int>(),data["png"]!.GetValue<string>());return null;
            case "export.finish":
            {
                if(export is null)throw new InvalidOperationException("No export is running.");var path=await export.Finish();await CleanupExport();return new{path};
            }
            case "export.cancel":exportCancellation?.Cancel();if(!exportBusy)await CleanupExport();return null;
            case "export.cinematic":return await RenderCinematic(Project(data),data["titles"] as JsonArray??[]);
            case "tools.blender":
            {
                var dialog=new OpenFileDialog{Filter="Blender|blender.exe",Title="Select Blender executable"};if(dialog.ShowDialog(this)==true){media.BlenderPath=dialog.FileName;SaveToolPaths();}return new{path=media.BlenderPath};
            }
            case "clipboard.copy":Clipboard.SetText(data["text"]!.GetValue<string>());return null;
            case "automation.elements":
            {
                if(sourceHandle==0)throw new InvalidOperationException("Launch or choose your app first.");var handle=sourceHandle;
                return new{title=sourceTitle,elements=await Task.Run(()=>new DemoAutomation(handle).Elements())};
            }
            case "automation.run":return await RunDemo(DemoScript.Parse(data["demo"]),data["fps"]?.GetValue<int>()??30);
            case "automation.stop":demo?.Cancel();return null;
            case "music.generate":return await GenerateMusic(data);
            default:throw new InvalidOperationException("Unknown studio command.");
        }
    }
    /// <summary>Composes an original track with the bundled synthesizer (Python with numpy and scipy) and adds it as an asset.</summary>
    private async Task<object> GenerateMusic(JsonObject data)
    {
        var python=MediaService.FindTool("python")??MediaService.FindTool("py")??throw new FileNotFoundException("Generating music needs Python 3 with numpy and scipy. Install them, or import your own soundtrack in Assets.");
        int Number(string key,int min,int max)=>Math.Clamp(data[key]?.GetValue<int>()??throw new InvalidDataException("Missing "+key),min,max);
        string List(string key)=>string.Join(',',((data[key] as JsonArray)??[]).Select(n=>Math.Clamp(n!.GetValue<int>(),0,500)));
        var bars=Number("bars",2,300);var path=Path.Combine(media.Assets,Guid.NewGuid()+".wav");
        try{await MediaService.Run(python,["-I",Path.Combine(AppContext.BaseDirectory,"Renderers","soundtrack.py"),path,"--bars",bars.ToString(),"--drops",List("drops"),"--breaks",List("breaks"),"--ending",Number("ending",1,300).ToString()]);}
        catch(IOException error)when(error.Message.Contains("ModuleNotFoundError")){throw new InvalidOperationException("Generating music needs the numpy and scipy Python packages: pip install numpy scipy");}
        return await media.Import(path,"Original soundtrack");
    }
    private CancellationTokenSource? demo;
    /// <summary>Performs a demo on the connected app, recording it when asked. A stopped or failed demo keeps what was recorded.</summary>
    private async Task<object> RunDemo(DemoPlan plan,int fps)
    {
        if(sourceHandle==0)throw new InvalidOperationException("Launch or choose your app first.");
        if(demo is not null)throw new InvalidOperationException("A demo is already running.");
        if(recorder is not null)throw new InvalidOperationException("Finish the current recording first.");
        var handle=sourceHandle;demo=new CancellationTokenSource();var token=demo.Token;string? message=null;var completed=false;object? asset=null;
        try
        {
            if(plan.Record)StartRecording(fps);
            await Task.Run(()=>new DemoAutomation(handle).Run(plan,(index,label)=>Dispatcher.BeginInvoke(()=>Event("automation.step",new{index,total=plan.Steps.Count,label})),token),token);
            completed=true;
        }
        catch(OperationCanceledException error){message=token.IsCancellationRequested?"Demo stopped.":error.Message;}
        catch(Exception error){message=error.Message;if(error is not(InvalidOperationException or InvalidDataException))CrashLog.Write(root,error,"demo");}
        finally
        {
            demo.Dispose();demo=null;
            if(recorder is not null){try{asset=await StopRecording();}catch(Exception error){message=(message is null?"":message+" ")+error.Message;}}
            Activate();
        }
        return new{asset,completed,message=message is null?null:asset is null?message:message+" What was recorded so far is in your assets."};
    }
    /// <summary>The picture of the connected source: the mirror (window plus its pop-ups) when there is one.</summary>
    private CapturedFrame? SourceFrame(){var m=mirror;return m is not null?m.Compose(capture.Latest):capture.Latest;}
    private static object? BuildInfo()
    {
        try{return JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"build-info.json")));}catch{return null;}
    }
    private async Task<object> BeginExport(JsonObject project)
    {
        if(export is not null||exportCancellation is not null)throw new InvalidOperationException("An export is already running.");
        if(recorder is not null)throw new InvalidOperationException("Stop recording before exporting.");
        var dialog=new SaveFileDialog{Filter="MP4 video|*.mp4",FileName=SafeName(project["name"]!.GetValue<string>())+".mp4"};if(dialog.ShowDialog(this)!=true)return new{cancelled=true};
        var duration=project["shots"]!.AsArray().Sum(s=>s!["duration"]!.GetValue<double>());if(!double.IsFinite(duration)||duration<=0||duration>1800)throw new InvalidDataException("Invalid project duration.");
        var fps=project["fps"]!.GetValue<int>();var count=(int)Math.Ceiling(duration*fps-1e-6);var estimate=media.DecodedSizeEstimate(project,fps)+512L*1024*1024;
        if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<estimate)throw new IOException("Not enough temporary disk space for a safe export. Shorten the film or use a smaller output size.");
        exportCancellation=new CancellationTokenSource();exportCache=Guid.NewGuid().ToString("N");Event("export.status",new{message="Preparing frame-accurate media…"});
        exportBusy=true;
        try{var decoded=await media.DecodeVideos(project,exportCache,fps,exportCancellation.Token);exportCancellation.Token.ThrowIfCancellationRequested();export=new ExportJob();export.Begin(media,project,dialog.FileName,count);return new{count,decoded};}
        catch{exportBusy=false;await CleanupExport();throw;}
        finally{exportBusy=false;}
    }
    /// <summary>True while an export operation is awaiting work of its own; it then owns the cleanup.</summary>
    private bool exportBusy;
    private async Task CleanupExport()
    {
        if(export is not null){await export.DisposeAsync();export=null;}exportCancellation?.Dispose();exportCancellation=null;
        if(exportCache is not null){var directory=Path.GetFullPath(Path.Combine(media.Cache,exportCache));if(directory.StartsWith(Path.GetFullPath(media.Cache)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(directory))Directory.Delete(directory,true);exportCache=null;}
    }
    private Action<string>? recordingFailed;
    private void StartRecording(int fps)
    {
        if(recorder is not null)throw new InvalidOperationException("Recording is already running.");if(sourceHandle==0&&!screenCapture)throw new InvalidOperationException("Launch or choose your app first.");
        if(export is not null)throw new InvalidOperationException("Finish the export before recording.");
        var ffmpeg=MediaService.FindTool("ffmpeg")??throw new FileNotFoundException("Install FFmpeg to record.");
        capture.MaxRate=fps;
        try{recorder=new Recorder(ffmpeg,Path.Combine(media.Assets,Guid.NewGuid()+".mp4"),SourceFrame,fps);}
        catch{capture.MaxRate=30;throw;}
        recordingFailed=message=>Dispatcher.BeginInvoke(()=>Event("capture.recordError",new{message}));recorder.Failed+=recordingFailed;
    }
    private async Task<object> StopRecording()
    {
        var active=recorder??throw new InvalidOperationException("No recording is running.");recorder=null;capture.MaxRate=30;
        try
        {
            var result=await active.Finish();
            var asset=await media.Import(result.Path,$"{(string.IsNullOrEmpty(sourceTitle)?"App":sourceTitle)} · recording {DateTime.Now:HH.mm.ss}");
            asset["recordedFrames"]=result.Frames;asset["uniqueFrames"]=result.UniqueFrames;asset["recordedFps"]=result.Fps;return asset;
        }
        catch{try{File.Delete(active.Path);}catch{}throw;}
        finally{await active.DisposeAsync();}
    }
    private async Task<object?> Bridge(JsonObject command)
    {
        if(bridgeToken is null||bridgePipe is null)throw new InvalidOperationException("No studio bridge is connected.");
        using var pipe=new NamedPipeClientStream(".",bridgePipe,PipeDirection.InOut,PipeOptions.Asynchronous);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));await pipe.ConnectAsync(timeout.Token);
        using var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true);using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
        var request=command.DeepClone().AsObject();request["token"]=bridgeToken;await writer.WriteLineAsync(request.ToJsonString());var line=await reader.ReadLineAsync(timeout.Token)??throw new IOException("Bridge disconnected.");var response=JsonNode.Parse(line)!;
        if(response["ok"]?.GetValue<bool>()!=true)throw new InvalidOperationException(response["error"]?.GetValue<string>()??"App rejected the action.");return response["data"]?.DeepClone();
    }
    private async Task<object> RenderCinematic(JsonObject project,JsonArray titles)
    {
        var blender=media.BlenderPath??MediaService.FindTool("blender")??throw new FileNotFoundException("Select a Blender executable in Settings to use cinematic rendering.");
        var ffmpeg=MediaService.FindTool("ffmpeg")??throw new FileNotFoundException("Install FFmpeg to export video.");
        var total=project["shots"]!.AsArray().Sum(s=>s!["duration"]!.GetValue<double>());if(!double.IsFinite(total)||total<=0||total>1800)throw new InvalidDataException("Invalid project duration.");
        if(exportCancellation is not null||export is not null)throw new InvalidOperationException("An export is already running.");
        if(recorder is not null)throw new InvalidOperationException("Stop recording before exporting.");
        var shots=project["shots"]!.AsArray();if(titles.Count!=shots.Count)throw new InvalidDataException("Every shot needs its title layer.");
        var dialog=new SaveFileDialog{Filter="MP4 video|*.mp4",FileName=SafeName(project["name"]!.GetValue<string>())+"-cinematic.mp4"};if(dialog.ShowDialog(this)!=true)return new{cancelled=true};
        exportCancellation=new();exportCache=Guid.NewGuid().ToString("N");var dir=Path.Combine(media.Cache,exportCache);Directory.CreateDirectory(dir);
        exportBusy=true;
        try
        {
            var copy=project.DeepClone().AsObject();var fps=copy["fps"]!.GetValue<int>();var decoded=await media.DecodeVideos(copy,exportCache,fps,exportCancellation.Token);foreach(var a in copy["assets"]!.AsArray()){var id=a!["id"]!.GetValue<string>();a["path"]=media.AssetPath(copy,id);if(decoded[id] is JsonObject decodedAsset){a["decodedPath"]=Path.Combine(dir,id);a["decodedCount"]=decodedAsset["count"]!.GetValue<int>();}}
            var manifest=Path.Combine(dir,"scene.json");await File.WriteAllTextAsync(manifest,copy.ToJsonString());
            var renderer=Path.Combine(AppContext.BaseDirectory,"Renderers","blender_render.py");Event("export.status",new{message="Blender is rendering locally. This can take time…"});
            await MediaService.Run(blender,["--background","--factory-startup","--disable-autoexec","--python-exit-code","1","--python",renderer,"--",manifest,dir],exportCancellation.Token);
            // Titles come from the editor's own typography so both renderers set identical text.
            var titleFiles=new List<string>();for(var i=0;i<titles.Count;i++){var png=Convert.FromBase64String(titles[i]!.GetValue<string>());if(png.Length<8||png[0]!=137||png[1]!=80)throw new InvalidDataException("Title layer must be a PNG.");var file=Path.Combine(dir,$"title_{i:D3}.png");await File.WriteAllBytesAsync(file,png);titleFiles.Add(file);}
            var durations=shots.Select(s=>s!["duration"]!.GetValue<double>()).ToList();
            var args=CinematicComposite.Arguments(Path.Combine(dir,"frame_%08d.png"),fps,titleFiles,durations,copy["audioId"]?.GetValue<string>() is string audio?media.AssetPath(copy,audio):null,copy["audioVolume"]?.GetValue<double>()??1,Path.Combine(dir,"cinematic.mp4"));
            await MediaService.Run(ffmpeg,args,exportCancellation.Token);File.Move(Path.Combine(dir,"cinematic.mp4"),dialog.FileName,true);return new{path=dialog.FileName};
        }finally{exportBusy=false;await CleanupExport();}
    }
    private static string SafeName(string value)=>string.Concat(value.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
}
