using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LaunchReel.Studio;

/// <summary>Explicitly enabled local development bridge. Do not include it in production builds.</summary>
public sealed class StudioBridge : IDisposable
{
    private readonly Window window;
    private readonly string token;
    private readonly CancellationTokenSource lifetime=new();
    private readonly Dictionary<string,(string Name,FrameworkElement Element)> elements=[];
    private FrameworkElement? focused;
    public StudioBridge(Window window,string token)
    {
        if(token.Length<24)throw new ArgumentException("Use a random session token of at least 24 characters.",nameof(token));
        this.window=window;this.token=token;Register("app","Application",window.Content as FrameworkElement??window);_=Serve();
    }
    public void Register(string id,string name,FrameworkElement element)=>elements[id]=(name,element);
    private async Task Serve()
    {
        while(!lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe=new NamedPipeServerStream("StudioBridge-"+Environment.ProcessId,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);using var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true);using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var line=await reader.ReadLineAsync(timeout.Token);if(line is null||line.Length>65536)continue;JsonObject response;
                try
                {
                    var request=JsonNode.Parse(line)!.AsObject();var supplied=request["token"]?.GetValue<string>()??"";
                    if(!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token),Encoding.UTF8.GetBytes(supplied)))throw new UnauthorizedAccessException("Invalid studio session.");
                    var result=await window.Dispatcher.InvokeAsync(()=>Handle(request));response=new JsonObject{["ok"]=true,["data"]=result};
                }
                catch(Exception e){response=new JsonObject{["ok"]=false,["error"]=e.Message};}
                await writer.WriteLineAsync(response.ToJsonString());
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){break;}
            catch(IOException){}
            catch(OperationCanceledException){}
        }
    }
    private JsonNode? Handle(JsonObject request)
    {
        var action=request["action"]?.GetValue<string>()??"";
        if(action is "hello" or "layers")
        {
            var list=new JsonArray();foreach(var (id,entry)in elements)list.Add(new JsonObject{["id"]=id,["name"]=entry.Name,["width"]=entry.Element.ActualWidth,["height"]=entry.Element.ActualHeight});
            return new JsonObject{["version"]=1,["app"]=window.Title,["layers"]=list,["input"]=true};
        }
        var elementId=request["elementId"]?.GetValue<string>();FrameworkElement? registered=null;
        if(elementId is not null){if(!elements.TryGetValue(elementId,out var entry))throw new InvalidOperationException("The registered element is no longer available.");registered=entry.Element;}
        if(action=="frame")return Snapshot(registered??(FrameworkElement)window.Content);
        if(action is "pointerDown" or "pointerMove" or "pointerUp" or "scroll")
        {
            var x=request["x"]!.GetValue<double>();var y=request["y"]!.GetValue<double>();
            if(!double.IsFinite(x)||!double.IsFinite(y))throw new ArgumentException("Invalid pointer coordinates.");
            Point local;FrameworkElement root;
            if(registered is not null){root=registered;local=new Point(x,y);}
            else{GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle,out var rect);local=window.PointFromScreen(new Point(rect.Left+x,rect.Top+y));root=window;}
            var hit=root.InputHitTest(local) as DependencyObject;var target=FindControl(hit)??root;
            if(action=="pointerDown"){focused=target;Keyboard.Focus(target);}
            if(action=="pointerUp")Activate(target);
            if(action=="scroll"){var scroll=Ancestor<ScrollViewer>(hit);scroll?.ScrollToVerticalOffset(scroll.VerticalOffset+(request["delta"]?.GetValue<double>()??0));}
            return new JsonObject{["handled"]=true};
        }
        if(action=="key")
        {
            var key=request["key"]?.GetValue<string>()??"";if(focused is TextBox text)
            {
                var index=text.SelectionStart;var length=text.SelectionLength;var value=text.Text;
                if(key=="Backspace"){if(length>0)text.SelectedText="";else if(index>0){text.Text=value.Remove(index-1,1);text.CaretIndex=index-1;}}
                else if(key=="Delete"){if(length>0)text.SelectedText="";else if(index<value.Length){text.Text=value.Remove(index,1);text.CaretIndex=index;}}
                else if(key=="ArrowLeft")text.CaretIndex=Math.Max(0,index-1);
                else if(key=="ArrowRight")text.CaretIndex=Math.Min(value.Length,index+1);
                else if(key=="a"&&request["ctrl"]?.GetValue<bool>()==true)text.SelectAll();
                else if(key.Length==1&&request["ctrl"]?.GetValue<bool>()!=true){text.SelectedText=key;text.CaretIndex=index+key.Length;}
                else if(key=="Enter"&&text.AcceptsReturn){text.SelectedText="\n";text.CaretIndex=index+1;}
                else if(key=="Enter")Activate(focused);
                else throw new InvalidOperationException("This text operation is not supported by the development bridge.");
                return new JsonObject{["handled"]=true};
            }
            if(key is "Enter" or " "){if(focused is not null)Activate(focused);return new JsonObject{["handled"]=true};}
            throw new InvalidOperationException("Focus a supported text field or control first.");
        }
        throw new InvalidOperationException("Unsupported bridge action.");
    }
    private static FrameworkElement? FindControl(DependencyObject? current)
    {
        FrameworkElement? scroll=null;
        while(current is not null){if(current is ButtonBase or TextBox or ListBoxItem or ComboBox)return (FrameworkElement)current;if(current is ScrollViewer viewer)scroll=viewer;current=Parent(current);}return scroll;
    }
    private static DependencyObject? Parent(DependencyObject node)=>node is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
    private static T? Ancestor<T>(DependencyObject? node)where T:DependencyObject {while(node is not null){if(node is T typed)return typed;node=Parent(node);}return null;}
    private static void Activate(FrameworkElement target)
    {
        var peer=UIElementAutomationPeer.CreatePeerForElement(target);
        if(peer?.GetPattern(PatternInterface.Toggle)is IToggleProvider toggle)toggle.Toggle();
        else if(peer?.GetPattern(PatternInterface.Invoke)is IInvokeProvider invoke)invoke.Invoke();
        else if(peer?.GetPattern(PatternInterface.SelectionItem)is ISelectionItemProvider select)select.Select();
    }
    private static JsonObject Snapshot(FrameworkElement element)
    {
        if(!element.IsVisible||element.ActualWidth<1||element.ActualHeight<1)throw new InvalidOperationException("Element must be visible and laid out before capture.");
        // Render at the monitor's scale so layers stay as sharp as the app itself on high-DPI displays.
        var dpi=VisualTreeHelper.GetDpi(element);var width=(int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX);var height=(int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY);if(width>4096||height>4096)throw new InvalidOperationException("Element exceeds the capture size limit.");
        var visual=new DrawingVisual();using(var context=visual.RenderOpen())context.DrawRectangle(new VisualBrush(element){Stretch=Stretch.Fill},null,new Rect(0,0,element.ActualWidth,element.ActualHeight));
        var bitmap=new RenderTargetBitmap(width,height,dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);
        return new JsonObject{["png"]=Convert.ToBase64String(stream.ToArray()),["width"]=width,["height"]=height};
    }
    public void Dispose(){lifetime.Cancel();lifetime.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out NativeRect rect);
}
