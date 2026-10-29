using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;

namespace Studio.Host;

/// <summary>
/// Automated App view check against the test fixture (never a real app): hosting, snapping back after the app moves
/// or maximizes itself, Studio minimizing, switching views, the app hiding to the tray, automatic reconnection and
/// release. Window changes the fixture would make itself are simulated with the same Windows calls an app would use.
/// </summary>
public partial class MainWindow
{
    private async Task RunAppViewTest(string fixture)
    {
        if(testStarted)return;testStarted=true;
        var proof=Path.Combine(root,"app-view-proof");Directory.CreateDirectory(proof);
        var report=new JsonObject{["fixture"]=fixture};var timer=Stopwatch.StartNew();int fixturePid=0;
        var studio=new WindowInteropHelper(this).Handle;
        async Task Until(Func<bool> condition,string failure,int ms=4000){var clock=Stopwatch.StartNew();while(!condition()){if(clock.ElapsedMilliseconds>ms)throw new IOException(failure);await Task.Delay(50);}}
        bool Near(Rect a,Rect b)=>Math.Abs(a.X-b.X)<=3&&Math.Abs(a.Y-b.Y)<=3&&Math.Abs(a.Width-b.Width)<=3&&Math.Abs(a.Height-b.Height)<=3;
        try
        {
            await WaitForEditor();
            var connected=await LaunchFixture(fixture);fixturePid=connected["pid"]!.GetValue<int>();var window=nint.Parse(connected["handle"]!.GetValue<string>());
            TestGetWindowRect(window,out var desktopRect);var desktop=new Rect(desktopRect.Left,desktopRect.Top,desktopRect.Right-desktopRect.Left,desktopRect.Bottom-desktopRect.Top);
            await Until(()=>appSurface?.IsDocked==true&&TestOwner(window)==0,"The app did not enter App view, or Studio took ownership of its window.");
            try{await Until(()=>!Near(appSurface!.Bounds,desktop),"");}catch(IOException){throw new IOException($"The app was not moved into App view. desktop={desktop} now={appSurface!.Bounds} docked={appSurface.IsDocked} appView={appView} viewport={appViewport} state={WindowState} hung={TestHung(window)}");}
            var hosted=appSurface!.Bounds;report["hosted"]=hosted.ToString(System.Globalization.CultureInfo.InvariantCulture);

            TestSetWindowPos(window,0,(int)hosted.X+140,(int)hosted.Y+90,500,400,0x0014);
            await Until(()=>Near(appSurface.Bounds,hosted),"App view did not hold the window after the app moved itself.");report["snapsBackAfterMove"]=true;
            TestShowWindow(window,3);
            await Until(()=>!TestIsZoomed(window)&&Near(appSurface.Bounds,hosted),"App view did not hold the window after the app maximized itself.");report["snapsBackAfterMaximize"]=true;

            TestSendMessage(studio,0x0112,0xF020,0);
            await Until(()=>!appSurface.IsDocked&&TestOwner(window)==0&&AppWindows.IsWindowVisible(window),"Minimizing Studio did not hand the app back visibly.");
            TestShowWindow(studio,4);SendToBack();
            await Until(()=>appSurface.IsDocked&&TestOwner(window)==0,"Restoring Studio did not host the app again.");report["studioMinimizeRestore"]=true;
            TestShowWindow(studio,7);// Minimize without the system command, as Win+M and Show desktop do.
            await Until(()=>!appSurface.IsDocked&&TestOwner(window)==0&&AppWindows.IsWindowVisible(window),"A direct minimize of Studio left the app hidden or owned.");
            TestShowWindow(studio,4);SendToBack();
            await Until(()=>appSurface.IsDocked,"Studio did not host the app after a direct minimize.");report["directMinimizeRestore"]=true;

            await Handle("app.view",new JsonObject{["mode"]="3d"});
            await Until(()=>TestOwner(window)==0&&Near(appSurface.Bounds,desktop),"3D view did not return the app to its own desktop position.");report["releasedFor3d"]=true;
            await Handle("app.view",new JsonObject{["mode"]="app"});
            await Until(()=>appSurface.IsDocked&&Near(appSurface.Bounds,hosted),"App view did not host the app again.");report["returnedToAppView"]=true;

            TestPostMessage(window,0x0010,0,0);// The fixture hides itself on close, like a tray app.
            await Until(()=>sourceHandle==0&&waiting is not null,"Studio did not notice the app hiding its window.");
            await Task.Delay(400);
            if(AppWindows.IsWindowVisible(window))throw new IOException("Studio revealed a window the app had hidden.");
            if(TestOwner(window)!=0)throw new IOException("The hidden window is still owned by Studio.");report["hideReleasesWithoutRevealing"]=true;

            TestShowWindow(window,4);// The app opens its window again.
            await Until(()=>sourceHandle==window&&appSurface.IsDocked,"Studio did not reconnect when the app reopened its window.");report["reconnectsWhenReopened"]=true;

            await Handle("capture.stop",new JsonObject());
            if(TestOwner(window)!=0)throw new IOException("Disconnect left Studio as the window owner.");
            using(var source=Process.GetProcessById(fixturePid))if(source.HasExited)throw new IOException("Disconnect closed the app.");
            report["releasedAndStillRunning"]=true;report["ok"]=true;
        }
        catch(Exception error){report["ok"]=false;report["error"]=error.ToString();}
        finally{report["elapsedSeconds"]=timer.Elapsed.TotalSeconds;await FinishTest(proof,report,fixture,fixturePid);}
    }
    private static nint TestOwner(nint window)=>AppWindows.GetWindowLongPtr(window,-8);
    private static bool TestGetWindowRect(nint window,out AppWindows.NativeRect rect)=>AppWindows.GetWindowRect(window,out rect);
    [DllImport("user32.dll",EntryPoint="SetWindowPos")]private static extern bool TestSetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="ShowWindow")]private static extern bool TestShowWindow(nint window,int command);
    [DllImport("user32.dll",EntryPoint="IsHungAppWindow")]private static extern bool TestHung(nint window);
    [DllImport("user32.dll",EntryPoint="IsZoomed")]private static extern bool TestIsZoomed(nint window);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint TestSendMessage(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool TestPostMessage(nint window,uint message,nint wParam,nint lParam);
}
