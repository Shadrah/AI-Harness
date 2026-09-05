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
await using (var previousTargetStore = new Harness.Storage.HarnessStore(portableTargetDatabase))
{
    await previousTargetStore.InitializeAsync();
    await previousTargetStore.SaveApplicationSettingsAsync(new HarnessApplicationSettings(PersonalInstructions: "replace me"));
}
var restoreService = new PortableBackupService(null, portableTargetRoot, portableTargetDatabase, portableTargetApi, portableTargetSkills);
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
    var restoredGlobalSkill = restoredSkills.Single(skill => skill.Scope == "GLOBAL");
    var deferredWorkspaceSkill = restoredSkills.Single(skill => skill.Scope == "WORKSPACE");
    Check(restoredSettings.PersonalInstructions == "portable fixture"
          && restoredSession.Messages.Any(message => message.Text == "portable message")
          && restoredSession.Attachments.Count == 1
          && restoredSession.Attachments[0].StoredPath.StartsWith(Path.Combine(portableTargetRoot, "data"), StringComparison.OrdinalIgnoreCase)
          && await File.ReadAllTextAsync(restoredSession.Attachments[0].StoredPath) == "portable context"
          && restoredGlobalSkill.Enabled
          && restoredGlobalSkill.InstallPath.StartsWith(portableTargetSkills, StringComparison.OrdinalIgnoreCase)
          && File.Exists(Path.Combine(restoredGlobalSkill.InstallPath, "SKILL.md"))
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
        vm.PromptText = "fixture"; vm.BeginTurn(); vm.CompleteTurn("Fixture API failure");
        Check(vm.Messages.Any(message => message.Text.Contains("Fixture API failure", StringComparison.Ordinal)), "API error hidden from chat.");
        var settings = new SettingsWindow(usePreviewData: true) { Width = 1300, Height = 850 };
        ((SettingsWindowViewModel)settings.DataContext!).SetModelPreferences([
            new("openai-codex::codex-fixture", "openai-codex", "codex-fixture", "OpenAI Codex · Codex Fixture"),
            new($"{anthropic.Id}::{discovered.Descriptor.ModelId}", anthropic.Id, discovered.Descriptor.ModelId, $"Anthropic API · {discovered.Descriptor.DisplayName}"),
            new("openai-api::gpt-fixture", "openai-api", "gpt-fixture", "OpenAI API · GPT Fixture")
        ]);
        settings.Show();
        settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
        settings.FindControl<ComboBox>("ApiProviderPicker")!.SelectedItem = ApiProviderDefinition.All.Single(provider => provider.Id == "anthropic-api");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = settings.CaptureRenderedFrame() ?? throw new Exception("Provider settings frame unavailable.");
        frame.Save(Path.Combine(Environment.CurrentDirectory, ".artifacts", "api-providers.png"));
        Check(settings.FindControl<TextBox>("ApiKey")!.PasswordChar != '\0', "API key input is not masked.");
        Check(settings.FindControl<Border>("CodexConnectionPanel") is not null
            && settings.FindControl<Button>("CodexSignInButton") is not null
            && settings.FindControl<Button>("CodexSignOutButton") is not null
            && settings.FindControl<ComboBox>("CodexIdentityPicker") is not null
            && settings.FindControl<Button>("CodexUseIdentityButton") is not null,
            "Subscription connection management is missing from Providers settings.");
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
Console.WriteLine("API checks passed: four native wire formats, reasoning/tool replay, Unicode, usage, pagination, unknown capabilities, failure handling, credential routing, approval boundaries, catalog merging, isolated subscription profiles and handoff UI, portable backup round-trip, unsafe-archive rejection, delayed PowerShell Write-Host plus rendered terminal output, and Providers UI. No live API calls made.");

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
