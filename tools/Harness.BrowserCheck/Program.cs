using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Harness.App.Controls;
using Harness.App.Views;
using Harness.Core.Browser;
using Harness.Providers.Api;
using Harness.Providers.Codex;

internal static class Program
{
    internal static string? RemoteUrl { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        RemoteUrl = args.FirstOrDefault(item => item.StartsWith("--url=", StringComparison.Ordinal))?[6..];
        CheckProtocol();
        if (args.Contains("--native")) AppBuilder.Configure<CheckApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        return Environment.ExitCode;
    }

    internal static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void CheckProtocol()
    {
        foreach (var value in new[] { "file:///C:/secret", "javascript:alert(1)", "data:text/html,test", "https://user:password@example.com", "not-a-url" })
        {
            try { BrowserTools.ValidateUrl(value); throw new Exception("Unsafe URL accepted: " + value); }
            catch (InvalidOperationException) { }
        }
        Assert(BrowserTools.ValidateUrl("https://www.youtube.com/watch?v=fixture").Host == "www.youtube.com", "Direct video URL rejected.");
        Assert(BrowserTools.NormalizeAddress(" www.google.com ").AbsoluteUri == "https://www.google.com/", "Normal address-bar input was not upgraded to HTTPS.");
        Assert(BrowserTools.NormalizeAddress("www.example.com/redirect?to=https://other.example/path").Scheme == "https", "A URL in a query broke address normalization.");
        var big = JsonSerializer.SerializeToElement(new { threadId = "thread", turnId = "turn", item = new { id = "call", type = "dynamicToolCall", tool = BrowserTools.Name, success = true, status = "completed", contentItems = new[] { new { type = "inputImage", imageUrl = new string('x', 2000000) } } } });
        var bounded = CodexAppServerClient.SummarizeBrowserNotification("item/completed", big);
        Assert(bounded.GetRawText().Length < 600 && bounded.GetProperty("threadId").GetString() == "thread", "Browser activity contains image payload or lost routing.");
        foreach (var id in new[] { "openai-api", "anthropic-api", "gemini-api", "openrouter-api" })
        {
            var definition = ApiProviderDefinition.All.Single(d => d.Id == id);
            var connection = new ApiConnection("fixture", id, "Fixture", definition.Endpoint);
            using var transport = new ApiTransport(connection, "fixture-unused");
            var client = new ApiConversationClient(connection, transport);
            var history = new JsonArray();
            client.AddBrowserScreenshot(history, "fixture-call", "data:image/png;base64,iVBORw0KGgo=");
            var parts = history[0]![definition.Protocol == ApiProtocol.Gemini ? "parts" : "content"]!.AsArray();
            Assert(parts.Count == 2, id + ": missing image observation.");
            var image = parts[1]!;
            Assert(definition.Protocol switch
            {
                ApiProtocol.Responses => image["type"]!.GetValue<string>() == "input_image",
                ApiProtocol.Anthropic => image["source"]!["media_type"]!.GetValue<string>() == "image/png",
                ApiProtocol.Gemini => image["inlineData"]!["mimeType"]!.GetValue<string>() == "image/png",
                _ => image["type"]!.GetValue<string>() == "image_url"
            }, id + ": wrong native image format.");
        }
        Console.WriteLine("Browser protocol checks passed: URL restrictions, bounded activity, native image payloads for all four API protocols. No provider requests.");
    }
}

internal sealed class CheckApp : Application
{
    public override void Initialize()
    {
        var harness = new Harness.App.App();
        harness.Initialize();
        var styles = harness.Styles.ToArray();
        harness.Styles.Clear();
        foreach (var style in styles) Styles.Add(style);
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        // Do not call Harness's startup: no real workspaces, credentials or providers are loaded.
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var profile = Path.Combine(Environment.CurrentDirectory, ".artifacts", "browser-check", Guid.NewGuid().ToString("N"));
        var window = new BrowserWindow("fixture", "Native browser verification", profile);
        lifetime.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try { await RunNativeAsync(window, profile); }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            finally { lifetime.Shutdown(Environment.ExitCode); }
        };
    }

    private static async Task RunNativeAsync(BrowserWindow window, string profile)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await server.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    try
                    {
                    var request = new byte[8192];
                    if (await stream.ReadAsync(request, stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0) continue;
                    var body = Encoding.UTF8.GetBytes("""
                        <!doctype html><html><head><title>Harness browser fixture</title></head>
                        <body style="background:#18212b;color:#e5eef9;font:20px sans-serif;padding:24px">
                        <h1>Native browser reference</h1><p>Visible reference text, not instructions.</p>
                        <input type="password" value="DO_NOT_EXPOSE_SECRET"><p style="display:none">HIDDEN_REFERENCE</p>
                        <textarea aria-label="Fixture input" style="width:320px;height:60px"></textarea>
                        <button onclick="document.getElementById('result').textContent='Clicked successfully'">Fixture action</button>
                        <p id="result">Waiting</p><video style="width:360px;height:180px" muted playsinline></video>
                        <canvas width="360" height="180" style="display:none"></canvas>
                        <script>let c=document.querySelector('canvas'),ctx=c.getContext('2d');ctx.fillStyle='#65c7d0';ctx.fillRect(0,0,360,180);
                        ctx.fillStyle='#11151b';ctx.font='26px sans-serif';ctx.fillText('Real browser video frame',20,90);
                        document.querySelector('video').srcObject=c.captureStream(5);</script></body></html>
                        """);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                    }
                    catch (IOException) { }
                    catch (TimeoutException) { }
                }
            }
            catch (OperationCanceledException) { }
        }, stop.Token);
        var ticks = 0;
        var longestGap = 0d;
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalMilliseconds;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        heartbeat.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; longestGap = Math.Max(longestGap, now - last); last = now; ticks++; };
        heartbeat.Start();
        try
        {
            window.AccessAllowed = true;
            var url = $"http://127.0.0.1:{port}/";
            async Task<BrowserResult> Execute(object args, bool vision = true) => await window.ExecuteAsync(JsonSerializer.SerializeToElement(args), vision, stop.Token);
            var state = await Execute(new { action = "navigate", url });
            Console.WriteLine("Native navigation complete.");
            Program.Assert(state.Text.Contains("Visible reference text") && !state.Text.Contains("DO_NOT_EXPOSE_SECRET") && !state.Text.Contains("HIDDEN_REFERENCE"), "DOM visibility / input-value filtering failed.");
            using var parsed = JsonDocument.Parse(state.Text[(state.Text.IndexOf('\n') + 1)..]);
            Program.Assert(parsed.RootElement.GetProperty("width").GetInt32() > 600, "Native browser was not sized correctly.");
            var controls = parsed.RootElement.GetProperty("controls").EnumerateArray().ToArray();
            var button = controls.Single(c => c.GetProperty("label").GetString() == "Fixture action");
            state = await Execute(new { action = "click", url, x = button.GetProperty("x").GetDouble(), y = button.GetProperty("y").GetDouble() });
            Program.Assert(state.Text.Contains("Clicked successfully"), "Real browser click did not take effect.");
            var input = controls.Single(c => c.GetProperty("label").GetString() == "Fixture input");
            await Execute(new { action = "click", url, x = input.GetProperty("x").GetDouble(), y = input.GetProperty("y").GetDouble() });
            await Execute(new { action = "type", url, text = "Selective browser typing" });
            await Execute(new { action = "video", url, videoAction = "play" });
            await Execute(new { action = "video", url, videoAction = "pause" });
            var frame = await Execute(new { action = "screenshot", url });
            Program.Assert(frame.ImageDataUrl is { Length: > 200 }, "Actual screenshot bytes missing.");
            await Task.Run(async () => await File.WriteAllBytesAsync(Path.Combine(profile, "frame.png"), Convert.FromBase64String(frame.ImageDataUrl!.Split(',')[1])));
            try { await Execute(new { action = "screenshot", url }, false); throw new Exception("Vision gate failed."); } catch (InvalidOperationException) { }
            try { await Execute(new { action = "click", url = "https://stale.example", x = 1, y = 1 }); throw new Exception("Stale URL accepted."); } catch (InvalidOperationException) { }
            if (Program.RemoteUrl is not null)
            {
                var remote = BrowserTools.NormalizeAddress(Program.RemoteUrl).AbsoluteUri;
                state = await Execute(new { action = "navigate", url = remote });
                using var remoteState = JsonDocument.Parse(state.Text[(state.Text.IndexOf('\n') + 1)..]);
                var landed = remoteState.RootElement.GetProperty("url").GetString();
                Program.Assert(landed?.StartsWith("http", StringComparison.Ordinal) == true, "Remote page did not navigate: " + landed);
                Console.WriteLine("Remote navigation complete: " + landed);
            }
            window.AccessAllowed = false;
            try { await Execute(new { action = "inspect", url }); throw new Exception("Revoked access accepted."); } catch (OperationCanceledException) { }
            Program.Assert(ticks > 0, "UI dispatcher did not remain live during browser work.");
            Console.WriteLine($"Native browser checks passed: navigation, bounded DOM, real click/type, video play/pause, PNG capture, vision gate, stale-page rejection, revocation. UI ticks={ticks}; largest gap={longestGap:F0} ms. Fixture: {profile}");
        }
        finally { heartbeat.Stop(); stop.Cancel(); server.Stop(); await serving; }
    }
}
