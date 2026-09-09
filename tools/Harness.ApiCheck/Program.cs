using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Harness.App;
using Harness.App.Services;
using Harness.App.ViewModels;
using Harness.App.Views;
using Harness.Core.Models;
using Harness.Providers.Api;
using Harness.Providers.Claude;
using Harness.Workspace;
using Microsoft.Data.Sqlite;

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

using (var claudeInitialization = JsonDocument.Parse("""
{
  "account":{"email":"developer@example.test","subscriptionType":"max","tokenSource":"oauth"},
  "models":[
    {"value":"claude-provider-model-a","displayName":"Claude Fixture A","supportsEffort":true,"supportedEffortLevels":["low","high","max"],"supportsFastMode":true,"isDefault":true},
    {"value":"claude-provider-model-b","displayName":"Claude Fixture B","supportsEffort":false,"supportedEffortLevels":[],"supportsFastMode":false}
  ]
}
"""))
{
    var account = ClaudeCodeClient.ParseAccount(claudeInitialization.RootElement);
    var models = ClaudeCodeClient.ParseModels(claudeInitialization.RootElement);
    Check(account.IsAuthenticated && account.SubscriptionType == "max", "Claude Code subscription identity was not parsed.");
    Check(models.Count == 2 && models[0].IsDefault, "Claude Code provider-reported model catalog was not preserved.");
    Check(models[0].ReasoningLevels!.Select(item => item.Id).SequenceEqual(["", "low", "high", "max"]),
        "Claude Code effort levels were hardcoded, reordered, or lost.");
    Check(models[0].ServiceTiers!.Select(item => item.Id).SequenceEqual([null, "fast"]),
        "Claude Code fast mode was not surfaced for the model that reported it.");
    Check(models[1].ReasoningLevels!.Count == 0 && models[1].ServiceTiers!.Count == 0,
        "Claude Code invented effort or fast-mode options for an unsupported model.");
}
using (var claudeSignedOutInitialization = JsonDocument.Parse("""
{"account":{"tokenSource":"none"},"models":[]}
"""))
{
    Check(!ClaudeCodeClient.ParseAccount(claudeSignedOutInitialization.RootElement).IsAuthenticated,
        "Claude Code treated a provider-reported absent token as an authenticated account.");
}
using (var claudeUsage = JsonDocument.Parse("""
{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":71.5,"resets_at":"2030-01-01T12:00:00Z"},"seven_day":{"utilization":22},"model_scoped":[{"display_name":"Sonnet","utilization":35,"resets_at":"2030-01-02T12:00:00Z"}]}}
"""))
{
    var usage = ClaudeCodeClient.ParseUsage(claudeUsage.RootElement, "max");
    Check(usage is { Windows.Count: 3 } && usage.Windows[0].UsedPercent == 71.5
          && usage.Windows[0].ResetsAt is not null,
        "Claude Code subscription usage windows were not preserved.");
}

var fixtures = new Dictionary<string, string>
{
    ["openai-api"] = Events(
        """{"type":"response.output_text.delta","delta":"I’m working."}""",
        """{"type":"response.completed","response":{"output":[{"type":"reasoning","id":"r1","encrypted_content":"opaque-signed-state","summary":[]},{"type":"message","id":"m1","role":"assistant","content":[{"type":"output_text","text":"I’m working.","annotations":[{"type":"container_file_citation","container_id":"cntr_fixture","file_id":"cfile_fixture","filename":"report.pdf"}]}]},{"type":"function_call","id":"i1","call_id":"c1","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}],"usage":{"input_tokens":123,"output_tokens":8}}}"""),
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
        """{"type":"content_block_start","index":3,"content_block":{"type":"bash_code_execution_tool_result","tool_use_id":"srvtoolu_fixture","content":{"type":"bash_code_execution_result","content":[{"type":"bash_code_execution_output","file_id":"file_fixture"}]}}}""",
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
    var descriptor = new ModelDescriptor(connection.Id, "provider-reported-fixture-model", "Fixture", ModelCapability.Text | ModelCapability.ToolUse | ModelCapability.Reasoning,
        ReasoningLevels: [new("custom-effort", "Custom effort")]);
    var model = new ApiModel(descriptor, new JsonObject(), false, 8192, provider == "anthropic-api");
    if (provider == "anthropic-api")
        model = model with
        {
            Descriptor = model.Descriptor with
            { Capabilities = model.Descriptor.Capabilities | ModelCapability.GeneratedArtifacts },
            HostedArtifactsEnabled = true
        };
    var history = new JsonArray();
    await client.AddUserAsync(model, history, "Inspect a.txt", [], default);
    var text = new StringBuilder();
    var result = await client.CompleteAsync(model, history, "Standing instructions", "custom-effort", null, ApiWorkspaceTools.Definitions,
        delta => { text.Append(delta); return Task.CompletedTask; }, default);
    Check(text.ToString() == "I’m working.", $"{provider}: text/reasoning separation or Unicode failed.");
    Check(result.InputTokens == 123 && result.OutputTokens == 8, $"{provider}: usage aggregation failed.");
    if (provider == "anthropic-api")
    {
        Check(result.CacheReadInputTokens == 90 && result.CacheWriteInputTokens == 10,
            "Anthropic cache hit/write telemetry was not retained.");
        Check(result.Artifacts is [{ ProviderFileId: "file_fixture" }],
            "Anthropic generated-file output was not retained as a generated artifact.");
    }
    if (provider == "openai-api")
        Check(result.Artifacts is [{ ProviderFileId: "cfile_fixture", ContainerId: "cntr_fixture", FileName: "report.pdf" }],
            "OpenAI container-file citation was not retained as a generated artifact.");
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
    if (provider == "anthropic-api")
        Check(handler.Headers.TryGetValue("anthropic-beta", out var betas)
              && betas.Contains("code-execution-2025-08-25", StringComparison.Ordinal)
              && betas.Contains("files-api-2025-04-14", StringComparison.Ordinal),
            "Anthropic hosted artifacts did not request the required native beta contracts.");
    if (provider == "gemini-api") Check(history.Last()?["parts"]?[0]?["functionResponse"]?["id"]?.GetValue<string>() == "c1", "Gemini function ID lost.");
}

var anthropic = Connection("anthropic-api");
var discovered = ApiModelCatalog.Parse(anthropic, Obj("""{"id":"future-model","display_name":"Future model","max_input_tokens":456789,"capabilities":{"image_input":{"supported":true},"pdf_input":{"supported":true},"structured_outputs":{"supported":true},"citations":{"supported":true},"context_management":{"supported":true},"effort":{"supported":true,"brand-new-level":{"supported":true},"unavailable":{"supported":false}},"thinking":{"supported":true,"types":{"adaptive":{"supported":true}}}}}"""))!;
Check(discovered.Descriptor.ReasoningLevels!.Single().Id == "brand-new-level", "Catalog reasoning levels were hardcoded or unsupported levels leaked.");
Check(discovered.Descriptor.Supports(ModelCapability.Vision | ModelCapability.PdfInput | ModelCapability.StructuredOutput
      | ModelCapability.Citations | ModelCapability.ContextManagement)
      && discovered.AdaptiveThinking && discovered.Descriptor.ContextWindow == 456789, "Reported capabilities lost.");
var conformance = ApiCapabilityConformance.Evaluate(anthropic, discovered);
Check(conformance.Ready.Contains(ModelCapability.Vision) && conformance.Ready.Contains(ModelCapability.Reasoning)
      && conformance.Ready.Contains(ModelCapability.PdfInput),
    "Implemented reported capabilities were not marked ready.");
Check(conformance.AdapterGaps.Contains(ModelCapability.StructuredOutput)
      && conformance.AdapterGaps.Contains(ModelCapability.Citations)
      && conformance.AdapterGaps.Contains(ModelCapability.ContextManagement),
    "Reported adapter gaps were incorrectly advertised as ready.");
var unknown = ApiModelCatalog.Parse(Connection("openai-api"), Obj("""{"id":"unclassified-model"}"""))!;
Check(!unknown.CapabilityMetadataReported && unknown.Descriptor.Capabilities == ModelCapability.Text && unknown.Descriptor.ReasoningLevels!.Count == 0, "Unreported capabilities were invented.");
Check(ApiCapabilityConformance.Evaluate(Connection("openai-api"), unknown).Findings.Single(finding => finding.Capability == ModelCapability.Vision).State == ApiCapabilityState.Unknown,
    "Missing catalog metadata was treated as proof that vision is unsupported.");
var mistral = ApiModelCatalog.Parse(Connection("mistral-api"), Obj("""{"id":"fixture","capabilities":{"completion_chat":true,"function_calling":true,"vision":true},"max_model_len":32000}"""))!;
Check(mistral.Descriptor.Supports(ModelCapability.ToolUse | ModelCapability.Vision), "Mistral metadata not applied.");
var parameterized = ApiModelCatalog.Parse(Connection("local-api"), Obj("""{"id":"fixture","input_modalities":["text","image","audio","video","pdf"],"output_modalities":["text","audio","file"],"supported_parameters":["tools","reasoning_effort","response_format","prompt_cache_key"]}"""))!;
Check(parameterized.Descriptor.Supports(ModelCapability.Vision | ModelCapability.AudioInput | ModelCapability.AudioOutput
      | ModelCapability.VideoInput | ModelCapability.PdfInput | ModelCapability.ToolUse | ModelCapability.StructuredOutput
      | ModelCapability.PromptCaching | ModelCapability.GeneratedArtifacts),
    "OpenAI-compatible reported parameters or modalities were lost.");
Check(ApiModelCatalog.Parse(Connection("gemini-api"), Obj("""{"name":"models/embedding","supportedGenerationMethods":["embedContent"]}""")) is null, "Embedding model appeared as a chat model.");

var ollamaHandler = new RouteFixtureHandler(async request =>
{
    if (request.RequestUri!.AbsolutePath == "/api/tags")
        return """{"models":[{"name":"gemma3:latest","model":"gemma3:latest"},{"name":"nomic-embed-text:latest","model":"nomic-embed-text:latest"}]}""";
    var body = await request.Content!.ReadAsStringAsync();
    return body.Contains("gemma3:latest", StringComparison.Ordinal)
        ? """{"capabilities":["completion","vision","tools","thinking"],"model_info":{"gemma3.context_length":131072}}"""
        : """{"capabilities":["embedding"],"model_info":{"nomic.context_length":8192}}""";
});
using (var transport = new ApiTransport(Connection("ollama-local"), "", ollamaHandler))
{
    var catalog = await ApiModelCatalog.LoadAsync(Connection("ollama-local"), transport, [], default);
    Check(catalog.Count == 1 && catalog[0].Descriptor.ModelId == "gemma3:latest", "Ollama discovery included a non-chat model or lost its chat model.");
    Check(catalog[0].Descriptor.Supports(ModelCapability.Vision | ModelCapability.ToolUse | ModelCapability.Reasoning)
          && catalog[0].Descriptor.ContextWindow == 131072, "Ollama native capability metadata was lost.");
    Check(catalog[0].Descriptor.ReasoningLevels!.Count == 0, "Ollama reasoning levels were invented instead of being model-reported.");
    Check(ollamaHandler.Paths.SequenceEqual(["/api/tags", "/api/show", "/api/show"]), "Ollama discovery used a generation endpoint or skipped native details.");
}

var llamaHandler = new RouteFixtureHandler(_ => Task.FromResult(
    """{"data":[{"id":"local-vision-model","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"],"meta":{"n_ctx_train":65536}}]}"""));
using (var transport = new ApiTransport(Connection("llama-cpp-local"), "", llamaHandler))
{
    var catalog = await ApiModelCatalog.LoadAsync(Connection("llama-cpp-local"), transport, [], default);
    Check(catalog.Count == 1 && catalog[0].Descriptor.Supports(ModelCapability.Vision | ModelCapability.ToolUse)
          && catalog[0].Descriptor.ContextWindow == 65536, "llama.cpp router metadata was not applied.");
    Check(llamaHandler.Paths.SequenceEqual(["/models"]), "llama.cpp discovery did not use its native metadata route.");
}

var strictModel = discovered with { Descriptor = discovered.Descriptor with
{
    ReasoningLevels = [new("low", "Low")],
    ServiceTiers = [new("priority", "Fast")]
}};
ApiCapabilityConformance.ValidateTurn(anthropic, strictModel, "low", "priority", ApiWorkspaceTools.Definitions);
var cachedAnthropic = strictModel with
{
    Descriptor = strictModel.Descriptor with { Capabilities = strictModel.Descriptor.Capabilities | ModelCapability.PromptCaching },
    PromptCachingEnabled = true
};
using (var transport = new ApiTransport(anthropic, "fixture", new FixtureHandler("")))
{
    var cacheRequest = new ApiConversationClient(anthropic, transport).BuildRequest(cachedAnthropic, [], "Stable instructions", "low", null, []);
    Check(cacheRequest["cache_control"]?["type"]?.GetValue<string>() == "ephemeral",
        "Opt-in Anthropic prompt caching did not use the native top-level cache control.");
}
var openAiArtifactModel = unknown with
{
    Descriptor = unknown.Descriptor with { Capabilities = ModelCapability.Text | ModelCapability.GeneratedArtifacts },
    HostedArtifactsEnabled = true
};
using (var transport = new ApiTransport(Connection("openai-api"), "fixture", new FixtureHandler("")))
{
    var artifactRequest = new ApiConversationClient(Connection("openai-api"), transport)
        .BuildRequest(openAiArtifactModel, [], "Create the requested document", null, null, []);
    Check(artifactRequest["tools"] is JsonArray hostedTools
          && hostedTools.OfType<JsonObject>().Any(tool => tool["type"]?.GetValue<string>() == "code_interpreter")
          && artifactRequest["include"] is JsonArray includes
          && includes.OfType<JsonValue>().Any(value => value.GetValue<string>() == "code_interpreter_call.outputs"),
        "OpenAI hosted artifact opt-in did not request native code-interpreter outputs.");
    Check(ApiCapabilityConformance.Evaluate(Connection("openai-api"), openAiArtifactModel).Ready.Contains(ModelCapability.GeneratedArtifacts),
        "OpenAI hosted artifacts were not marked adapter-ready after explicit model verification.");
}
var openAiCompactionModel = unknown with
{
    Descriptor = unknown.Descriptor with
    {
        Capabilities = ModelCapability.Text | ModelCapability.ContextManagement,
        ServiceTiers = [new("priority", "Fast")]
    },
    ContextManagementEnabled = true
};
var compactHandler = new RouteFixtureHandler(async request =>
{
    Check(request.RequestUri!.AbsolutePath == "/v1/responses/compact",
        "OpenAI compaction used the wrong endpoint.");
    var requestBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
    Check(requestBody["model"]?.GetValue<string>() == "unclassified-model"
          && requestBody["input"] is JsonArray { Count: 2 }
          && requestBody["instructions"]?.GetValue<string>() == "Stable instructions"
          && requestBody["service_tier"]?.GetValue<string>() == "priority",
        "OpenAI compaction did not preserve model, native history, instructions, or service tier.");
    return """{"id":"cmp_fixture","object":"response.compaction","output":[{"id":"msg_fixture","type":"message","role":"user","status":"completed","content":[{"type":"input_text","text":"Continue the task"}]},{"id":"cmp_item_fixture","type":"compaction","encrypted_content":"opaque-compacted-state"}],"usage":{"input_tokens":800,"output_tokens":120,"total_tokens":920}}""";
});
using (var transport = new ApiTransport(Connection("openai-api"), "fixture", compactHandler))
{
    var compacted = await new ApiConversationClient(Connection("openai-api"), transport).CompactAsync(
        openAiCompactionModel,
        new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Continue the task" },
            new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray() }),
        "Stable instructions", "priority", default);
    Check(compacted.History.Count == 2
          && compacted.History[1]?["type"]?.GetValue<string>() == "compaction"
          && compacted.History[1]?["encrypted_content"]?.GetValue<string>() == "opaque-compacted-state"
          && compacted.TotalTokens == 920,
        "OpenAI native compaction state or usage was not retained.");
    Check(ApiCapabilityConformance.Evaluate(Connection("openai-api"), openAiCompactionModel).Ready.Contains(ModelCapability.ContextManagement),
        "OpenAI native compaction was not marked adapter-ready after explicit model verification.");
}

var openAiCountHandler = new RouteFixtureHandler(async request =>
{
    Check(request.RequestUri!.AbsolutePath == "/v1/responses/input_tokens",
        "OpenAI input counting used the wrong endpoint.");
    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
    Check(body["model"]?.GetValue<string>() == "unclassified-model"
          && body["input"] is JsonArray { Count: 1 }
          && body["instructions"]?.GetValue<string>() == "Stable instructions"
          && body["stream"] is null && body["store"] is null && body["include"] is null
          && body["service_tier"] is null,
        "OpenAI preflight did not preserve the countable request or remove generation-only fields.");
    return """{"object":"response.input_tokens","input_tokens":321}""";
});
using (var transport = new ApiTransport(Connection("openai-api"), "fixture", openAiCountHandler))
{
    var count = await new ApiConversationClient(Connection("openai-api"), transport).CountInputTokensAsync(
        openAiCompactionModel, new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Count this" }),
        "Stable instructions", null, "priority", [], default);
    Check(count.InputTokens == 321, "OpenAI provider-reported input count was not retained.");
}

var anthropicCountHandler = new RouteFixtureHandler(async request =>
{
    Check(request.RequestUri!.AbsolutePath == "/v1/messages/count_tokens",
        "Anthropic input counting used the wrong endpoint.");
    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
    Check(body["model"]?.GetValue<string>() == "future-model"
          && body["messages"] is JsonArray { Count: 1 }
          && body["system"]?.GetValue<string>() == "Stable instructions"
          && body["output_config"]?["effort"]?.GetValue<string>() == "low"
          && body["tools"] is JsonArray { Count: > 0 }
          && body["stream"] is null && body["max_tokens"] is null,
        "Anthropic preflight was not the same native message, system, reasoning, and tool request.");
    return """{"input_tokens":654}""";
});
using (var transport = new ApiTransport(anthropic, "fixture", anthropicCountHandler))
{
    var count = await new ApiConversationClient(anthropic, transport).CountInputTokensAsync(
        strictModel, new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Count this" }),
        "Stable instructions", "low", null, ApiWorkspaceTools.Definitions, default);
    Check(count.InputTokens == 654, "Anthropic provider-reported input count was not retained.");
}

var geminiCountConnection = Connection("gemini-api");
var geminiCountModel = new ApiModel(new ModelDescriptor(geminiCountConnection.Id, "gemini-fixture", "Gemini fixture",
    ModelCapability.Text | ModelCapability.ToolUse | ModelCapability.Reasoning,
    ReasoningLevels: [new("high", "High")]), new JsonObject(), false, 8192, false);
var geminiCountHandler = new RouteFixtureHandler(async request =>
{
    Check(request.RequestUri!.AbsolutePath == "/v1beta/models/gemini-fixture:countTokens",
        "Gemini input counting used the wrong endpoint.");
    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
    var generation = body["generateContentRequest"]?.AsObject();
    Check(body.Count == 1 && generation?["model"]?.GetValue<string>() == "models/gemini-fixture"
          && generation["contents"] is JsonArray { Count: 1 }
          && generation["systemInstruction"]?["parts"] is JsonArray { Count: 1 }
          && generation["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]?.GetValue<string>() == "high"
          && generation["tools"] is JsonArray { Count: > 0 },
        "Gemini preflight did not wrap the full native generation request.");
    return """{"totalTokens":987,"promptTokensDetails":[{"modality":"TEXT","tokenCount":987}]}""";
});
using (var transport = new ApiTransport(geminiCountConnection, "fixture", geminiCountHandler))
{
    var count = await new ApiConversationClient(geminiCountConnection, transport).CountInputTokensAsync(
        geminiCountModel, new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = "Count this" }) }),
        "Stable instructions", "high", null, ApiWorkspaceTools.Definitions, default);
    Check(count.InputTokens == 987, "Gemini provider-reported input count was not retained.");
}
Check(!ApiConversationClient.SupportsInputTokenCounting(Connection("openrouter-api")),
    "A generic compatible endpoint was incorrectly advertised as having a native count contract.");

var anthropicArtifactModel = strictModel with
{
    Descriptor = strictModel.Descriptor with
    { Capabilities = strictModel.Descriptor.Capabilities | ModelCapability.GeneratedArtifacts },
    HostedArtifactsEnabled = true
};
using (var transport = new ApiTransport(anthropic, "fixture", new FixtureHandler("")))
{
    var artifactRequest = new ApiConversationClient(anthropic, transport)
        .BuildRequest(anthropicArtifactModel, [], "Create the requested document", "low", null, []);
    Check(artifactRequest["tools"] is JsonArray hostedTools
          && hostedTools.OfType<JsonObject>().Any(tool => tool["type"]?.GetValue<string>() == "code_execution_20250825"
                                                       && tool["name"]?.GetValue<string>() == "code_execution"),
        "Anthropic hosted artifact opt-in did not request native code execution.");
    Check(ApiCapabilityConformance.Evaluate(anthropic, anthropicArtifactModel).Ready.Contains(ModelCapability.GeneratedArtifacts),
        "Anthropic hosted artifacts were not marked adapter-ready after explicit model verification.");
}
foreach (var invalid in new Action[]
{
    () => ApiCapabilityConformance.ValidateTurn(anthropic, strictModel, "invented", "priority", []),
    () => ApiCapabilityConformance.ValidateTurn(anthropic, strictModel, "low", "invented", []),
    () => ApiCapabilityConformance.ValidateTurn(Connection("openai-api"), strictModel, null, null, []),
    () => ApiCapabilityConformance.ValidateTurn(Connection("openai-api"), unknown, null, null, ApiWorkspaceTools.Definitions)
})
{
    try { invalid(); throw new Exception("Invalid provider capability selection was accepted."); }
    catch (InvalidOperationException) { }
}
try
{
    var unsupportedAudio = discovered with { Descriptor = discovered.Descriptor with
    { Capabilities = discovered.Descriptor.Capabilities | ModelCapability.AudioInput } };
    ApiCapabilityConformance.ValidateAttachment(anthropic, unsupportedAudio,
        new FilePart("fixture.wav", "audio/wav"));
    throw new Exception("Unimplemented Anthropic audio delivery was advertised as ready.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("does not implement", StringComparison.Ordinal), "Adapter capability failure did not explain the implementation gap.");
}

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
Check(new ApiConnection("fixture", "ollama-local", "Fixture", "http://localhost:11435/v1/").BaseUri.Port == 11435,
    "A local runtime could not use a user-selected loopback port.");

var testRoot = Path.Combine(Environment.CurrentDirectory, ".artifacts", "api-check", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var pdfPath = Path.Combine(testRoot, "reference.pdf");
var audioPath = Path.Combine(testRoot, "reference.wav");
var videoPath = Path.Combine(testRoot, "reference.mp4");
await File.WriteAllBytesAsync(pdfPath, "%PDF-1.7 fixture"u8.ToArray());
await File.WriteAllBytesAsync(audioPath, "RIFF fixture"u8.ToArray());
await File.WriteAllBytesAsync(videoPath, "video fixture"u8.ToArray());
var generatedDownloadPath = Path.Combine(testRoot, "provider-report.pdf");
using (var transport = new ApiTransport(Connection("openai-api"), "fixture",
           new FixtureHandler("%PDF-1.7 generated fixture")))
{
    var download = await transport.DownloadToFileAsync(
        "containers/cntr_fixture/files/cfile_fixture/content", generatedDownloadPath, 1024 * 1024, default);
    Check(download.ByteLength > 0 && await File.ReadAllTextAsync(generatedDownloadPath) == "%PDF-1.7 generated fixture",
        "Provider artifact download did not retain the returned bytes.");
}
var anthropicGeneratedDownloadPath = Path.Combine(testRoot, "anthropic-report.docx");
var anthropicArtifactHandler = new FixtureHandler("", json: true, pages:
[
    """{"id":"file_fixture","filename":"anthropic-report.docx","mime_type":"application/vnd.openxmlformats-officedocument.wordprocessingml.document","downloadable":true}""",
    "anthropic generated fixture"
]);
using (var transport = new ApiTransport(anthropic, "fixture", anthropicArtifactHandler))
{
    var metadata = await transport.GetAsync("files/file_fixture", default, ["files-api-2025-04-14"]);
    var download = await transport.DownloadToFileAsync("files/file_fixture/content",
        anthropicGeneratedDownloadPath, 1024 * 1024, default, ["files-api-2025-04-14"]);
    Check(metadata["filename"]?.GetValue<string>() == "anthropic-report.docx"
          && download.ByteLength > 0
          && await File.ReadAllTextAsync(anthropicGeneratedDownloadPath) == "anthropic generated fixture",
        "Anthropic artifact metadata/download did not retain the provider filename and bytes.");
    Check(anthropicArtifactHandler.Headers.TryGetValue("anthropic-beta", out var artifactBetas)
          && artifactBetas.Contains("files-api-2025-04-14", StringComparison.Ordinal),
        "Anthropic file download omitted the Files API beta contract.");
}

var openAiPdfHistory = new JsonArray();
var openAiPdfModel = unknown with { Descriptor = unknown.Descriptor with { Capabilities = ModelCapability.Text | ModelCapability.PdfInput } };
using (var transport = new ApiTransport(Connection("openai-api"), "fixture", new FixtureHandler("")))
{
    await new ApiConversationClient(Connection("openai-api"), transport).AddUserAsync(openAiPdfModel, openAiPdfHistory,
        "Inspect the PDF", [new FilePart(pdfPath, "application/pdf", "reference.pdf")], default);
}
var openAiPdf = openAiPdfHistory[0]!["content"]![1]!;
Check(openAiPdf["type"]!.GetValue<string>() == "input_file"
      && openAiPdf["filename"]!.GetValue<string>() == "reference.pdf"
      && openAiPdf["file_data"]!.GetValue<string>().StartsWith("data:application/pdf;base64,", StringComparison.Ordinal),
    "OpenAI Responses PDF input did not use a native input_file block.");

var anthropicPdfHistory = new JsonArray();
var anthropicPdfModel = discovered with { Descriptor = discovered.Descriptor with { Capabilities = discovered.Descriptor.Capabilities | ModelCapability.PdfInput } };
using (var transport = new ApiTransport(anthropic, "fixture", new FixtureHandler("")))
{
    await new ApiConversationClient(anthropic, transport).AddUserAsync(anthropicPdfModel, anthropicPdfHistory,
        "Inspect the PDF", [new FilePart(pdfPath, "application/pdf", "reference.pdf")], default);
}
var anthropicPdf = anthropicPdfHistory[0]!["content"]![1]!;
Check(anthropicPdf["type"]!.GetValue<string>() == "document"
      && anthropicPdf["source"]!["media_type"]!.GetValue<string>() == "application/pdf",
    "Anthropic PDF input did not use a native document block.");

var geminiMediaHistory = new JsonArray();
var geminiConnection = Connection("gemini-api");
var geminiMediaModel = new ApiModel(new ModelDescriptor(geminiConnection.Id, "fixture", "Fixture",
    ModelCapability.Text | ModelCapability.PdfInput | ModelCapability.AudioInput | ModelCapability.VideoInput), new JsonObject(), true, null, false);
using (var transport = new ApiTransport(geminiConnection, "fixture", new FixtureHandler("")))
{
    await new ApiConversationClient(geminiConnection, transport).AddUserAsync(geminiMediaModel, geminiMediaHistory,
        "Inspect these files", [new FilePart(pdfPath, "application/pdf"), new FilePart(audioPath, "audio/wav"), new FilePart(videoPath, "video/mp4")], default);
}
var geminiParts = geminiMediaHistory[0]!["parts"]!.AsArray();
Check(geminiParts.Skip(1).Select(part => part!["inlineData"]!["mimeType"]!.GetValue<string>())
        .SequenceEqual(["application/pdf", "audio/wav", "video/mp4"]),
    "Gemini PDF/audio/video input did not use native inlineData blocks.");

var identityRoot = Path.Combine(testRoot, "identity-store");
var identityStore = new SubscriptionIdentityStore(identityRoot);
var primaryIdentities = await identityStore.LoadAsync();
Check(primaryIdentities.Count == 1 && primaryIdentities[0].IsPrimary,
    "Subscription identity store did not create the primary isolated profile metadata.");
var addedIdentity = await identityStore.AddAsync("Second Plus");
Check(addedIdentity.DisplayName == "Second Plus"
      && !addedIdentity.IsPrimary
      && addedIdentity.ProfileRoot.StartsWith(identityRoot, StringComparison.OrdinalIgnoreCase),
    "Additional subscription profile was not isolated under Harness storage.");
await identityStore.UpdateAsync(addedIdentity with
{
    Email = "fixture@example.com",
    Plan = "plus",
    LastFiveHourRemainingPercent = 82,
    LastUsageAt = DateTimeOffset.UtcNow
});
var loadedIdentities = await identityStore.LoadAsync();
Check(loadedIdentities.Count == 2
      && loadedIdentities.Single(identity => identity.Id == addedIdentity.Id).LastFiveHourRemainingPercent == 82,
    "Subscription account metadata or per-account usage was not retained.");
var claudePrimaryIdentities = await identityStore.LoadAsync(SubscriptionProviderIds.AnthropicClaude);
var addedClaudeIdentity = await identityStore.AddAsync(
    SubscriptionProviderIds.AnthropicClaude, "Second Claude Max");
await identityStore.UpdateAsync(addedClaudeIdentity with
{
    Email = "claude-fixture@example.com",
    Plan = "max",
    LastFiveHourRemainingPercent = 74,
    LastWeeklyRemainingPercent = 61,
    FiveHourResetsAt = DateTimeOffset.UtcNow.AddHours(2),
    WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(3),
    LastModelIds = ["claude-provider-model-a"],
    AutomaticHandoffEnabled = true,
    ConnectionState = "CONNECTED",
    BillingMode = "SUBSCRIPTION",
    LastUsageAt = DateTimeOffset.UtcNow
});
var loadedClaudeIdentities = await identityStore.LoadAsync(SubscriptionProviderIds.AnthropicClaude);
var loadedClaudeIdentity = loadedClaudeIdentities.Single(identity => identity.Id == addedClaudeIdentity.Id);
Check(claudePrimaryIdentities.Count == 1
      && claudePrimaryIdentities[0].ProviderId == SubscriptionProviderIds.AnthropicClaude
      && loadedClaudeIdentities.Count == 2
      && loadedClaudeIdentity.ProfileRoot.Contains(SubscriptionProviderIds.AnthropicClaude, StringComparison.Ordinal)
      && loadedClaudeIdentity.LastWeeklyRemainingPercent == 61
      && loadedClaudeIdentity.LastModelIds!.SequenceEqual(["claude-provider-model-a"])
      && loadedClaudeIdentity.ConnectionSummary == "CONNECTED · SUBSCRIPTION",
    "Claude subscription profiles, model availability, or exact per-account usage was not isolated and retained.");
var selectedHandoffIdentity = SubscriptionHandoffSelector.Select(
    SubscriptionProviderIds.AnthropicClaude,
    claudePrimaryIdentities[0].Id,
    "claude-provider-model-a",
    5,
    [
        loadedClaudeIdentity,
        loadedClaudeIdentity with { Id = "wrong-model", LastFiveHourRemainingPercent = 99, LastModelIds = ["claude-other"] },
        loadedClaudeIdentity with { Id = "api-account", LastFiveHourRemainingPercent = 98, BillingMode = "API/PAYG" },
        loadedClaudeIdentity with { Id = "manual-account", LastFiveHourRemainingPercent = 97, AutomaticHandoffEnabled = false }
    ]);
Check(selectedHandoffIdentity?.Id == loadedClaudeIdentity.Id,
    "Automatic handoff selected an API, opted-out, cross-model, or otherwise ineligible account.");
var identityJson = await File.ReadAllTextAsync(Path.Combine(identityRoot, "subscription-identities.json"));
Check(!identityJson.Contains("token", StringComparison.OrdinalIgnoreCase)
      && !identityJson.Contains("password", StringComparison.OrdinalIgnoreCase),
    "Credential material was written to Harness subscription metadata.");
var unsafeIdentityRejected = false;
try { await identityStore.UpdateAsync(addedIdentity with { ProfileRoot = Path.Combine(testRoot, "escape") }); }
catch (InvalidDataException) { unsafeIdentityRejected = true; }
Check(unsafeIdentityRejected, "A managed subscription profile escaped Harness storage.");
var approvalCount = 0;
var runner = new ApiWorkspaceTools(testRoot, (_, _, _) => { approvalCount++; return Task.FromResult(false); });
var denied = await runner.ExecuteAsync(new("call", "write_file", """{"path":"a.txt","content":"unchanged"}"""), default);
Check(approvalCount == 1 && !File.Exists(Path.Combine(testRoot, "a.txt")) && denied.StartsWith("User declined", StringComparison.Ordinal), "Denied write executed.");
Check((await runner.ExecuteAsync(new("call", "read_file", """{"path":"../outside.txt"}"""), default)).StartsWith("Tool failed", StringComparison.Ordinal), "Path traversal allowed.");
Check((await runner.ExecuteAsync(new("call", "read_file", """{"path":".git/config"}"""), default)).StartsWith("Tool failed", StringComparison.Ordinal), "Git internals exposed.");
await runner.ExecuteAsync(new("call", "run_command", """{"command":"this command must never start"}"""), default);
Check(approvalCount == 2, "Command bypassed approval.");

var apiSkillWorkspace = Path.Combine(testRoot, "api-skill-workspace");
var otherApiSkillWorkspace = Path.Combine(testRoot, "other-api-skill-workspace");
var apiSkillPackage = Path.Combine(testRoot, "api-skill-package");
Directory.CreateDirectory(apiSkillWorkspace);
Directory.CreateDirectory(otherApiSkillWorkspace);
Directory.CreateDirectory(apiSkillPackage);
await File.WriteAllTextAsync(Path.Combine(apiSkillPackage, "SKILL.md"),
    "---\nname: fixture-skill\ndescription: Explains deterministic fixture testing.\n---\n\nUse the fixture exactly.\n");
await File.WriteAllTextAsync(Path.Combine(apiSkillPackage, "reference.md"), "reference body");
var apiSkillCatalog = new SkillCatalogEntry(
    "api-skill-catalog", "fixture-skill", "Explains deterministic fixture testing.", "Testing", "fixture/api-skills",
    "fixture/SKILL.md", "revision", "https://example.invalid/api-skill", "Portable Agent Skill", "FIXTURE",
    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
var cachedSkillRoot = Path.Combine(testRoot, "cached-skill-packages");
var cachedSkillDirectory = Path.Combine(cachedSkillRoot,
    SkillManifestParser.Slug(apiSkillCatalog.Name) + "--" + apiSkillCatalog.Id[..12], apiSkillCatalog.SourceRevision);
Directory.CreateDirectory(cachedSkillDirectory);
var cachedSkillBytes = Encoding.UTF8.GetBytes("cached skill body");
await File.WriteAllBytesAsync(Path.Combine(cachedSkillDirectory, "SKILL.md"), cachedSkillBytes);
var cachedSkillHashInput = Encoding.UTF8.GetBytes("SKILL.md\0").Concat(cachedSkillBytes).ToArray();
var cachedSkillHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cachedSkillHashInput)).ToLowerInvariant();
await File.WriteAllTextAsync(Path.Combine(cachedSkillDirectory, ".harness-package.json"), JsonSerializer.Serialize(new
{
    apiSkillCatalog.Id,
    apiSkillCatalog.Repository,
    apiSkillCatalog.SkillPath,
    apiSkillCatalog.SourceRevision,
    contentSha256 = cachedSkillHash,
    fileCount = 1,
    byteLength = cachedSkillBytes.Length
}));
var cachedPackage = await new GitHubCliClient().DownloadSkillPackageAsync(
    apiSkillCatalog, new SkillPackageInspection([], 0, 0, []), cachedSkillRoot);
Check(cachedPackage.ContentSha256 == cachedSkillHash, "A valid pinned skill cache was not reused.");
await File.WriteAllTextAsync(Path.Combine(cachedSkillDirectory, "SKILL.md"), "tampered skill body");
var tamperedCacheRejected = false;
try
{
    await new GitHubCliClient().DownloadSkillPackageAsync(
        apiSkillCatalog, new SkillPackageInspection([], 0, 0, []), cachedSkillRoot);
}
catch (InvalidOperationException) { tamperedCacheRejected = true; }
Check(tamperedCacheRejected, "A modified pinned skill cache was reused without verifying its content hash.");
var unsafeSkillRevisionRejected = false;
try
{
    await new GitHubCliClient().DownloadSkillPackageAsync(
        apiSkillCatalog with { SourceRevision = "../escape" }, new SkillPackageInspection([], 0, 0, []), cachedSkillRoot);
}
catch (InvalidOperationException) { unsafeSkillRevisionRejected = true; }
Check(unsafeSkillRevisionRejected, "An unsafe catalog revision escaped or reached the skill package cache.");
var apiSkillInstallPath = await SkillPackageInstaller.InstallHarnessApiAsync(
    new DownloadedSkillPackage(apiSkillPackage, "fixture-content", 2, 100), apiSkillCatalog,
    "api-connection-one", "WORKSPACE", apiSkillWorkspace, "fixture-model");
var claudeSkillInstallPath = await SkillPackageInstaller.InstallClaudeCodeAsync(
    new DownloadedSkillPackage(apiSkillPackage, "fixture-content", 2, 100), apiSkillCatalog,
    "WORKSPACE", apiSkillWorkspace);
Check(claudeSkillInstallPath.StartsWith(Path.Combine(apiSkillWorkspace, ".claude", "skills"), StringComparison.OrdinalIgnoreCase)
      && File.Exists(Path.Combine(claudeSkillInstallPath, "SKILL.md")),
    "Claude Code skill installation did not use its provider-native workspace path.");
var apiSkillInstallation = new InstalledSkill(
    SkillPackageInstaller.CreateInstallId(apiSkillCatalog.Id, "api-connection-one", "WORKSPACE", apiSkillWorkspace, "fixture-model"),
    apiSkillCatalog.Id, apiSkillCatalog.Name, apiSkillCatalog.SourceRevision, apiSkillPackage, apiSkillInstallPath,
    "WORKSPACE", apiSkillWorkspace, "api-connection-one", "fixture-model", "fixture-content", true, DateTimeOffset.UtcNow);
var apiSkills = await ApiSkillTools.CreateAsync([apiSkillInstallation], "api-connection-one", "fixture-model", apiSkillWorkspace);
Check(apiSkills.Count == 1 && ApiSkillTools.Definitions.Count == 2,
    "A direct-API skill was not activated with its discovery/read tools.");
var listedSkills = await apiSkills.ExecuteAsync(new("skills", ApiSkillTools.ListName, """{"query":"deterministic"}"""));
Check(listedSkills.Contains(Path.GetFileName(apiSkillInstallPath), StringComparison.Ordinal)
      && listedSkills.Contains("fixture testing", StringComparison.OrdinalIgnoreCase),
    "The direct-API skill catalog did not expose its stable ID and description.");
var readSkill = await apiSkills.ExecuteAsync(new("skill", ApiSkillTools.ReadName,
    $$"""{"skill_id":"{{Path.GetFileName(apiSkillInstallPath)}}","path":"reference.md"}"""));
Check(readSkill.Contains("reference body", StringComparison.Ordinal)
      && (await apiSkills.ExecuteAsync(new("escape", ApiSkillTools.ReadName,
          $$"""{"skill_id":"{{Path.GetFileName(apiSkillInstallPath)}}","path":"../outside.txt"}"""))).StartsWith("Tool failed", StringComparison.Ordinal),
    "Skill resource reads failed or escaped the installed package.");
Check((await ApiSkillTools.CreateAsync([apiSkillInstallation], "different-connection", "fixture-model", apiSkillWorkspace)).Count == 0
      && (await ApiSkillTools.CreateAsync([apiSkillInstallation], "api-connection-one", "different-model", apiSkillWorkspace)).Count == 0
      && (await ApiSkillTools.CreateAsync([apiSkillInstallation], "api-connection-one", "fixture-model", otherApiSkillWorkspace)).Count == 0,
    "A direct-API skill leaked across provider connections, models, or workspace scope.");
Check(await SkillPackageInstaller.InspectManagedCopyAsync(apiSkillInstallation) == ManagedSkillIntegrity.Unchanged,
    "A newly installed skill did not receive a valid integrity baseline.");
await File.WriteAllTextAsync(Path.Combine(apiSkillInstallation.InstallPath, "reference.md"), "locally modified");
Check(await SkillPackageInstaller.InspectManagedCopyAsync(apiSkillInstallation) == ManagedSkillIntegrity.Modified,
    "Local modifications to a provider-facing skill copy were not detected.");
await File.WriteAllTextAsync(Path.Combine(apiSkillInstallation.InstallPath, "reference.md"), "reference body");
var disabledApiSkill = await SkillPackageInstaller.SetEnabledAsync(apiSkillInstallation, false);
Check(!disabledApiSkill.Enabled
      && !Directory.Exists(apiSkillInstallation.InstallPath)
      && Directory.Exists(disabledApiSkill.InstallPath)
      && (await ApiSkillTools.CreateAsync([disabledApiSkill], "api-connection-one", "fixture-model", apiSkillWorkspace)).Count == 0,
    "Disabling a skill did not remove it from the provider discovery path.");
apiSkillInstallation = await SkillPackageInstaller.SetEnabledAsync(disabledApiSkill, true);
Check(apiSkillInstallation.Enabled && Directory.Exists(apiSkillInstallation.InstallPath),
    "A disabled skill could not be restored to its provider discovery path.");
var apiSkillPackageV2 = Path.Combine(testRoot, "api-skill-package-v2");
Directory.CreateDirectory(apiSkillPackageV2);
await File.WriteAllTextAsync(Path.Combine(apiSkillPackageV2, "SKILL.md"),
    "---\nname: fixture-skill\ndescription: Explains updated deterministic fixture testing.\n---\n");
await File.WriteAllTextAsync(Path.Combine(apiSkillPackageV2, "reference.md"), "reference body v2");
var apiSkillCatalogV2 = apiSkillCatalog with { SourceRevision = "revision-v2", Description = "Updated fixture skill" };
var apiSkillDownloadV2 = new DownloadedSkillPackage(apiSkillPackageV2, "fixture-content-v2", 2, 110);
var pendingSkillUpdate = await SkillPackageInstaller.UpdateAsync(apiSkillInstallation, apiSkillDownloadV2, apiSkillCatalogV2);
Check(Directory.Exists(pendingSkillUpdate.BackupPath)
      && await File.ReadAllTextAsync(Path.Combine(pendingSkillUpdate.InstallPath, "reference.md")) == "reference body v2",
    "Skill update did not retain a rollback copy or activate the reviewed revision.");
await SkillPackageInstaller.RollbackUpdateAsync(pendingSkillUpdate, enabled: true);
Check(await File.ReadAllTextAsync(Path.Combine(apiSkillInstallation.InstallPath, "reference.md")) == "reference body",
    "A failed skill update could not roll back its provider-facing copy.");
pendingSkillUpdate = await SkillPackageInstaller.UpdateAsync(apiSkillInstallation, apiSkillDownloadV2, apiSkillCatalogV2);
apiSkillInstallation = apiSkillInstallation with
{
    SourceRevision = apiSkillCatalogV2.SourceRevision,
    PackagePath = apiSkillDownloadV2.PackagePath,
    ContentSha256 = apiSkillDownloadV2.ContentSha256,
    InstallPath = pendingSkillUpdate.InstallPath
};
SkillPackageInstaller.CommitUpdate(pendingSkillUpdate);
Check(!Directory.Exists(pendingSkillUpdate.BackupPath)
      && await SkillPackageInstaller.InspectManagedCopyAsync(apiSkillInstallation) == ManagedSkillIntegrity.Unchanged,
    "Committing a skill update retained its rollback directory or lost its integrity baseline.");
var removedApiSkill = await SkillPackageInstaller.RemoveRecoverablyAsync(apiSkillInstallation);
Check(!Directory.Exists(apiSkillInstallation.InstallPath) && Directory.Exists(removedApiSkill),
    "Skill removal did not leave a recoverable local copy.");
await SkillPackageInstaller.RestoreRemovedAsync(removedApiSkill, apiSkillInstallation);
Check(Directory.Exists(apiSkillInstallation.InstallPath), "A recoverably removed skill could not be restored after a storage failure.");
await using (var skillLifecycleStore = new Harness.Storage.HarnessStore(Path.Combine(testRoot, "skill-lifecycle.db")))
{
    await skillLifecycleStore.InitializeAsync();
    await skillLifecycleStore.UpsertSkillCatalogAsync([apiSkillCatalogV2]);
    await skillLifecycleStore.SaveInstalledSkillAsync(apiSkillInstallation);
    await skillLifecycleStore.DeleteInstalledSkillAsync(apiSkillInstallation.Id);
    Check((await skillLifecycleStore.ListInstalledSkillsAsync()).Count == 0,
        "Removing an installed-skill record did not persist.");
}
var skillCompatibilityProbe = new SettingsWindowViewModel(new HarnessApplicationSettings(), apiSkillWorkspace);
var apiCompatibility = new SkillCompatibilityOption(
    "api-connection-one:fixture-model", "api-connection-one", "fixture-model", "Fixture API · Model",
    CompatibilityProviderId: "openai-api");
skillCompatibilityProbe.SetCompatibilityTargets([apiCompatibility]);
skillCompatibilityProbe.SelectedSkillCompatibility = skillCompatibilityProbe.SkillCompatibilityOptions[1];
var codexOnlyCatalog = apiSkillCatalog with { Id = "codex-only", Name = "codex-only", Compatibility = "Codex extension" };
skillCompatibilityProbe.ReplaceSkills([apiSkillCatalogV2, codexOnlyCatalog], [apiSkillInstallation], installTargets:
[
    new SkillInstallTarget("api-connection-one", "Fixture API · Model", "fixture-model", "harness-api", "openai-api")
]);
Check(skillCompatibilityProbe.Skills.Count == 1
      && skillCompatibilityProbe.Skills[0].Entry.Id == apiSkillCatalogV2.Id
      && skillCompatibilityProbe.Skills[0].IsInstalled,
    "Direct-API model filtering exposed a Codex-only extension or lost target-specific install state.");
skillCompatibilityProbe.ReplaceSkills([apiSkillCatalogV2], [apiSkillInstallation with { Enabled = false }]);
Check(skillCompatibilityProbe.Skills.Single().IsInstalled
      && skillCompatibilityProbe.SelectedSkillInstallation?.Installation.Enabled == false,
    "A disabled skill disappeared from installed-target management and could not be re-enabled.");
var otherModelCompatibility = apiCompatibility with { Id = "api-connection-one:other-model", ModelId = "other-model" };
skillCompatibilityProbe.SetCompatibilityTargets([otherModelCompatibility]);
skillCompatibilityProbe.SelectedSkillCompatibility = skillCompatibilityProbe.SkillCompatibilityOptions[1];
skillCompatibilityProbe.ReplaceSkills([apiSkillCatalogV2], [apiSkillInstallation]);
Check(skillCompatibilityProbe.Skills.Single().InstallState == "AVAILABLE",
    "A model-specific installation was presented as installed for another model.");

var alphaKey = MainWindowViewModel.ModelPreferenceKey("openai-codex", "alpha");
var betaKey = MainWindowViewModel.ModelPreferenceKey("openai-codex", "beta");
var gammaKey = MainWindowViewModel.ModelPreferenceKey("api-fixture", "gamma");
var preferenceSettings = new HarnessApplicationSettings(
    HiddenModelIds: [betaKey, "disconnected::hidden"],
    FavoriteModelIds: [gammaKey, "disconnected::favorite"],
    ModelOrder: [gammaKey, alphaKey, betaKey, "disconnected::ordered"]);
var modelPreferenceProbe = new MainWindowViewModel();
modelPreferenceProbe.ApplyApplicationSettings(preferenceSettings);
modelPreferenceProbe.ApplyProviderModels("openai-codex",
    [new("openai-codex", "alpha", "Alpha", ModelCapability.Text), new("openai-codex", "beta", "Beta", ModelCapability.Text)],
    "OpenAI Codex", "FIXTURE");
modelPreferenceProbe.ApplyProviderModels("api-fixture",
    [new("api-fixture", "gamma", "Gamma", ModelCapability.Text)], "Fixture API", "FIXTURE");
Check(modelPreferenceProbe.Models.Select(model => model.ModelName).SequenceEqual(["gamma", "alpha"])
      && modelPreferenceProbe.ReportedModels.Count == 3,
    "Hidden or favorite model preferences were not applied to the composer catalog.");
var preferenceEditor = new SettingsWindowViewModel(preferenceSettings, testRoot);
preferenceEditor.SetModelPreferences([
    new(alphaKey, "openai-codex", "alpha", "OpenAI Codex · Alpha"),
    new(betaKey, "openai-codex", "beta", "OpenAI Codex · Beta"),
    new(gammaKey, "api-fixture", "gamma", "Fixture API · Gamma")]);
preferenceEditor.ModelPreferences.Single(item => item.Key == alphaKey).IsEnabled = false;
var savedPreferences = preferenceEditor.ToSettings();
var savedHidden = savedPreferences.HiddenModelIds ?? [];
var savedFavorites = savedPreferences.FavoriteModelIds ?? [];
var savedOrder = savedPreferences.ModelOrder ?? [];
Check(savedHidden.Contains(alphaKey)
      && savedHidden.Contains(betaKey)
      && savedHidden.Contains("disconnected::hidden")
      && savedFavorites.Contains("disconnected::favorite")
      && savedOrder.Contains("disconnected::ordered"),
    "Saving current model preferences discarded hidden-provider settings or new choices.");
var diagnosticsRoot = Path.Combine(testRoot, "diagnostics");
var diagnostics = new CrashDiagnosticsService(diagnosticsRoot);
var diagnosticPath = await diagnostics.WriteNonfatalReportAsync(
    new InvalidOperationException("secret-ghp_fixture E:\\private\\workspace\\prompt.txt"),
    "fixture");
Check(diagnosticPath is not null && File.Exists(diagnosticPath), "Sanitized diagnostic report was not created.");
var diagnosticJson = await File.ReadAllTextAsync(diagnosticPath!);
Check(diagnosticJson.Contains("System.InvalidOperationException", StringComparison.Ordinal)
      && !diagnosticJson.Contains("secret-ghp_fixture", StringComparison.Ordinal)
      && !diagnosticJson.Contains("private", StringComparison.OrdinalIgnoreCase)
      && !diagnosticJson.Contains("prompt.txt", StringComparison.OrdinalIgnoreCase),
    "Diagnostic report omitted the failure type or retained sensitive exception content.");
var pendingDiagnostics = Path.Combine(diagnosticsRoot, "pending");
Directory.CreateDirectory(pendingDiagnostics);
await File.WriteAllTextAsync(Path.Combine(pendingDiagnostics, "stale.json"), JsonSerializer.Serialize(new
{
    SessionId = "stale-fixture",
    ProcessId = int.MaxValue,
    ProcessStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
    ProductVersion = "fixture"
}));
var recovery = await diagnostics.StartSessionAsync();
Check(recovery is { RecoveredSessionCount: 1 } && File.Exists(recovery.ReportPath),
    "Stale session marker did not produce a crash-recovery notice and report.");
Check(Directory.EnumerateFiles(Path.Combine(diagnosticsRoot, "pending"), "*.json").Count() == 1,
    "Active session marker was not created.");
await diagnostics.CompleteSessionAsync();
Check(!Directory.EnumerateFiles(Path.Combine(diagnosticsRoot, "pending"), "*.json").Any(),
    "Clean shutdown left a crash-recovery marker behind.");

var sessionLifecycleRoot = Path.Combine(testRoot, "session-lifecycle");
Directory.CreateDirectory(sessionLifecycleRoot);
await using (var sessionStore = new Harness.Storage.HarnessStore(Path.Combine(sessionLifecycleRoot, "harness.db")))
{
    await sessionStore.InitializeAsync();
    var workspace = await sessionStore.OpenWorkspaceAsync(sessionLifecycleRoot);
    var original = workspace.ActiveSession;
    await sessionStore.RenameSessionAsync(original.Id, "Original task");
    var current = await sessionStore.CreateSessionAsync(workspace.Project.Id, "Current task");
    var archived = await sessionStore.SetSessionArchivedAsync(original.Id, true);
    Check(archived.ArchivedAt is not null, "Archiving a task did not persist its archive timestamp.");
    var reopened = await sessionStore.OpenWorkspaceAsync(sessionLifecycleRoot);
    Check(reopened.Sessions.Count == 1 && reopened.ActiveSession.Id == current.Id,
        "An archived task remained in the active workspace rail.");
    var allTasks = await sessionStore.SearchSessionsAsync(workspace.Project.Id);
    var archivedTasks = await sessionStore.SearchSessionsAsync(workspace.Project.Id, archived: true);
    var searchedTasks = await sessionStore.SearchSessionsAsync(workspace.Project.Id, "Current", archived: false);
    Check(allTasks.Count == 2 && archivedTasks.Single().Id == original.Id && searchedTasks.Single().Id == current.Id,
        "Task Library search or archive filtering lost durable sessions.");
    var restored = await sessionStore.SetSessionArchivedAsync(original.Id, false);
    Check(restored.ArchivedAt is null && (await sessionStore.SearchSessionsAsync(workspace.Project.Id, archived: false)).Count == 2,
        "Restoring an archived task did not return it to the active task set.");

    await sessionStore.UpsertMessageAsync(new StoredMessage(
        Guid.NewGuid().ToString("N"), current.Id, 0, "YOU", "Prompt", "export fixture", "COMPLETED",
        "#8993A3", false, DateTimeOffset.UtcNow));
    var loaded = await sessionStore.LoadSessionAsync(current.Id);
    var markdownExport = Path.Combine(sessionLifecycleRoot, "task.md");
    var jsonExport = Path.Combine(sessionLifecycleRoot, "task.json");
    var exporter = new SessionExportService();
    await exporter.ExportAsync(markdownExport, loaded.Session, loaded.Messages, loaded.Attachments);
    await exporter.ExportAsync(jsonExport, loaded.Session, loaded.Messages, loaded.Attachments);
    Check((await File.ReadAllTextAsync(markdownExport)).Contains("export fixture", StringComparison.Ordinal)
          && JsonNode.Parse(await File.ReadAllTextAsync(jsonExport))?["Format"]?.GetValue<string>() == "harness.session.v1",
        "Markdown or structured JSON task export omitted durable history.");
}

var legacyDatabase = Path.Combine(testRoot, "schema-v5.db");
await using (var legacy = new SqliteConnection($"Data Source={legacyDatabase}"))
{
    await legacy.OpenAsync();
    await using var create = legacy.CreateCommand();
    create.CommandText = """
        CREATE TABLE schema_info(version INTEGER NOT NULL);
        INSERT INTO schema_info(version) VALUES(5);
        CREATE TABLE sessions(
            id TEXT PRIMARY KEY, project_id TEXT NOT NULL, title TEXT NOT NULL,
            provider_id TEXT NULL, provider_thread_id TEXT NULL, model_id TEXT NULL,
            reasoning_effort TEXT NULL, service_tier TEXT NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        """;
    await create.ExecuteNonQueryAsync();
}
await using (var migrated = new Harness.Storage.HarnessStore(legacyDatabase))
    await migrated.InitializeAsync();
await using (var inspect = new SqliteConnection($"Data Source={legacyDatabase}"))
{
    await inspect.OpenAsync();
    await using var version = inspect.CreateCommand();
    version.CommandText = "SELECT version FROM schema_info;";
    Check(Convert.ToInt32(await version.ExecuteScalarAsync()) == Harness.Storage.HarnessStore.CurrentSchemaVersion,
        "The task archive schema did not migrate an existing Harness database.");
    await using var columns = inspect.CreateCommand();
    columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'archived_at';";
    Check(Convert.ToInt32(await columns.ExecuteScalarAsync()) == 1,
        "The migrated sessions table is missing durable archive state.");
}

var portableSourceRoot = Path.Combine(testRoot, "portable-source");
var portableSourceDatabase = Path.Combine(portableSourceRoot, "data", "harness.db");
var portableSourceApi = Path.Combine(portableSourceRoot, "api-connections.json");
var portableArchive = Path.Combine(testRoot, "fixture.harness-backup");
var portableWorkspaceRoot = Path.Combine(portableSourceRoot, "workspace-scope");
string portableSessionId;
await using (var portableSourceStore = new Harness.Storage.HarnessStore(portableSourceDatabase))
{
    await portableSourceStore.InitializeAsync();
    var portableWorkspace = await portableSourceStore.OpenWorkspaceAsync(testRoot);
    portableSessionId = portableWorkspace.ActiveSession.Id;
    await portableSourceStore.SaveApplicationSettingsAsync(new HarnessApplicationSettings(PersonalInstructions: "portable fixture"));
    await portableSourceStore.UpsertMessageAsync(new StoredMessage(
        Guid.NewGuid().ToString("N"), portableSessionId, 0, "YOU", "Prompt", "portable message", "COMPLETED", "#8993A3", false, DateTimeOffset.UtcNow));
    var contextSource = Path.Combine(portableSourceRoot, "context.txt");
    Directory.CreateDirectory(portableSourceRoot);
    await File.WriteAllTextAsync(contextSource, "portable context");
    await portableSourceStore.AddAttachmentAsync(portableSessionId, contextSource);
    var portableArtifact = Path.Combine(portableSourceRoot, "data", "artifacts", portableSessionId, "report.pdf");
    Directory.CreateDirectory(Path.GetDirectoryName(portableArtifact)!);
    await File.WriteAllTextAsync(portableArtifact, "%PDF portable artifact");
    var portableSkill = new SkillCatalogEntry(
        "portable-skill", "portable-skill", "Portable backup fixture", "Testing", "fixture/skills",
        "skills/portable-skill/SKILL.md", "fixture-revision", "https://example.invalid/portable-skill",
        "Portable Agent Skill", "FIXTURE", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await portableSourceStore.UpsertSkillCatalogAsync([portableSkill]);
    var portableSkillPath = Path.Combine(portableSourceRoot, "global-skills", "portable-skill-folder");
    Directory.CreateDirectory(portableSkillPath);
    await File.WriteAllTextAsync(Path.Combine(portableSkillPath, "SKILL.md"), "---\nname: portable-skill\n---\n");
    await File.WriteAllTextAsync(Path.Combine(portableSkillPath, ".harness-source.json"), JsonSerializer.Serialize(new
    {
        catalogId = portableSkill.Id,
        installedName = "portable-skill",
        originalName = portableSkill.Name,
        portableSkill.Repository,
        portableSkill.SkillPath,
        portableSkill.SourceRevision
    }));
    await portableSourceStore.SaveInstalledSkillAsync(new InstalledSkill(
        "portable-skill-install", portableSkill.Id, portableSkill.Name, portableSkill.SourceRevision,
        portableSkillPath, portableSkillPath, "GLOBAL", null, "openai-codex", null,
        "fixture-content", true, DateTimeOffset.UtcNow));
    var portableApiSkillPath = Path.Combine(portableSourceRoot, "api-active", "portable-api-skill");
    Directory.CreateDirectory(portableApiSkillPath);
    await File.WriteAllTextAsync(Path.Combine(portableApiSkillPath, "SKILL.md"), "---\nname: portable-api-skill\n---\n");
    await File.WriteAllTextAsync(Path.Combine(portableApiSkillPath, ".harness-source.json"), JsonSerializer.Serialize(new
    {
        catalogId = portableSkill.Id,
        installedName = "portable-api-skill",
        originalName = portableSkill.Name,
        portableSkill.Repository,
        portableSkill.SkillPath,
        portableSkill.SourceRevision
    }));
    await portableSourceStore.SaveInstalledSkillAsync(new InstalledSkill(
        "portable-api-skill-install", portableSkill.Id, portableSkill.Name, portableSkill.SourceRevision,
        portableApiSkillPath, portableApiSkillPath, "USER", null, "portable-api-connection", "fixture-model",
        "fixture-api-content", true, DateTimeOffset.UtcNow));
    var portableDisabledSkillPath = Path.Combine(portableSourceRoot, "disabled-skills", "portable-disabled-skill");
    Directory.CreateDirectory(portableDisabledSkillPath);
    await File.WriteAllTextAsync(Path.Combine(portableDisabledSkillPath, "SKILL.md"), "---\nname: portable-disabled-skill\n---\n");
    await File.WriteAllTextAsync(Path.Combine(portableDisabledSkillPath, ".harness-source.json"), JsonSerializer.Serialize(new
    {
        catalogId = portableSkill.Id,
        installedName = "portable-disabled-skill",
        originalName = portableSkill.Name,
        portableSkill.Repository,
        portableSkill.SkillPath,
        portableSkill.SourceRevision
    }));
    await portableSourceStore.SaveInstalledSkillAsync(new InstalledSkill(
        "portable-disabled-install", portableSkill.Id, portableSkill.Name, portableSkill.SourceRevision,
        portableDisabledSkillPath, portableDisabledSkillPath, "GLOBAL", null, "openai-codex", "disabled-model",
        "fixture-disabled-content", false, DateTimeOffset.UtcNow));
    var portableWorkspaceSkillPath = Path.Combine(portableWorkspaceRoot, ".agents", "skills", "portable-workspace-skill");
    Directory.CreateDirectory(portableWorkspaceSkillPath);
    await File.WriteAllTextAsync(Path.Combine(portableWorkspaceSkillPath, "SKILL.md"), "---\nname: portable-workspace-skill\n---\n");
    await File.WriteAllTextAsync(Path.Combine(portableWorkspaceSkillPath, ".harness-source.json"), JsonSerializer.Serialize(new
    {
        catalogId = portableSkill.Id,
        installedName = "portable-workspace-skill",
        originalName = portableSkill.Name,
        portableSkill.Repository,
        portableSkill.SkillPath,
        portableSkill.SourceRevision
    }));
    await portableSourceStore.SaveInstalledSkillAsync(new InstalledSkill(
        "portable-workspace-skill-install", portableSkill.Id, portableSkill.Name, portableSkill.SourceRevision,
        portableWorkspaceSkillPath, portableWorkspaceSkillPath, "WORKSPACE", portableWorkspaceRoot, "openai-codex", null,
        "fixture-workspace-content", true, DateTimeOffset.UtcNow));
    await File.WriteAllTextAsync(portableSourceApi, "[{\"name\":\"fixture endpoint metadata\"}]");
    var export = await new PortableBackupService(
        portableSourceStore,
        portableSourceRoot,
        portableSourceDatabase,
        portableSourceApi).CreateAsync(portableArchive);
    Check(export.PayloadCount >= 3 && export.ArchiveBytes > 0, "Portable backup omitted required payloads.");
}

var portableTargetRoot = Path.Combine(testRoot, "portable-target");
var portableTargetDatabase = Path.Combine(portableTargetRoot, "data", "harness.db");
var portableTargetApi = Path.Combine(portableTargetRoot, "api-connections.json");
var portableTargetSkills = Path.Combine(portableTargetRoot, "global-skills");
var portableTargetApiSkills = Path.Combine(portableTargetRoot, "api-skills");
await using (var previousTargetStore = new Harness.Storage.HarnessStore(portableTargetDatabase))
{
    await previousTargetStore.InitializeAsync();
    await previousTargetStore.SaveApplicationSettingsAsync(new HarnessApplicationSettings(PersonalInstructions: "replace me"));
}
var restoreService = new PortableBackupService(null, portableTargetRoot, portableTargetDatabase, portableTargetApi,
    portableTargetSkills, portableTargetApiSkills);
var staged = await restoreService.StageRestoreAsync(portableArchive);
Check(restoreService.HasPendingRestore && staged.PayloadCount >= 3, "Portable restore was not staged after archive validation.");
var applied = await restoreService.ApplyPendingRestoreAsync();
Check(applied is not null && !restoreService.HasPendingRestore && File.Exists(portableTargetDatabase + ".before-restore"),
    "Portable restore did not apply cleanly or retain the previous database.");
await using (var restoredStore = new Harness.Storage.HarnessStore(portableTargetDatabase))
{
    await restoredStore.InitializeAsync();
    var restoredSettings = await restoredStore.LoadApplicationSettingsAsync();
    var restoredSession = await restoredStore.LoadSessionAsync(portableSessionId);
    var restoredSkills = await restoredStore.ListInstalledSkillsAsync();
    var restoredGlobalSkill = restoredSkills.Single(skill => skill.Id == "portable-skill-install");
    var restoredApiSkill = restoredSkills.Single(skill => skill.ProviderId == "portable-api-connection");
    var restoredDisabledSkill = restoredSkills.Single(skill => skill.Id == "portable-disabled-install");
    var deferredWorkspaceSkill = restoredSkills.Single(skill => skill.Scope == "WORKSPACE");
    Check(restoredSettings.PersonalInstructions == "portable fixture"
          && restoredSession.Messages.Any(message => message.Text == "portable message")
          && restoredSession.Attachments.Count == 1
          && restoredSession.Attachments[0].StoredPath.StartsWith(Path.Combine(portableTargetRoot, "data"), StringComparison.OrdinalIgnoreCase)
          && await File.ReadAllTextAsync(restoredSession.Attachments[0].StoredPath) == "portable context"
          && await File.ReadAllTextAsync(Path.Combine(portableTargetRoot, "data", "artifacts", portableSessionId, "report.pdf")) == "%PDF portable artifact"
          && restoredGlobalSkill.Enabled
          && restoredGlobalSkill.InstallPath.StartsWith(portableTargetSkills, StringComparison.OrdinalIgnoreCase)
          && File.Exists(Path.Combine(restoredGlobalSkill.InstallPath, "SKILL.md"))
          && restoredApiSkill.Enabled
          && restoredApiSkill.ModelId == "fixture-model"
          && restoredApiSkill.InstallPath.StartsWith(portableTargetApiSkills, StringComparison.OrdinalIgnoreCase)
          && File.Exists(Path.Combine(restoredApiSkill.InstallPath, "SKILL.md"))
          && !restoredDisabledSkill.Enabled
          && restoredDisabledSkill.InstallPath.Contains(".harness-disabled-skills", StringComparison.OrdinalIgnoreCase)
          && !restoredDisabledSkill.InstallPath.StartsWith(portableTargetSkills, StringComparison.OrdinalIgnoreCase)
          && !deferredWorkspaceSkill.Enabled
          && deferredWorkspaceSkill.InstallPath.StartsWith(Path.Combine(portableTargetRoot, "data", "deferred-skills"), StringComparison.OrdinalIgnoreCase),
        "Portable restore lost settings, chat history, rebased context, or an installed skill.");
    var activatedSkillWorkspace = Path.Combine(testRoot, "activated-skill-workspace");
    Directory.CreateDirectory(activatedSkillWorkspace);
    Check(await restoreService.ActivateDeferredWorkspaceSkillsAsync(
              restoredStore, portableWorkspaceRoot, activatedSkillWorkspace) == 1,
        "Relinking did not activate a deferred workspace skill.");
    var activatedSkill = (await restoredStore.ListInstalledSkillsAsync()).Single(skill => skill.Scope == "WORKSPACE");
    Check(activatedSkill.Enabled
          && activatedSkill.WorkspacePath == Path.GetFullPath(activatedSkillWorkspace)
          && File.Exists(Path.Combine(activatedSkill.InstallPath, "SKILL.md")),
        "Deferred workspace skill was not restored into the user-selected project.");
    var relocatedPath = Path.Combine(testRoot, "relocated-workspace");
    Directory.CreateDirectory(relocatedPath);
    var restoredProject = (await restoredStore.ListProjectsAsync()).Single();
    await restoredStore.RelocateProjectAsync(restoredProject.Id, relocatedPath);
    Check((await restoredStore.ListProjectsAsync()).Single().RootPath == Path.GetFullPath(relocatedPath)
          && (await restoredStore.LoadSessionAsync(portableSessionId)).Messages.Any(message => message.Text == "portable message"),
        "Relinking a restored workspace lost its project identity or chat history.");
}
Check(await File.ReadAllTextAsync(portableTargetApi) == "[{\"name\":\"fixture endpoint metadata\"}]",
    "Portable restore lost credential-free API connection metadata.");

var unsafeArchive = Path.Combine(testRoot, "unsafe.harness-backup");
var unsafeBytes = Encoding.UTF8.GetBytes("x");
var unsafePayload = new PortableBackupPayload(
    "../escape.txt",
    unsafeBytes.Length,
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(unsafeBytes)).ToLowerInvariant());
using (var unsafeFile = new FileStream(unsafeArchive, FileMode.CreateNew, FileAccess.Write))
using (var unsafeZip = new System.IO.Compression.ZipArchive(unsafeFile, System.IO.Compression.ZipArchiveMode.Create))
{
    using (var payloadStream = unsafeZip.CreateEntry(unsafePayload.Path).Open()) payloadStream.Write(unsafeBytes);
    using var manifestStream = unsafeZip.CreateEntry("manifest.json").Open();
    JsonSerializer.Serialize(manifestStream, new PortableBackupManifest(
        "harness.portable-backup.v1", DateTimeOffset.UtcNow, "fixture", Harness.Storage.HarnessStore.CurrentSchemaVersion,
        [unsafePayload], []));
}
var unsafeRejected = false;
try { await restoreService.StageRestoreAsync(unsafeArchive); }
catch (InvalidDataException) { unsafeRejected = true; }
Check(unsafeRejected && !restoreService.HasPendingRestore, "Restore accepted an archive path that escapes its staging directory.");

var terminalOutput = new StringBuilder();
var terminalOutputLock = new object();
var terminalMarker = $"harness-terminal-{Guid.NewGuid():N}";
await using (var terminal = await TerminalSession.StartAsync(testRoot))
{
    terminal.OutputReceived += (_, output) =>
    {
        lock (terminalOutputLock) terminalOutput.Append(output.Text);
    };
    await Task.Delay(TimeSpan.FromSeconds(1));
    await terminal.SendAsync(OperatingSystem.IsWindows()
        ? $"Write-Host ('{terminalMarker[..20]}' + '{terminalMarker[20..]}')"
        : $"printf '%s\\n' '{terminalMarker}'");
    using var terminalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        lock (terminalOutputLock)
        {
            if (terminalOutput.ToString().Contains(terminalMarker, StringComparison.Ordinal)) break;
        }
        await Task.Delay(25, terminalTimeout.Token);
    }
}
var terminalBuffer = new TerminalWindowViewModel(testRoot);
terminalBuffer.CommandText = "Write-Host 'preserve me'";
Check(terminalBuffer.TakeCommand() == ""
      && terminalBuffer.CommandText == "Write-Host 'preserve me'",
    "Terminal consumed a command before its shell was ready.");
terminalBuffer.Enqueue(new TerminalOutputEventArgs("\u001b[31mred\u001b[0m\r\nready", TerminalOutputKind.StandardOutput));
Check(terminalBuffer.FlushPendingOutput()
      && terminalBuffer.OutputText == "red\nready"
      && !terminalBuffer.OutputText.Contains('\u001b'),
    "Terminal output sanitization did not preserve selectable text.");

await using var settingsStore = new Harness.Storage.HarnessStore(Path.Combine(testRoot, "settings-lifecycle.db"));
await settingsStore.InitializeAsync();

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
        vm.SetContextCountSupport(true);
        vm.ApplyApiContextPreview(250, 500);
        Check(vm.CanCheckContext && vm.ContextUsagePercent == 50
              && vm.ContextWindowStatus.Contains("provider preflight", StringComparison.Ordinal),
            "Provider preflight was not presented as pending context.");
        vm.PromptText = "fixture";
        Check(vm.ContextUsagePercent == 2.4
              && !vm.ContextWindowStatus.Contains("provider preflight", StringComparison.Ordinal),
            "Editing the pending request did not invalidate its provider count.");
        vm.BeginTurn(); vm.CompleteTurn("Fixture API failure");
        Check(vm.Messages.Any(message => message.Text.Contains("Fixture API failure", StringComparison.Ordinal)), "API error hidden from chat.");
        vm.PromptText = "stop fixture";
        vm.BeginTurn();
        vm.AppendAssistantDelta("stopped-fixture", "partial response");
        vm.CompleteTurn("Turn stopped by the user.");
        Check(vm.TurnActivityStatus == "STOPPED"
              && vm.Messages.Last(message => message.Text == "partial response").Status == "STOPPED",
            "A stopped turn or its partial assistant output was presented as completed/failed instead of STOPPED.");
        var settings = new SettingsWindow(usePreviewData: true) { Width = 1300, Height = 850 };
        ((SettingsWindowViewModel)settings.DataContext!).SetModelPreferences([
            new("openai-codex::codex-fixture", "openai-codex", "codex-fixture", "OpenAI Codex · Codex Fixture"),
            new("anthropic-claude::claude-provider-model-a", "anthropic-claude", "claude-provider-model-a", "Claude Code · Fixture A"),
            new($"{anthropic.Id}::{discovered.Descriptor.ModelId}", anthropic.Id, discovered.Descriptor.ModelId, $"Anthropic API · {discovered.Descriptor.DisplayName}"),
            new("openai-api::gpt-fixture", "openai-api", "gpt-fixture", "OpenAI API · GPT Fixture")
        ]);
        var settingsVm = (SettingsWindowViewModel)settings.DataContext!;
        settingsVm.ModelPreferences.Single(item => item.ProviderId == "anthropic-claude").IsEnabled = false;
        settingsVm.SubscriptionHandoffMode = "automatic";
        settingsVm.SetModelPreferences([
            new("openai-codex::codex-fixture", "openai-codex", "codex-fixture", "OpenAI Codex · Codex Fixture"),
            new("anthropic-claude::claude-provider-model-a", "anthropic-claude", "claude-provider-model-a", "Claude Code · Fixture A"),
            new("anthropic-claude::claude-provider-model-b", "anthropic-claude", "claude-provider-model-b", "Claude Code · Fixture B")
        ]);
        Check(settingsVm.ToSettings().HiddenModelIds!.Contains("anthropic-claude::claude-provider-model-a"),
            "A provider-reported Claude Code model could not be disabled or lost that choice during a catalog refresh.");
        Check(settingsVm.ToSettings().SubscriptionHandoffMode == "automatic",
            "The subscription handoff mode was not retained by Settings.");
        settings.Show();
        settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
        settings.FindControl<ComboBox>("ApiProviderPicker")!.SelectedItem = ApiProviderDefinition.All.Single(provider => provider.Id == "anthropic-api");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = settings.CaptureRenderedFrame() ?? throw new Exception("Provider settings frame unavailable.");
        frame.Save(Path.Combine(Environment.CurrentDirectory, ".artifacts", "api-providers.png"));
        Check(settings.FindControl<TextBox>("ApiKey")!.PasswordChar != '\0', "API key input is not masked.");
        Check(settings.FindControl<Button>("ApiDetectLocalButton") is not null,
            "Native local-runtime discovery is missing from Providers settings.");
        Check(settings.FindControl<CheckBox>("ApiModelPdf") is not null
              && settings.FindControl<CheckBox>("ApiModelAudio") is not null
              && settings.FindControl<CheckBox>("ApiModelVideo") is not null
              && settings.FindControl<CheckBox>("ApiModelCaching") is not null
              && settings.FindControl<CheckBox>("ApiModelHostedArtifacts") is not null
              && settings.FindControl<CheckBox>("ApiModelContextManagement") is not null,
            "Per-model PDF/audio/video/cache/hosted-artifact/context controls are missing from Providers settings.");
        Check(settings.FindControl<Border>("CodexConnectionPanel") is not null
            && settings.FindControl<Button>("CodexSignInButton") is not null
            && settings.FindControl<Button>("CodexSignOutButton") is not null
            && settings.FindControl<ComboBox>("CodexIdentityPicker") is not null
            && settings.FindControl<Button>("CodexUseIdentityButton") is not null,
            "Subscription connection management is missing from Providers settings.");
        Check(settings.FindControl<Border>("ClaudeConnectionPanel") is not null
              && settings.FindControl<Button>("ClaudeSignInButton") is not null
              && settings.FindControl<Button>("ClaudeSignOutButton") is not null
              && settings.FindControl<Button>("ClaudeInstallHelpButton") is not null
              && settings.FindControl<ComboBox>("ClaudeIdentityPicker") is not null
              && settings.FindControl<Button>("ClaudeUseIdentityButton") is not null
              && settings.FindControl<CheckBox>("ClaudeAutomaticHandoffToggle") is not null
              && settings.FindControl<ItemsControl>("ClaudeUsageCards") is not null
              && settings.FindControl<CheckBox>("CodexAutomaticHandoffToggle") is not null
              && settings.FindControl<ItemsControl>("CodexUsageCards") is not null,
            "Multi-account subscription management, routing controls, or usage cards are missing from Providers settings.");
        settings.Close();

        var handoff = new SubscriptionHandoffDialog(
            primaryIdentities[0],
            [loadedIdentities.Single(identity => identity.Id == addedIdentity.Id)]);
        handoff.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var handoffFrame = handoff.CaptureRenderedFrame()
            ?? throw new Exception("Account handoff frame unavailable.");
        handoffFrame.Save(Path.Combine(Environment.CurrentDirectory, ".artifacts", "account-handoff.png"));
        Check(handoff.FindControl<ListBox>("DestinationList")?.ItemCount == 1,
            "Account handoff did not expose the available destination profile.");
        handoff.Close();

        var terminalWindow = new TerminalWindow(testRoot);
        terminalWindow.Show();
        var terminalUiMarker = $"terminal-ui-{Guid.NewGuid():N}";
        DispatcherTimer.RunOnce(() =>
        {
            terminalWindow.FindControl<TextBox>("TerminalCommandBox")!.Text =
                $"Write-Host ('{terminalUiMarker[..20]}' + '{terminalUiMarker[20..]}')";
            terminalWindow.FindControl<Button>("TerminalRunButton")!.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }, TimeSpan.FromSeconds(1));
        using (var terminalPump = new CancellationTokenSource())
        {
            DispatcherTimer.RunOnce(terminalPump.Cancel, TimeSpan.FromSeconds(5));
            try { Dispatcher.UIThread.MainLoop(terminalPump.Token); }
            catch (OperationCanceledException) { }
        }
        var renderedTerminalOutput = ((TerminalWindowViewModel)terminalWindow.DataContext!).OutputText;
        Check(renderedTerminalOutput.Contains(terminalUiMarker, StringComparison.Ordinal),
            $"Terminal window did not render PowerShell Write-Host output. Transcript: {renderedTerminalOutput}");
        terminalWindow.Close();
        Dispatcher.UIThread.RunJobs();

        var taskLibrary = new SessionLibraryWindow();
        taskLibrary.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Check(taskLibrary.FindControl<TextBox>("SearchBox") is not null
              && taskLibrary.FindControl<ComboBox>("StatusFilter") is not null
              && taskLibrary.FindControl<TextBox>("TitleEditor") is not null,
            "Task Library search, archive filter, or rename surface is missing.");
        taskLibrary.Close();
        Dispatcher.UIThread.RunJobs();

        // Reproduce the production failure: Opened starts asynchronous catalog I/O, then closing
        // cancels it. Cancellation from an async-void UI event must never escape the dispatcher.
        var lifecycle = new SettingsWindow(new HarnessApplicationSettings(), testRoot,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask, () => Task.CompletedTask, settingsStore, [], [], false);
        lifecycle.Show();
        DispatcherTimer.RunOnce(lifecycle.Close, TimeSpan.FromMilliseconds(1));
        using var pump = new CancellationTokenSource();
        DispatcherTimer.RunOnce(pump.Cancel, TimeSpan.FromMilliseconds(400));
        try { Dispatcher.UIThread.MainLoop(pump.Token); }
        catch (OperationCanceledException) { }
    }
    catch (Exception exception) { uiFailure = exception; }
});
uiThread.Start(); uiThread.Join();
if (uiFailure is not null) throw uiFailure;
Console.WriteLine("API checks passed: Claude Code subscription model/effort/fast-mode/usage parsing, four native API wire formats, provider-native OpenAI/Anthropic/Gemini input-token preflight, native PDF/audio/video inputs, opt-in prompt caching and cache telemetry, OpenAI native context compaction, OpenAI and Anthropic hosted-artifact request/citation/download handling, native Ollama/llama.cpp discovery, capability conformance/preflight, reasoning/tool replay, direct-API skill installation/discovery/resource isolation plus integrity/update/disable/removal lifecycle, durable task search/archive/restore/export and v5 migration, Unicode, usage, pagination, unknown capabilities, failure handling, credential routing, approval boundaries, catalog merging, isolated subscription profiles and handoff UI, portable backup round-trip, unsafe-archive rejection, delayed PowerShell Write-Host plus rendered terminal output, and Providers UI. No live API calls made.");

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

sealed class RouteFixtureHandler(Func<HttpRequestMessage, Task<string>> route) : HttpMessageHandler
{
    public List<string> Paths { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri) throw new Exception("Request endpoint missing.");
        lock (Paths) Paths.Add(request.RequestUri.AbsolutePath);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(await route(request), Encoding.UTF8, "application/json")
        };
    }
}
