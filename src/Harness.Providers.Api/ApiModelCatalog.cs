using Harness.Core.Models;
using System.Text.Json.Nodes;

namespace Harness.Providers.Api;

public sealed record ApiModelConfiguration(string ModelId, bool Tools, bool Images, int? ContextWindow,
    string[] ReasoningLevels, string[] ServiceTiers);

public sealed record ApiModel(ModelDescriptor Descriptor, JsonObject Metadata, bool CapabilityMetadataReported,
    int? MaxOutputTokens, bool AdaptiveThinking)
{
    public override string ToString() => Descriptor.DisplayName;
}

public static class ApiModelCatalog
{
    public static async Task<IReadOnlyList<ApiModel>> LoadAsync(ApiConnection connection, ApiTransport transport,
        IReadOnlyList<ApiModelConfiguration> configurations, CancellationToken cancellationToken)
    {
        var models = new Dictionary<string, ApiModel>(StringComparer.Ordinal);
        var path = connection.ProviderId == "xai-api" ? "language-models" : "models";
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
        foreach (var config in configurations)
        {
            if (!models.TryGetValue(config.ModelId, out var model)) continue;
            var caps = ModelCapability.Text | (config.Tools ? ModelCapability.ToolUse : 0)
                | (config.Images ? ModelCapability.Vision : 0)
                | (config.ReasoningLevels.Length > 0 ? ModelCapability.Reasoning : 0);
            models[config.ModelId] = model with { Descriptor = model.Descriptor with
            {
                Capabilities = caps, ContextWindow = config.ContextWindow ?? model.Descriptor.ContextWindow,
                ReasoningLevels = config.ReasoningLevels.Select(id => new ReasoningLevelDescriptor(id, id, "User-configured API value")).ToArray(),
                ServiceTiers = config.ServiceTiers.Select(id => new ServiceTierDescriptor(id, id, "User-configured API value")).ToArray()
            }};
        }
        return models.Values.OrderBy(model => model.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
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
        var parameters = Strings(node["supported_parameters"] as JsonArray).ToHashSet(StringComparer.Ordinal);
        var reported = capabilities is not null || input is not null || parameters.Count > 0;
        var caps = ModelCapability.Text;
        if (Strings(input).Contains("image") || Supported(capabilities?["vision"]) || Supported(capabilities?["image_input"])) caps |= ModelCapability.Vision;
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
        var tiers = Strings(node["supported_service_tiers"] as JsonArray)
            .Select(tier => new ServiceTierDescriptor(tier, tier, "Reported by the model catalog")).ToArray();
        var context = Number(node, "max_input_tokens") ?? Number(node, "inputTokenLimit") ?? Number(node, "context_length")
            ?? Number(node, "max_model_len");
        return new ApiModel(new ModelDescriptor(connection.Id, id,
                Text(node, "display_name") ?? Text(node, "displayName") ?? Text(node, "name") ?? id,
                caps, context, reasoning, tiers), node.DeepClone().AsObject(), reported,
            Number(node, "max_tokens") ?? Number(node, "outputTokenLimit"), Supported(capabilities?["thinking"]?["types"]?["adaptive"]));
    }

    internal static string? Text(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static int? Number(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    internal static bool Supported(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean
        : node is JsonObject obj && obj["supported"] is JsonValue flag && flag.TryGetValue<bool>(out var supported) && supported;
    internal static IEnumerable<string> Strings(JsonArray? array) => array?.OfType<JsonValue>()
        .Select(node => node.TryGetValue<string>(out var value) ? value : null).OfType<string>() ?? [];
}
