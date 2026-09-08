using Harness.Core.Models;
using System.Text.Json.Nodes;

namespace Harness.Providers.Api;

public sealed record ApiModelConfiguration(string ModelId, bool Tools, bool Images, int? ContextWindow,
    string[] ReasoningLevels, string[] ServiceTiers, bool Audio = false, bool Video = false, bool Pdf = false,
    bool PromptCaching = false, bool HostedArtifacts = false, bool ContextManagement = false);

public sealed record ApiModel(ModelDescriptor Descriptor, JsonObject Metadata, bool CapabilityMetadataReported,
    int? MaxOutputTokens, bool AdaptiveThinking, IReadOnlySet<ModelCapability>? ReportedCapabilityFields = null,
    bool PromptCachingEnabled = false, bool HostedArtifactsEnabled = false, bool ContextManagementEnabled = false)
{
    public override string ToString() => Descriptor.DisplayName;
}

public static class ApiModelCatalog
{
    public static async Task<IReadOnlyList<ApiModel>> LoadAsync(ApiConnection connection, ApiTransport transport,
        IReadOnlyList<ApiModelConfiguration> configurations, CancellationToken cancellationToken)
    {
        var models = new Dictionary<string, ApiModel>(StringComparer.Ordinal);
        if (connection.ProviderId == "ollama-local")
        {
            foreach (var model in await LoadOllamaAsync(connection, transport, cancellationToken).ConfigureAwait(false))
                models[model.Descriptor.ModelId] = model;
        }
        else
        {
            var path = connection.ProviderId switch
            {
                "xai-api" => "language-models",
                "llama-cpp-local" => "/models",
                _ => "models"
            };
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            while (seenPages.Add(path))
            {
                var response = await transport.GetAsync(path, cancellationToken).ConfigureAwait(false);
                var records = response["data"] as JsonArray ?? response["models"] as JsonArray
                    ?? throw new InvalidOperationException("The provider did not return a models array.");
                foreach (var node in records.OfType<JsonObject>())
                {
                    var model = Parse(connection, node);
                    if (model is not null) models[model.Descriptor.ModelId] = model;
                }
                if (response["nextPageToken"]?.GetValue<string>() is { Length: > 0 } page)
                    path = "models?pageToken=" + Uri.EscapeDataString(page);
                else if (response["has_more"]?.GetValue<bool>() == true && response["last_id"]?.GetValue<string>() is { Length: > 0 } after)
                    path = "models?after_id=" + Uri.EscapeDataString(after);
                else break;
                if (seenPages.Contains(path)) throw new InvalidOperationException("The provider repeated a catalog page; the partial catalog was not applied.");
                if (seenPages.Count >= 100) throw new InvalidOperationException("Catalog pagination exceeded 100 pages; the partial catalog was not applied.");
            }
        }
        foreach (var config in configurations)
        {
            if (!models.TryGetValue(config.ModelId, out var model)) continue;
            var caps = model.Descriptor.Capabilities;
            caps = config.Tools ? caps | ModelCapability.ToolUse : caps & ~ModelCapability.ToolUse;
            caps = config.Images ? caps | ModelCapability.Vision : caps & ~ModelCapability.Vision;
            caps = config.Audio ? caps | ModelCapability.AudioInput : caps & ~ModelCapability.AudioInput;
            caps = config.Video ? caps | ModelCapability.VideoInput : caps & ~ModelCapability.VideoInput;
            caps = config.Pdf ? caps | ModelCapability.PdfInput : caps & ~ModelCapability.PdfInput;
            if (config.PromptCaching) caps |= ModelCapability.PromptCaching;
            caps = config.HostedArtifacts ? caps | ModelCapability.GeneratedArtifacts : caps & ~ModelCapability.GeneratedArtifacts;
            caps = config.ContextManagement ? caps | ModelCapability.ContextManagement : caps & ~ModelCapability.ContextManagement;
            caps = config.ReasoningLevels.Length > 0 ? caps | ModelCapability.Reasoning : caps & ~ModelCapability.Reasoning;
            var reportedFields = (model.ReportedCapabilityFields ?? new HashSet<ModelCapability>()).ToHashSet();
            reportedFields.UnionWith([ModelCapability.Text, ModelCapability.ToolUse, ModelCapability.Vision,
                ModelCapability.AudioInput, ModelCapability.VideoInput, ModelCapability.PdfInput,
                ModelCapability.Reasoning, ModelCapability.GeneratedArtifacts, ModelCapability.ContextManagement]);
            models[config.ModelId] = model with { Descriptor = model.Descriptor with
            {
                Capabilities = caps, ContextWindow = config.ContextWindow ?? model.Descriptor.ContextWindow,
                ReasoningLevels = config.ReasoningLevels.Select(id => new ReasoningLevelDescriptor(id, id, "User-configured API value")).ToArray(),
                ServiceTiers = config.ServiceTiers.Select(id => new ServiceTierDescriptor(id, id, "User-configured API value")).ToArray()
            }, ReportedCapabilityFields = reportedFields, PromptCachingEnabled = config.PromptCaching,
                HostedArtifactsEnabled = config.HostedArtifacts, ContextManagementEnabled = config.ContextManagement };
        }
        return models.Values.OrderBy(model => model.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<ApiModel>> LoadOllamaAsync(ApiConnection connection, ApiTransport transport,
        CancellationToken cancellationToken)
    {
        var catalog = await transport.GetAsync("/api/tags", cancellationToken).ConfigureAwait(false);
        var records = catalog["models"] as JsonArray
            ?? throw new InvalidOperationException("Ollama did not return a models array from /api/tags.");
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = records.OfType<JsonObject>().Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var id = Text(entry, "model") ?? Text(entry, "name");
                if (string.IsNullOrWhiteSpace(id)) return null;
                JsonObject? details = null;
                string? detailError = null;
                try
                {
                    details = await transport.PostJsonAsync("/api/show", new JsonObject { ["model"] = id }, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { detailError = exception.Message; }
                return ParseOllama(connection, entry, details, detailError);
            }
            finally { gate.Release(); }
        }).ToArray();
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).OfType<ApiModel>().ToArray();
    }

    internal static ApiModel? ParseOllama(ApiConnection connection, JsonObject catalogEntry, JsonObject? details,
        string? detailError = null)
    {
        var id = Text(catalogEntry, "model") ?? Text(catalogEntry, "name");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var capabilityNode = details?["capabilities"] as JsonArray;
        var capabilities = Strings(capabilityNode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (capabilityNode is not null && !capabilities.Contains("completion")) return null;

        var flags = ModelCapability.Text;
        if (capabilities.Contains("vision") || capabilities.Contains("image")) flags |= ModelCapability.Vision;
        if (capabilities.Contains("tools") || capabilities.Contains("tool_use")) flags |= ModelCapability.ToolUse;
        if (capabilities.Contains("thinking") || capabilities.Contains("reasoning")) flags |= ModelCapability.Reasoning;
        if (capabilities.Contains("audio")) flags |= ModelCapability.AudioInput;

        var reportedFields = new HashSet<ModelCapability> { ModelCapability.Text };
        if (capabilityNode is not null)
            reportedFields.UnionWith([ModelCapability.Vision, ModelCapability.ToolUse, ModelCapability.Reasoning, ModelCapability.AudioInput]);
        var metadata = new JsonObject
        {
            ["catalog"] = catalogEntry.DeepClone(),
            ["details"] = details?.DeepClone(),
            ["detail_error"] = detailError
        };
        var context = FindContextLength(details?["model_info"] as JsonObject);
        return new ApiModel(new ModelDescriptor(connection.Id, id, id, flags, context, [], []), metadata,
            capabilityNode is not null, null, false, reportedFields);
    }

    public static ApiModel? Parse(ApiConnection connection, JsonObject node)
    {
        var protocol = connection.Definition.Protocol;
        var id = Text(node, "id") ?? Text(node, "name");
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (protocol == ApiProtocol.Gemini)
        {
            if (node["supportedGenerationMethods"] is JsonArray methods && !Strings(methods).Contains("generateContent")) return null;
            if (id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
        }
        var capabilities = node["capabilities"] as JsonObject;
        if (capabilities?["completion_chat"]?.GetValue<bool>() == false) return null;
        var input = node["input_modalities"] as JsonArray ?? node["architecture"]?["input_modalities"] as JsonArray;
        var output = node["output_modalities"] as JsonArray ?? node["architecture"]?["output_modalities"] as JsonArray;
        // Dedicated embedding/image/audio generators do not use a conversational agent protocol.
        if (output is not null && !Strings(output).Contains("text")) return null;
        var parameterNode = node["supported_parameters"] as JsonArray;
        var parameters = Strings(parameterNode).ToHashSet(StringComparer.Ordinal);
        var reported = capabilities is not null || input is not null || output is not null || parameterNode is not null;
        var reportedFields = new HashSet<ModelCapability> { ModelCapability.Text };
        if (input is not null) reportedFields.UnionWith([ModelCapability.Vision, ModelCapability.AudioInput, ModelCapability.VideoInput, ModelCapability.PdfInput]);
        if (output is not null) reportedFields.UnionWith([ModelCapability.AudioOutput, ModelCapability.ImageGeneration,
            ModelCapability.GeneratedArtifacts]);
        if (parameterNode is not null) reportedFields.UnionWith([ModelCapability.ToolUse, ModelCapability.Reasoning, ModelCapability.StructuredOutput, ModelCapability.PromptCaching]);
        MarkReported(capabilities, reportedFields, ModelCapability.Vision, "vision", "image_input");
        MarkReported(capabilities, reportedFields, ModelCapability.AudioInput, "audio_input");
        MarkReported(capabilities, reportedFields, ModelCapability.VideoInput, "video_input");
        MarkReported(capabilities, reportedFields, ModelCapability.PdfInput, "pdf_input");
        MarkReported(capabilities, reportedFields, ModelCapability.AudioOutput, "audio_output");
        MarkReported(capabilities, reportedFields, ModelCapability.ImageGeneration, "image_generation");
        MarkReported(capabilities, reportedFields, ModelCapability.ToolUse, "function_calling", "tool_use");
        MarkReported(capabilities, reportedFields, ModelCapability.Reasoning, "effort", "thinking");
        MarkReported(capabilities, reportedFields, ModelCapability.StructuredOutput, "structured_outputs", "structured_output");
        MarkReported(capabilities, reportedFields, ModelCapability.PromptCaching, "prompt_caching");
        MarkReported(capabilities, reportedFields, ModelCapability.Citations, "citations");
        MarkReported(capabilities, reportedFields, ModelCapability.ContextManagement, "context_management");
        MarkReported(capabilities, reportedFields, ModelCapability.ComputerUse, "computer_use");
        MarkReported(capabilities, reportedFields, ModelCapability.GeneratedArtifacts,
            "generated_artifacts", "artifact_generation", "file_output", "code_interpreter");
        if (node["supported_reasoning_levels"] is JsonArray) reportedFields.Add(ModelCapability.Reasoning);
        var caps = ModelCapability.Text;
        if (Strings(input).Contains("image") || Supported(capabilities?["vision"]) || Supported(capabilities?["image_input"])) caps |= ModelCapability.Vision;
        if (Strings(input).Contains("audio") || Supported(capabilities?["audio_input"])) caps |= ModelCapability.AudioInput;
        if (Strings(input).Contains("video") || Supported(capabilities?["video_input"])) caps |= ModelCapability.VideoInput;
        if (Strings(input).Contains("pdf") || Supported(capabilities?["pdf_input"])) caps |= ModelCapability.PdfInput;
        if (Strings(output).Contains("audio") || Supported(capabilities?["audio_output"])) caps |= ModelCapability.AudioOutput;
        if (Strings(output).Contains("image") || Supported(capabilities?["image_generation"])) caps |= ModelCapability.ImageGeneration;
        if (Supported(capabilities?["function_calling"]) || Supported(capabilities?["tool_use"]) || parameters.Contains("tools")) caps |= ModelCapability.ToolUse;
        // Claude's Messages contract supports client tool use. A negative explicit capability wins.
        if (protocol == ApiProtocol.Anthropic && capabilities?["tool_use"] is null) caps |= ModelCapability.ToolUse;
        var effort = capabilities?["effort"] as JsonObject;
        var reasoning = new List<ReasoningLevelDescriptor>();
        if (effort is not null)
            foreach (var pair in effort)
                if (pair.Key != "supported" && Supported(pair.Value)) reasoning.Add(new(pair.Key, pair.Key, "Reported by the model catalog"));
        if (node["supported_reasoning_levels"] is JsonArray levels)
            reasoning.AddRange(Strings(levels).Select(level => new ReasoningLevelDescriptor(level, level, "Reported by the model catalog")));
        if (reasoning.Count > 0 || Supported(capabilities?["thinking"])) caps |= ModelCapability.Reasoning;
        if (Supported(capabilities?["structured_outputs"]) || Supported(capabilities?["structured_output"])
            || parameters.Contains("response_format")) caps |= ModelCapability.StructuredOutput;
        if (Supported(capabilities?["prompt_caching"]) || parameters.Contains("prompt_cache_key")
            || parameters.Contains("cache_control")) caps |= ModelCapability.PromptCaching;
        if (Supported(capabilities?["citations"])) caps |= ModelCapability.Citations;
        if (Supported(capabilities?["context_management"])) caps |= ModelCapability.ContextManagement;
        if (Supported(capabilities?["computer_use"])) caps |= ModelCapability.ComputerUse;
        if (Strings(output).Any(value => value is "file" or "document")
            || Supported(capabilities?["generated_artifacts"])
            || Supported(capabilities?["artifact_generation"])
            || Supported(capabilities?["file_output"])
            || Supported(capabilities?["code_interpreter"])) caps |= ModelCapability.GeneratedArtifacts;
        var tiers = Strings(node["supported_service_tiers"] as JsonArray)
            .Select(tier => new ServiceTierDescriptor(tier, tier, "Reported by the model catalog")).ToArray();
        var context = Number(node, "max_input_tokens") ?? Number(node, "inputTokenLimit") ?? Number(node, "context_length")
            ?? Number(node, "max_model_len") ?? Number(node["meta"], "n_ctx_train")
            ?? FindContextLength(node["model_info"] as JsonObject);
        return new ApiModel(new ModelDescriptor(connection.Id, id,
                Text(node, "display_name") ?? Text(node, "displayName") ?? Text(node, "name") ?? id,
                caps, context, reasoning, tiers), node.DeepClone().AsObject(), reported,
            Number(node, "max_tokens") ?? Number(node, "outputTokenLimit"), Supported(capabilities?["thinking"]?["types"]?["adaptive"]), reportedFields);
    }

    private static void MarkReported(JsonObject? source, ISet<ModelCapability> fields, ModelCapability capability, params string[] names)
    {
        if (source is not null && names.Any(source.ContainsKey)) fields.Add(capability);
    }

    private static int? FindContextLength(JsonObject? modelInfo)
    {
        if (modelInfo is null) return null;
        foreach (var pair in modelInfo)
        {
            if (!pair.Key.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) || pair.Value is not JsonValue value) continue;
            if (value.TryGetValue<int>(out var integer) && integer > 0) return integer;
            if (value.TryGetValue<long>(out var number) && number is > 0 and <= int.MaxValue) return (int)number;
        }
        return null;
    }

    internal static string? Text(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static int? Number(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    internal static bool Supported(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean
        : node is JsonObject obj && obj["supported"] is JsonValue flag && flag.TryGetValue<bool>(out var supported) && supported;
    internal static IEnumerable<string> Strings(JsonArray? array) => array?.OfType<JsonValue>()
        .Select(node => node.TryGetValue<string>(out var value) ? value : null).OfType<string>() ?? [];
}
