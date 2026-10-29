using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Studio.Host;

/// <summary>
/// Gentle diagnostic run against a real app (--probe-app &lt;exe&gt;): connect, hold it in App view, switch to 3D and back,
/// then hand it back. It never hides, maximizes, minimizes, closes or types into the app. Screenshots and a report
/// go to probe\ in the data folder.
/// </summary>
public partial class MainWindow
{
    private async Task RunAppProbe(string executable)
    {
        if(testStarted)return;testStarted=true;
        var proof=Path.Combine(root,"probe");Directory.CreateDirectory(proof);var report=new JsonObject{["executable"]=executable};
        JsonObject State(string step){var b=appSurface?.Bounds??Rect.Empty;return new JsonObject{["step"]=step,["docked"]=appSurface?.IsDocked==true,["appView"]=appView,["bounds"]=b.ToString(System.Globalization.CultureInfo.InvariantCulture),["viewport"]=appViewport?.ToString(System.Globalization.CultureInfo.InvariantCulture),["studio"]=$"{WindowState} {Left},{Top} {ActualWidth}x{ActualHeight}",["frame"]=capture.Latest is {} f?$"{f.Width}x{f.Height} #{f.Sequence}":"none"};}
        var steps=new JsonArray();report["steps"]=steps;
        try
        {
            await WaitForEditor();
            var result=JsonSerializer.SerializeToNode(await LaunchApplication(executable))!;report["launch"]=result.DeepClone();
            for(var i=0;i<40&&appSurface?.IsDocked!=true;i++)await Task.Delay(100);
            await Task.Delay(1500);steps.Add(State("app view"));Screen(Path.Combine(proof,"1-app-view.png"));
            await Task.Delay(2000);steps.Add(State("app view +2s"));Screen(Path.Combine(proof,"2-app-view-later.png"));
            async Task<JsonNode?> Debug()=>JsonNode.Parse(JsonSerializer.Deserialize<string>(await Browser.ExecuteScriptAsync("JSON.stringify(window.studioTest.debug())"))!);
            // Switch views through the editor's own buttons, exactly as the user does.
            await Browser.ExecuteScriptAsync("document.getElementById('mode-compose').click()");await Task.Delay(800);report["debug3dEarly"]=await Debug();
            await Task.Delay(2200);steps.Add(State("3d view"));report["debug3d"]=await Debug();Screen(Path.Combine(proof,"3-3d-view.png"));
            using(var stream=File.Create(Path.Combine(proof,"3-editor.png")))await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
            await Browser.ExecuteScriptAsync("document.getElementById('mode-app').click()");await Task.Delay(2000);steps.Add(State("app view again"));report["debugApp"]=await Debug();Screen(Path.Combine(proof,"4-app-view-again.png"));
            await Handle("capture.stop",new JsonObject());await Task.Delay(1200);steps.Add(State("released"));Screen(Path.Combine(proof,"5-released.png"));
            report["ok"]=true;
        }
        catch(Exception error){report["ok"]=false;report["error"]=error.ToString();}
        finally
        {
            try{ReleaseSource();}catch(Exception error){report["releaseError"]=error.Message;}
            await File.WriteAllTextAsync(Path.Combine(proof,"report.json"),report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(0);
        }
    }
    /// <summary>Saves the whole desktop as it appears, so the real app window's position can be checked by eye.</summary>
    private static void Screen(string path)
    {
        int x=GetSystemMetrics(76),y=GetSystemMetrics(77),w=GetSystemMetrics(78),h=GetSystemMetrics(79);
        var screen=GetDC(0);var memory=CreateCompatibleDC(screen);var bitmap=CreateCompatibleBitmap(screen,w,h);var old=SelectObject(memory,bitmap);
        try
        {
            BitBlt(memory,0,0,w,h,screen,x,y,0x00CC0020|0x40000000);
            var source=Imaging.CreateBitmapSourceFromHBitmap(bitmap,0,new Int32Rect(0,0,w,h),BitmapSizeOptions.FromEmptyOptions());
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var stream=File.Create(path);encoder.Save(stream);
        }
        finally{SelectObject(memory,old);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(0,screen);}
    }
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]private static extern nint CreateCompatibleBitmap(nint dc,int width,int height);
    [DllImport("gdi32.dll")]private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")]private static extern bool BitBlt(nint dest,int x,int y,int width,int height,nint source,int sx,int sy,uint rop);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
}
