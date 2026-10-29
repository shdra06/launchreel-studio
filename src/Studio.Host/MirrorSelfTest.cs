using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace Studio.Host;

/// <summary>
/// Automated Live 3D check against the test fixture (--mirror-test): mirror the window without docking it, click and
/// type into it through Studio's input path, then read the results back from the real app through UI Automation.
/// The fixture's window, the real cursor and the foreground window must not change.
/// </summary>
public partial class MainWindow
{
    private async Task RunMirrorTest(string fixture)
    {
        if(testStarted)return;testStarted=true;
        var proof=Path.Combine(root,"mirror-proof");Directory.CreateDirectory(proof);var report=new JsonObject();int pid=0;
        try
        {
            await WaitForEditor();
            if(AppWindows.IsRunning(fixture))throw new InvalidOperationException("A previous test fixture is still running.");
            var result=JsonSerializer.SerializeToNode(await LaunchApplication(fixture,dock:false))!.AsObject();
            if(result["state"]?.GetValue<string>()!="connected")throw new IOException("Not connected: "+result.ToJsonString());
            pid=result["pid"]!.GetValue<int>();var window=nint.Parse(result["handle"]!.GetValue<string>());
            bridgeToken=null; // exercise the window-message path, not the development bridge
            await Task.Delay(600);
            MirrorRect(window,out var before);MirrorCursor(out var cursor);var foreground=MirrorForeground();
            var app=AutomationElement.FromHandle(window);
            AutomationElement Find(Condition c)=>app.FindFirst(TreeScope.Descendants,c)??throw new IOException("Fixture control not found.");
            var field=Find(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
            var count=Find(new PropertyCondition(AutomationElement.NameProperty,"Count"));
            var origin=WindowInput.Origin(window);
            JsonObject At(AutomationElement e,string action){var r=e.Current.BoundingRectangle;return new JsonObject{["action"]=action,["x"]=r.Left+r.Width/2-origin.X,["y"]=r.Top+r.Height/2-origin.Y,["button"]=0};}
            var steps=new JsonArray();void Step(string n){GetWindowThreadProcessId(MirrorForeground(),out var f);steps.Add($"{n}:{f}");}Step("start");
            await Handle("input",At(field,"pointerDown"));await Handle("input",At(field,"pointerUp"));await Task.Delay(250);Step("field");
            foreach(var vk in new[]{0x48,0x45,0x4C,0x4C,0x4F}){await Handle("input",new JsonObject{["action"]="key",["type"]="down",["vk"]=vk});await Handle("input",new JsonObject{["action"]="key",["type"]="up",["vk"]=vk});}Step("typed");
            for(var i=0;i<2;i++){await Handle("input",At(count,"pointerDown"));await Handle("input",At(count,"pointerUp"));await Task.Delay(150);}
            await Task.Delay(700);Step("counted");report["focusSteps"]=steps;
            var typed=(field.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern)?.Current.Value;
            var output=app.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().Select(e=>e.Current.Name).FirstOrDefault(n=>n.StartsWith("Ready",StringComparison.Ordinal));
            report["typed"]=typed;report["output"]=output;
            if(typed!="hello")throw new IOException($"Typing through the mirror gave “{typed}”.");
            if(output is null||!output.StartsWith("Ready · 2 ",StringComparison.Ordinal))throw new IOException($"Clicking Count twice through the mirror gave “{output}”.");
            MirrorRect(window,out var after);MirrorCursor(out var cursorAfter);
            if(before.L!=after.L||before.T!=after.T||before.R!=after.R||before.B!=after.B)throw new IOException("The mirrored app's window moved.");
            if(cursor.X!=cursorAfter.X||cursor.Y!=cursorAfter.Y)throw new IOException("The real cursor moved.");
            if(MirrorForeground()!=foreground){GetWindowThreadProcessId(foreground,out var p0);GetWindowThreadProcessId(MirrorForeground(),out var p1);throw new IOException($"Focus moved from pid {p0} to pid {p1} (fixture {pid}, Studio {Environment.ProcessId}).");}
            var frame=SourceFrame()??throw new IOException("No mirrored picture.");await File.WriteAllBytesAsync(Path.Combine(proof,"mirror.png"),await NativeCapture.Png(frame));
            report["windowUnmoved"]=true;report["cursorUnmoved"]=true;report["focusUnchanged"]=true;report["ok"]=true;
        }
        catch(Exception error){report["ok"]=false;report["error"]=error.ToString();}
        finally{await FinishTest(proof,report,fixture,pid);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct MirrorRectData{public int L,T,R,B;}
    [StructLayout(LayoutKind.Sequential)]private struct MirrorPoint{public int X,Y;}
    [DllImport("user32.dll",EntryPoint="GetWindowRect")]private static extern bool MirrorRect(nint window,out MirrorRectData rect);
    [DllImport("user32.dll",EntryPoint="GetCursorPos")]private static extern bool MirrorCursor(out MirrorPoint point);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll",EntryPoint="GetForegroundWindow")]private static extern nint MirrorForeground();
}
