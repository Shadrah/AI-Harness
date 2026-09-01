namespace Harness.Core.Models;

[Flags]
public enum ModelCapability
{
    None = 0,
    Text = 1 << 0,
    Vision = 1 << 1,
    ToolUse = 1 << 2,
    Reasoning = 1 << 3,
    ImageGeneration = 1 << 4,
    AudioInput = 1 << 5,
    AudioOutput = 1 << 6,
    PromptCaching = 1 << 7,
    ComputerUse = 1 << 8
}

public sealed record ModelDescriptor(
    string ProviderId,
    string ModelId,
    string DisplayName,
    ModelCapability Capabilities,
    int? ContextWindow = null,
    IReadOnlyList<ReasoningLevelDescriptor>? ReasoningLevels = null,
    IReadOnlyList<ServiceTierDescriptor>? ServiceTiers = null,
    bool IsDefault = false)
{
    public bool Supports(ModelCapability capability) =>
        (Capabilities & capability) == capability;
}

public sealed record ReasoningLevelDescriptor(
    string Id,
    string DisplayName,
    string? Description = null,
    bool IsDefault = false);

public sealed record ServiceTierDescriptor(
    string? Id,
    string DisplayName,
    string? Description = null,
    bool IsDefault = false);
