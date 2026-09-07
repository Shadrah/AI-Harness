using Harness.Core.Models;

namespace Harness.Providers.Api;

public enum ApiCapabilityState
{
    Ready,
    ModelUnsupported,
    AdapterUnavailable,
    Unknown
}

public sealed record ApiCapabilityFinding(
    ModelCapability Capability,
    ApiCapabilityState State,
    string Detail);

public sealed record ApiCapabilityReport(
    IReadOnlyList<ApiCapabilityFinding> Findings)
{
    public IReadOnlyList<ModelCapability> Ready => Findings
        .Where(finding => finding.State == ApiCapabilityState.Ready)
        .Select(finding => finding.Capability)
        .ToArray();

    public ModelCapability ReadyCapabilities => Ready.Aggregate(
        ModelCapability.None,
        (current, capability) => current | capability);

    public IReadOnlyList<ModelCapability> AdapterGaps => Findings
        .Where(finding => finding.State == ApiCapabilityState.AdapterUnavailable)
        .Select(finding => finding.Capability)
        .ToArray();
}

/// <summary>
/// Separates what a model reports from what Harness's wire adapter can currently deliver.
/// A reported model capability is never presented as usable until both sides are ready.
/// </summary>
public static class ApiCapabilityConformance
{
    private const ModelCapability CommonAdapterCapabilities =
        ModelCapability.Text |
        ModelCapability.Vision |
        ModelCapability.ToolUse |
        ModelCapability.Reasoning;

    private static readonly ModelCapability[] Features =
    [
        ModelCapability.Text,
        ModelCapability.Vision,
        ModelCapability.ToolUse,
        ModelCapability.Reasoning,
        ModelCapability.StructuredOutput,
        ModelCapability.PromptCaching,
        ModelCapability.PdfInput,
        ModelCapability.AudioInput,
        ModelCapability.AudioOutput,
        ModelCapability.VideoInput,
        ModelCapability.ImageGeneration,
        ModelCapability.ComputerUse,
        ModelCapability.Citations,
        ModelCapability.ContextManagement,
        ModelCapability.GeneratedArtifacts
    ];

    public static ApiCapabilityReport Evaluate(ApiConnection connection, ApiModel model)
    {
        var adapterImplemented = ImplementedFor(connection);
        var findings = new List<ApiCapabilityFinding>(Features.Length);
        foreach (var capability in Features)
        {
            var reported = model.Descriptor.Supports(capability);
            var implemented = (adapterImplemented & capability) == capability;
            var fieldReported = model.ReportedCapabilityFields?.Contains(capability) == true;
            var state = reported
                ? implemented ? ApiCapabilityState.Ready : ApiCapabilityState.AdapterUnavailable
                : fieldReported ? ApiCapabilityState.ModelUnsupported : ApiCapabilityState.Unknown;
            var detail = state switch
            {
                ApiCapabilityState.Ready => "Reported by the selected model and implemented by this adapter.",
                ApiCapabilityState.AdapterUnavailable => "Reported by the selected model; its Harness request/output path is not implemented yet.",
                ApiCapabilityState.ModelUnsupported => "The model catalog did not report this capability as supported.",
                _ => "The model catalog did not publish enough metadata to determine support."
            };
            findings.Add(new(capability, state, detail));
        }
        return new(findings);
    }

    public static ModelCapability ImplementedFor(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.Responses => CommonAdapterCapabilities | ModelCapability.PdfInput,
        ApiProtocol.Anthropic => CommonAdapterCapabilities | ModelCapability.PdfInput | ModelCapability.PromptCaching,
        ApiProtocol.Gemini => CommonAdapterCapabilities | ModelCapability.PdfInput | ModelCapability.AudioInput | ModelCapability.VideoInput,
        _ => CommonAdapterCapabilities
    };

    public static ModelCapability ImplementedFor(ApiConnection connection) =>
        ImplementedFor(connection.Definition.Protocol)
        | (connection.ProviderId is "openai-api" or "anthropic-api"
            ? ModelCapability.GeneratedArtifacts : ModelCapability.None);

    public static string Name(ModelCapability capability) => capability switch
    {
        ModelCapability.Text => "text streaming",
        ModelCapability.Vision => "image input",
        ModelCapability.ToolUse => "client tools",
        ModelCapability.Reasoning => "reasoning controls",
        ModelCapability.StructuredOutput => "structured output",
        ModelCapability.PromptCaching => "prompt caching controls",
        ModelCapability.PdfInput => "PDF input",
        ModelCapability.AudioInput => "audio input",
        ModelCapability.AudioOutput => "audio output",
        ModelCapability.VideoInput => "video input",
        ModelCapability.ImageGeneration => "image generation",
        ModelCapability.Citations => "citations",
        ModelCapability.ContextManagement => "native context management",
        ModelCapability.GeneratedArtifacts => "generated artifacts",
        _ => capability.ToString()
    };

    public static void ValidateTurn(ApiConnection connection, ApiModel model, string? effort,
        string? tier, IReadOnlyList<ApiTool> tools)
    {
        if (!string.Equals(model.Descriptor.ProviderId, connection.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected model belongs to a different provider connection. Refresh the model selection and try again.");

        if (tools.Count > 0) RequireReady(connection, model, ModelCapability.ToolUse, "workspace tools");

        if (!string.IsNullOrWhiteSpace(effort))
        {
            RequireReady(connection, model, ModelCapability.Reasoning, "reasoning control");
            var levels = model.Descriptor.ReasoningLevels ?? [];
            if (levels.Count == 0 || !levels.Any(level => string.Equals(level.Id, effort, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Reasoning level '{effort}' was not advertised for {model.Descriptor.DisplayName}. Refresh its catalog metadata or configure a verified model override.");
        }

        if (!string.IsNullOrWhiteSpace(tier))
        {
            var tiers = model.Descriptor.ServiceTiers ?? [];
            if (tiers.Count == 0 || !tiers.Any(option => string.Equals(option.Id, tier, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Service tier '{tier}' was not advertised for {model.Descriptor.DisplayName}. Refresh its catalog metadata or configure a verified model override.");
        }

        if (model.PromptCachingEnabled)
            RequireReady(connection, model, ModelCapability.PromptCaching, "automatic prompt caching");
        if (model.HostedArtifactsEnabled)
            RequireReady(connection, model, ModelCapability.GeneratedArtifacts, "provider-hosted artifact generation");
    }

    public static void ValidateAttachment(ApiConnection connection, ApiModel model, FilePart file)
    {
        var mediaType = file.MediaType ?? "application/octet-stream";
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            RequireReady(connection, model, ModelCapability.Vision, "image input");
            return;
        }
        if (mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            RequireReady(connection, model, ModelCapability.PdfInput, "PDF input");
        else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            RequireReady(connection, model, ModelCapability.AudioInput, "audio input");
        else if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            RequireReady(connection, model, ModelCapability.VideoInput, "video input");
    }

    private static void RequireReady(ApiConnection connection, ApiModel model, ModelCapability capability, string label)
    {
        if (!model.Descriptor.Supports(capability))
            throw new InvalidOperationException($"{model.Descriptor.DisplayName} did not report support for {label}. Harness did not send the turn.");
        if ((ImplementedFor(connection) & capability) != capability)
            throw new InvalidOperationException($"{model.Descriptor.DisplayName} reports {label}, but the Harness API adapter does not implement that delivery path yet. Nothing was sent.");
    }
}
