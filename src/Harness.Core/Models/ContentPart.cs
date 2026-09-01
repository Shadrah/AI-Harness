namespace Harness.Core.Models;

public abstract record ContentPart;

public sealed record TextPart(string Text) : ContentPart;

public sealed record ImagePart(
    Uri Source,
    string? MediaType = null,
    string? AltText = null) : ContentPart;

public sealed record FilePart(
    string Path,
    string? MediaType = null) : ContentPart;

public sealed record UserTurn(IReadOnlyList<ContentPart> Content);
