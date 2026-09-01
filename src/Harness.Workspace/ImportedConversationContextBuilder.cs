using System.Text;
using Harness.Core.Models;

namespace Harness.Workspace;

public static class ImportedConversationContextBuilder
{
    // Hidden context still consumes provider tokens. Keep the continuity brief
    // deliberately small and retain the raw export only for local inspection.
    public const int DefaultMaximumCharacters = 12_000;
    private const int MaximumExcerptCharacters = 1_600;
    private static readonly string[] ContinuationSignals =
    [
        "awaiting", "pending", "approval", "blocked", "blocker", "in progress",
        "stopping point", "when we resume", "next step", "remaining work",
        "not yet", "cannot proceed", "ready to resume"
    ];

    public static ImportedContextEnvelope Build(
        StoredImportSource? source,
        IReadOnlyList<StoredMessage> messages,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        if (maximumCharacters < 4_000) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var history = messages
            .Where(message => message.Role is "YOU" or "HARNESS" or "REPORT")
            .OrderBy(message => message.Sequence)
            .ToArray();
        if (history.Length == 0)
            throw new InvalidOperationException("This session contains no normalized conversation messages.");

        var selected = SelectBriefMessages(history);
        var excerptBudget = Math.Min(
            MaximumExcerptCharacters,
            Math.Max(320, (maximumCharacters - 1_200) / selected.Count));

        var builder = new StringBuilder()
            .AppendLine("<harness_continuation_brief>")
            .AppendLine("This compact brief represents prior work. Use it for continuity, but follow the current request after this block. Do not claim omitted history, hidden provider state, or unverified code changes were recovered.")
            .AppendLine($"Source: {source?.SourceKind ?? "Harness durable session"}")
            .AppendLine($"History records: {history.Length}; brief excerpts: {selected.Count}")
            .AppendLine();
        foreach (var entry in selected)
        {
            var role = entry.Message.Role switch
            {
                "YOU" => "User direction",
                "REPORT" => "Verified turn report",
                _ => "Assistant result"
            };
            builder.Append("## ").Append(role).Append(" · record ").Append(entry.Index + 1).AppendLine();
            builder.AppendLine(Excerpt(entry.Message.Text, excerptBudget));
        }
        builder.AppendLine("</harness_continuation_brief>");
        var text = Fit(builder.ToString(), maximumCharacters);
        return new ImportedContextEnvelope(
            text,
            history.Length,
            selected.Count,
            history.Length - selected.Count,
            source is not null && File.Exists(source.StoredPath) ? source.StoredPath : null);
    }

    private static IReadOnlyList<(int Index, StoredMessage Message)> SelectBriefMessages(
        IReadOnlyList<StoredMessage> history)
    {
        var indexes = new SortedSet<int>();
        for (var index = 0; index < history.Count && indexes.Count < 1; index++)
        {
            if (history[index].Role == "YOU") indexes.Add(index);
        }

        // Reports are produced from observed file and command events, making
        // them denser and safer continuity anchors than unconstrained prose.
        foreach (var index in Enumerable.Range(0, history.Count).Reverse())
        {
            if (indexes.Count >= 3) break;
            if (history[index].Role == "REPORT") indexes.Add(index);
        }

        var signalCount = 0;
        foreach (var index in Enumerable.Range(0, history.Count).Reverse())
        {
            if (signalCount >= 3) break;
            if (!ContinuationSignals.Any(signal =>
                    history[index].Text.Contains(signal, StringComparison.OrdinalIgnoreCase))) continue;
            if (indexes.Add(index)) signalCount++;
        }
        foreach (var index in Enumerable.Range(0, history.Count).Reverse())
        {
            if (indexes.Count >= 8) break;
            indexes.Add(index);
        }
        return indexes.Select(index => (index, history[index])).ToArray();
    }

    private static string Excerpt(string value, int maximumCharacters)
    {
        var normalized = value.Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..Math.Max(0, maximumCharacters - 62)].TrimEnd()
              + "\n[Excerpt shortened; raw import remains in local storage.]";
    }

    private static string Fit(string value, int budget) => value.Length <= budget
        ? value
        : value[..Math.Max(0, budget - 64)].TrimEnd()
          + "\n[Continuation brief reached its configured size limit.]\n";
}

public sealed record ImportedContextEnvelope(
    string Text,
    int TotalMessages,
    int IncludedMessages,
    int OmittedMessages,
    string? RetainedSourcePath);
