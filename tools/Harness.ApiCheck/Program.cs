using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Harness.App;
using Harness.App.ViewModels;
using Harness.App.Views;
using Harness.Core.Models;
using Harness.Providers.Api;

if (args.Contains("--startup-profile", StringComparer.Ordinal))
{
    await StartupProfile.RunAsync();
    return;
}
if (args.Contains("--startup-check", StringComparer.Ordinal))
{
    await StartupProfile.CheckAsync();
    return;
}

// No real credentials, provider requests, or paid model turns are used by this check.
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
static ApiConnection Connection(string provider) => new("api-fixture-" + provider, provider, provider, ApiProviderDefinition.All.Single(item => item.Id == provider).Endpoint);
static string Events(params string[] events) => string.Join("\n\n", events.Select(item => "data: " + item)) + "\n\n";

var fixtures = new Dictionary<string, string>
{
    ["openai-api"] = Events(
        """{"type":"response.output_text.delta","delta":"I’m working."}""",
        """{"type":"response.completed","response":{"output":[{"type":"reasoning","id":"r1","encrypted_content":"opaque-signed-state","summary":[]},{"type":"function_call","id":"i1","call_id":"c1","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}],"usage":{"input_tokens":123,"output_tokens":8}}}"""),
    ["anthropic-api"] = Events(
        """{"type":"message_start","message":{"usage":{"input_tokens":23,"cache_read_input_tokens":90,"cache_creation_input_tokens":10}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"private thought"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed-state"}}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"I’m working."}}""",
        """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"c1","name":"read_file","input":{}}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":8}}""",
        """{"type":"message_stop"}"""),
    ["gemini-api"] = Events(
        """{"candidates":[{"content":{"parts":[{"text":"private thought","thought":true},{"text":"I’m working."}]}}]}""",
        """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"read_file","id":"c1","args":{"path":"a.txt"}},"thoughtSignature":"signed-state"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":123,"candidatesTokenCount":5,"thoughtsTokenCount":3}}"""),
    ["openrouter-api"] = Events(
        """{"choices":[{"delta":{"content":"I’m ","reasoning_details":[{"index":0,"type":"reasoning.encrypted","data":"signed-","format":"fixture"}],"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"read_file","arguments":"{\"path\":"}}]}}]}""",
        """{"choices":[{"delta":{"content":"working.","reasoning_details":[{"index":0,"type":"reasoning.encrypted","data":"state"}],"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":123,"completion_tokens":8}}""",
        "[DONE]")
};

foreach (var (provider, fixture) in fixtures)
{
    var connection = Connection(provider);
    var handler = new FixtureHandler(fixture);
    using var transport = new ApiTransport(connection, "fixture-key-not-a-secret", handler);
    var client = new ApiConversationClient(connection, transport);
    var descriptor = new ModelDescriptor(connection.Id, "provider-reported-fixture-model", "Fixture", ModelCapability.Text | ModelCapability.ToolUse);
    var model = new ApiModel(descriptor, new JsonObject(), false, 8192, provider == "anthropic-api");
    var history = new JsonArray();
    await client.AddUserAsync(history, "Inspect a.txt", [], default);
    var text = new StringBuilder();
    var result = await client.CompleteAsync(model, history, "Standing instructions", "custom-effort", null, ApiWorkspaceTools.Definitions,
        delta => { text.Append(delta); return Task.CompletedTask; }, default);
    Check(text.ToString() == "I’m working.", $"{provider}: text/reasoning separation or Unicode failed.");
    Check(result.InputTokens == 123 && result.OutputTokens == 8, $"{provider}: usage aggregation failed.");
    Check(result.Calls.Count == 1 && result.Calls[0].Arguments == "{\"path\":\"a.txt\"}", $"{provider}: tool argument assembly failed.");
    client.AddToolResults(history, [(result.Calls[0], "File contents")]);
    // A serialization round trip is the same boundary used for durable native session state.
    history = JsonNode.Parse(history.ToJsonString())!.AsArray();
    var request = client.BuildRequest(model, history, "Updated standing instructions", "custom-effort", null, ApiWorkspaceTools.Definitions);
    Check(request.ToJsonString().Contains("signed-state", StringComparison.Ordinal), $"{provider}: native reasoning state lost.");
    Check(request.ToJsonString().Contains("File contents", StringComparison.Ordinal), $"{provider}: tool result missing.");
    Check(request.ToJsonString().Contains("custom-effort", StringComparison.Ordinal), $"{provider}: reasoning value not forwarded.");
    Check(request.ToJsonString().Contains("Updated standing instructions", StringComparison.Ordinal), $"{provider}: personalization lost.");
    Check(!request.ToJsonString().Contains("fixture-key", StringComparison.Ordinal), "Credential leaked into conversation state.");
    Check(handler.Calls == 1, "Unexpected automatic retry.");
    var credentialHeader = provider switch { "anthropic-api" => "x-api-key", "gemini-api" => "x-goog-api-key", _ => "Authorization" };
    Check(handler.Headers.ContainsKey(credentialHeader) && handler.Headers.Count(pair => pair.Value.Contains("fixture-key-not-a-secret", StringComparison.Ordinal)) == 1,
        $"{provider}: incorrect credential header routing.");
    if (provider == "gemini-api") Check(history.Last()?["parts"]?[0]?["functionResponse"]?["id"]?.GetValue<string>() == "c1", "Gemini function ID lost.");
}

var anthropic = Connection("anthropic-api");
var discovered = ApiModelCatalog.Parse(anthropic, Obj("""{"id":"future-model","display_name":"Future model","max_input_tokens":456789,"capabilities":{"image_input":{"supported":true},"effort":{"supported":true,"brand-new-level":{"supported":true},"unavailable":{"supported":false}},"thinking":{"supported":true,"types":{"adaptive":{"supported":true}}}}}"""))!;
Check(discovered.Descriptor.ReasoningLevels!.Single().Id == "brand-new-level", "Catalog reasoning levels were hardcoded or unsupported levels leaked.");
Check(discovered.Descriptor.Supports(ModelCapability.Vision) && discovered.AdaptiveThinking && discovered.Descriptor.ContextWindow == 456789, "Reported capabilities lost.");
var unknown = ApiModelCatalog.Parse(Connection("openai-api"), Obj("""{"id":"unclassified-model"}"""))!;
Check(!unknown.CapabilityMetadataReported && unknown.Descriptor.Capabilities == ModelCapability.Text && unknown.Descriptor.ReasoningLevels!.Count == 0, "Unreported capabilities were invented.");
var mistral = ApiModelCatalog.Parse(Connection("mistral-api"), Obj("""{"id":"fixture","capabilities":{"completion_chat":true,"function_calling":true,"vision":true},"max_model_len":32000}"""))!;
Check(mistral.Descriptor.Supports(ModelCapability.ToolUse | ModelCapability.Vision), "Mistral metadata not applied.");
Check(ApiModelCatalog.Parse(Connection("gemini-api"), Obj("""{"name":"models/embedding","supportedGenerationMethods":["embedContent"]}""")) is null, "Embedding model appeared as a chat model.");

var pagination = new FixtureHandler("", json: true, pages:
[
    """{"data":[{"id":"one"}],"has_more":true,"last_id":"one"}""",
    """{"data":[{"id":"two"}],"has_more":false}"""
]);
using (var transport = new ApiTransport(anthropic, "fixture", pagination))
{
    var catalog = await ApiModelCatalog.LoadAsync(anthropic, transport, [], default);
    Check(catalog.Count == 2 && pagination.Calls == 2, "Model pagination lost records.");
}
using (var transport = new ApiTransport(Connection("openai-api"), "fixture", new FixtureHandler(Events("""{"type":"response.output_text.delta","delta":"partial"}"""))))
{
    try
    {
        await new ApiConversationClient(Connection("openai-api"), transport).CompleteAsync(unknown, [], "", null, null, [], _ => Task.CompletedTask, default);
        throw new InvalidOperationException("Truncated stream accepted as successful.");
    }
    catch (IOException) { }
}
using (var transport = new ApiTransport(Connection("openai-api"), "fixture-key", new FixtureHandler("fixture-key", status: HttpStatusCode.Unauthorized)))
{
    try { await transport.GetAsync("models", default); throw new Exception("Unauthorized response accepted."); }
    catch (InvalidOperationException exception) { Check(!exception.Message.Contains("fixture-key", StringComparison.Ordinal), "Error leaked credential."); }
}
foreach (var endpoint in new[] { "http://example.com/v1/", "https://user:password@example.com/v1/", "https://example.com/v1/?key=secret" })
{
    try { _ = new ApiConnection("fixture", "local-api", "Fixture", endpoint).BaseUri; throw new Exception("Unsafe URL accepted."); }
    catch (InvalidOperationException) { }
}

var testRoot = Path.Combine(Environment.CurrentDirectory, ".artifacts", "api-check", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var approvalCount = 0;
var runner = new ApiWorkspaceTools(testRoot, (_, _, _) => { approvalCount++; return Task.FromResult(false); });
var denied = await runner.ExecuteAsync(new("call", "write_file", """{"path":"a.txt","content":"unchanged"}"""), default);
Check(approvalCount == 1 && !File.Exists(Path.Combine(testRoot, "a.txt")) && denied.StartsWith("User declined", StringComparison.Ordinal), "Denied write executed.");
Check((await runner.ExecuteAsync(new("call", "read_file", """{"path":"../outside.txt"}"""), default)).StartsWith("Tool failed", StringComparison.Ordinal), "Path traversal allowed.");
Check((await runner.ExecuteAsync(new("call", "read_file", """{"path":".git/config"}"""), default)).StartsWith("Tool failed", StringComparison.Ordinal), "Git internals exposed.");
await runner.ExecuteAsync(new("call", "run_command", """{"command":"this command must never start"}"""), default);
Check(approvalCount == 2, "Command bypassed approval.");

// Headless UI runs on a dedicated, stable thread after async fixture checks finish.
Exception? uiFailure = null;
var uiThread = new Thread(() =>
{
    try
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var vm = new MainWindowViewModel();
        vm.ApplyProviderModels("openai-codex", [new("openai-codex", "codex-fixture", "Codex", ModelCapability.Text)], "Codex", "FIXTURE");
        vm.ApplyProviderModels(anthropic.Id, [discovered.Descriptor], "Anthropic", "FIXTURE");
        Check(vm.Models.Count == 2 && vm.SelectedModel!.ProviderId == "openai-codex", "API discovery replaced the Codex catalog.");
        vm.SelectedModel = vm.Models.Single(model => model.ProviderId == anthropic.Id);
        Check(vm.SelectedReasoningLevel?.Id == "", "API catalog silently selected a reasoning level instead of provider default.");
        vm.ApplyApiUsage(12, 3, 1000, 500);
        Check(vm.ContextUsagePercent == 2.4 && vm.UsageWindows.Count == 0, "API throughput confused with context or subscription limits.");
        vm.PromptText = "fixture"; vm.BeginTurn(); vm.CompleteTurn("Fixture API failure");
        Check(vm.Messages.Any(message => message.Text.Contains("Fixture API failure", StringComparison.Ordinal)), "API error hidden from chat.");
        var settings = new SettingsWindow(usePreviewData: true) { Width = 1300, Height = 850 };
        settings.Show();
        settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
        settings.FindControl<ComboBox>("ApiProviderPicker")!.SelectedItem = ApiProviderDefinition.All.Single(provider => provider.Id == "anthropic-api");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = settings.CaptureRenderedFrame() ?? throw new Exception("Provider settings frame unavailable.");
        frame.Save(Path.Combine(Environment.CurrentDirectory, ".artifacts", "api-providers.png"));
        Check(settings.FindControl<TextBox>("ApiKey")!.PasswordChar != '\0', "API key input is not masked.");
        settings.Close();
    }
    catch (Exception exception) { uiFailure = exception; }
});
uiThread.Start(); uiThread.Join();
if (uiFailure is not null) throw uiFailure;
Console.WriteLine("API checks passed: four native wire formats, reasoning/tool replay, Unicode, usage, pagination, unknown capabilities, failure handling, credential routing, approval boundaries, catalog merging, and Providers UI. No live API calls made.");

sealed class FixtureHandler(string content, bool json = false, string[]? pages = null, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri) throw new Exception("Request endpoint missing.");
        var body = pages is null ? content : pages[Calls - 1];
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, json ? "application/json" : "text/event-stream") });
    }
}
