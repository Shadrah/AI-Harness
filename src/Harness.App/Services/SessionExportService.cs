using System.Text;
using System.Text.Json;
using Harness.Core.Models;

namespace Harness.App.Services;

public sealed class SessionExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task ExportAsync(
        string destinationPath,
        StoredSession session,
        IReadOnlyList<StoredMessage> messages,
        IReadOnlyList<StoredAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        var extension = Path.GetExtension(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await JsonSerializer.SerializeAsync(
                stream,
                new SessionExportDocument("harness.session.v1", session, messages, attachments),
                JsonOptions,
                cancellationToken);
            return;
        }

        var markdown = await Task.Run(
            () => BuildMarkdown(session, messages, attachments), cancellationToken);
        await File.WriteAllTextAsync(destination, markdown, new UTF8Encoding(false), cancellationToken);
    }

    private static string BuildMarkdown(
        StoredSession session,
        IReadOnlyList<StoredMessage> messages,
        IReadOnlyList<StoredAttachment> attachments)
    {
        var output = new StringBuilder();
        output.AppendLine($"# {session.Title}");
        output.AppendLine();
        output.AppendLine($"- Provider: {session.ProviderId ?? "Not selected"}");
        output.AppendLine($"- Model: {session.ModelId ?? "Not selected"}");
        output.AppendLine($"- Created: {session.CreatedAt:O}");
        output.AppendLine($"- Updated: {session.UpdatedAt:O}");
        output.AppendLine($"- Harness session: `{session.Id}`");
        if (attachments.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("## Attached context");
            output.AppendLine();
            foreach (var attachment in attachments)
                output.AppendLine($"- {attachment.DisplayName} ({attachment.MediaType ?? "unknown"}, {attachment.ByteLength} bytes, SHA-256 `{attachment.Sha256}`)");
        }
        foreach (var message in messages.OrderBy(message => message.Sequence))
        {
            output.AppendLine();
            output.AppendLine($"## {message.Role} · {message.CreatedAt.ToLocalTime():g}");
            output.AppendLine();
            output.AppendLine(message.Text);
        }
        return output.ToString();
    }

    private sealed record SessionExportDocument(
        string Format,
        StoredSession Session,
        IReadOnlyList<StoredMessage> Messages,
        IReadOnlyList<StoredAttachment> Attachments);
}
