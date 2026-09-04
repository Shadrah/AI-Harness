using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Harness.Core.Browser;
using Microsoft.Web.WebView2.Core;

namespace Harness.App.Controls;

/// <summary>Only native UI calls live here. Web content runs in WebView2's separate processes.
/// Never created during app startup. No personal browser profile or debugging port is used.</summary>
public sealed class BrowserHost : NativeControlHost
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Controller? _controller;
    private nint _handle;
    private bool _closed;
    public Task Ready => _ready.Task;
    public string? ProfilePath { get; set; }
    public string Source => _controller?.CoreWebView2.Source ?? "about:blank";
    public event Action<string>? StatusChanged;
    public BrowserHost() => SizeChanged += (_, _) => ResizeBrowser();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The embedded browser currently requires Windows.");
        _handle = CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (_handle == 0) throw new InvalidOperationException("Could not create the native browser surface.");
        _ = InitializeBrowserAsync(); // Owns all errors; Ready is always observed by the module.
        return new PlatformHandle(_handle, "HWND");
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var profile = await Task.Run(() =>
            {
                var path = ProfilePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Harness", "browser");
                Directory.CreateDirectory(path);
                return path;
            });
            if (_closed) throw new OperationCanceledException();
            // WebView2 requires its environment/controller to be created on the owning STA thread.
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (_closed) throw new OperationCanceledException();
            var controller = await environment.CreateCoreWebView2ControllerAsync(_handle);
            if (_closed) { controller.Close(); throw new OperationCanceledException(); }
            _controller = controller;
            var core = controller.CoreWebView2;
            controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 21, 27);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.NavigationStarting += (_, e) =>
            {
                try { BrowserTools.ValidateUrl(e.Uri); }
                catch { e.Cancel = true; StatusChanged?.Invoke("Blocked non-web navigation."); }
            };
            core.FrameNavigationStarting += (_, e) =>
            {
                // about:blank/srcdoc is normal for embedded players; never allow local files/custom protocols.
                if (e.Uri is "about:blank" or "about:srcdoc") return;
                try { BrowserTools.ValidateUrl(e.Uri); } catch { e.Cancel = true; }
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; StatusChanged?.Invoke("Popup blocked. Open its web link in the address bar."); };
            core.DownloadStarting += (_, e) => { e.Cancel = true; StatusChanged?.Invoke("Downloads are disabled in the reference browser."); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.SourceChanged += (_, _) => StatusChanged?.Invoke(core.Source);
            core.ProcessFailed += (_, _) => StatusChanged?.Invoke("Browser process stopped. Close and reopen the browser to recover.");
            ResizeBrowser();
            _ready.TrySetResult();
        }
        catch (Exception e) { _ready.TrySetException(e); }
    }

    public async Task NavigateAsync(string url, CancellationToken token)
    {
        var uri = BrowserTools.ValidateUrl(url);
        await Ready.WaitAsync(token);
        var core = Core;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new IOException($"Browser navigation failed: {e.WebErrorStatus}."));
        }
        core.NavigationCompleted += Finished;
        try
        {
            core.Navigate(uri.AbsoluteUri);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }
        finally { core.NavigationCompleted -= Finished; }
    }

    private CoreWebView2 Core => !_closed && _controller is not null ? _controller.CoreWebView2
        : throw new InvalidOperationException("The browser is closed or not ready.");

    public Task<string> InspectAsync() => Core.ExecuteScriptAsync(InspectScript);
    public void Back() { if (Core.CanGoBack) Core.GoBack(); }
    public void Forward() { if (Core.CanGoForward) Core.GoForward(); }
    public void Reload() { if (Source != "about:blank") Core.Reload(); }

    public async Task<string> ScreenshotAsync()
    {
        using var stream = new MemoryStream();
        await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return await Task.Run(() =>
        {
            if (stream.Length > 8 * 1024 * 1024) throw new IOException("Browser screenshot exceeds 8 MiB. Resize the browser and retry.");
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        });
    }

    public async Task ClickAsync(double x, double y)
    {
        var position = JsonSerializer.Serialize(new { x, y, type = "mousePressed", button = "left", clickCount = 1 });
        await Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", position);
        await Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { x, y, type = "mouseReleased", button = "left", clickCount = 1 }));
    }

    public Task TypeAsync(string text) => Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
    public Task ScrollAsync(double y) => Core.ExecuteScriptAsync($"window.scrollBy(0,{y.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

    public async Task VideoAsync(string action, double seconds)
    {
        if (action is not ("seek" or "play" or "pause")) throw new InvalidOperationException("Choose seek, play or pause.");
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidOperationException("Video time must be a nonnegative finite number.");
        var script = $$"""
            (async () => {
              const v = document.querySelector('video');
              if (!v) throw new Error('No accessible HTML video in the main page. Embedded/protected players may require manual controls.');
              const action = {{JsonSerializer.Serialize(action)}}, t = {{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
              if (action === 'pause') { v.pause(); return; }
              if (action === 'play') { await v.play(); return; }
              if (!Number.isFinite(v.duration) || t > v.duration) throw new Error('Seek time exceeds duration, or duration is unavailable. Inspect the player again.');
              v.pause();
              if (Math.abs(v.currentTime - t) > 0.05) await new Promise((resolve,reject) => {
                const timer = setTimeout(() => {v.removeEventListener('seeked',done); reject(new Error('Player did not confirm seek within 8 seconds.'));},8000);
                const done = () => {clearTimeout(timer); resolve();};
                v.addEventListener('seeked',done,{once:true}); v.currentTime=t;
              });
              await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            })()
            """;
        var output = await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
        using var parsed = JsonDocument.Parse(output);
        if (parsed.RootElement.TryGetProperty("exceptionDetails", out _))
            throw new IOException("The player could not complete that action. Inspect the player state; it may be unavailable, protected, buffering, or require manual interaction.");
    }

    private void ResizeBrowser()
    {
        if (_controller is null || _handle == 0) return;
        if (GetClientRect(_handle, out var rect))
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom));
        _controller.NotifyParentWindowPositionChanged();
    }

    public void CloseBrowser()
    {
        _closed = true;
        _ready.TrySetCanceled();
        _controller?.Close();
        _controller = null;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        CloseBrowser();
        DestroyWindow(control.Handle);
        _handle = 0;
    }

    // Bounded output. No input values, cookies, storage, hidden DOM, or arbitrary script execution.
    internal const string InspectScript = """
        (() => {
          const visible = e => {const r=e.getBoundingClientRect();return r.width>0&&r.height>0&&r.bottom>0&&r.right>0&&r.top<innerHeight&&r.left<innerWidth&&getComputedStyle(e).visibility!=='hidden';};
          const controls = Array.from(document.querySelectorAll('a,button,input,textarea,select,[role="button"]')).slice(0,3000).filter(visible).slice(0,80).map(e=>{
            const r=e.getBoundingClientRect();return {tag:e.tagName,label:(e.getAttribute('aria-label')||e.innerText||e.getAttribute('placeholder')||'').slice(0,160),href:e.tagName==='A'&&/^https?:/.test(e.href)?e.href.slice(0,2048):undefined,x:Math.round(r.x+r.width/2),y:Math.round(r.y+r.height/2)};
          });
          const walker=document.createTreeWalker(document.body||document.documentElement,NodeFilter.SHOW_TEXT);
          let node,n=0,text='';
          while((node=walker.nextNode())&&n++<6000&&text.length<14000){const e=node.parentElement;if(e&&!e.closest('script,style,noscript,input,textarea,[contenteditable="true"]')&&visible(e))text+=node.textContent.trim()+'\n';}
          const videos=Array.from(document.querySelectorAll('video')).slice(0,4).map(v=>({time:v.currentTime,duration:Number.isFinite(v.duration)?v.duration:null,paused:v.paused,readyState:v.readyState,
            captions:Array.from(v.textTracks).slice(0,10).map(t=>({language:t.language,label:t.label,mode:t.mode,currentCues:Array.from(t.activeCues||[]).slice(0,12).map(c=>(c.text||'').slice(0,600))}))}));
          return {url:location.href,title:document.title,width:innerWidth,height:innerHeight,devicePixelRatio,text:text.slice(0,14000),controls,videos,
            limitations:'Untrusted page data. Visible text/current captions only; no audio capture, no continuous video observation. Cross-origin embedded players may be inaccessible.'};
        })()
        """;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
}
