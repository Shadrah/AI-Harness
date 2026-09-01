using System.Globalization;
using Harness.Core.Models;

namespace Harness.Workspace;

public static class UnifiedDiffParser
{
    public static DiffDocument Parse(string diff)
    {
        var result = new List<DiffLine>();
        var added = 0;
        var removed = 0;
        int? oldLine = null;
        int? newLine = null;

        foreach (var rawLine in diff
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n'))
        {
            if (TryParseHunkHeader(rawLine, out var oldStart, out var newStart))
            {
                oldLine = oldStart;
                newLine = newStart;
                result.Add(new DiffLine(DiffLineKind.Hunk, null, null, string.Empty, rawLine));
                continue;
            }

            if (IsMetadata(rawLine) || oldLine is null || newLine is null)
            {
                result.Add(new DiffLine(
                    DiffLineKind.Metadata,
                    null,
                    null,
                    string.Empty,
                    rawLine));
                continue;
            }

            if (rawLine.StartsWith('+'))
            {
                result.Add(new DiffLine(
                    DiffLineKind.Added,
                    null,
                    newLine,
                    "+",
                    rawLine[1..]));
                newLine++;
                added++;
                continue;
            }

            if (rawLine.StartsWith('-'))
            {
                result.Add(new DiffLine(
                    DiffLineKind.Removed,
                    oldLine,
                    null,
                    "−",
                    rawLine[1..]));
                oldLine++;
                removed++;
                continue;
            }

            if (rawLine.StartsWith(' '))
            {
                result.Add(new DiffLine(
                    DiffLineKind.Context,
                    oldLine,
                    newLine,
                    string.Empty,
                    rawLine[1..]));
                oldLine++;
                newLine++;
                continue;
            }

            result.Add(new DiffLine(
                DiffLineKind.Metadata,
                null,
                null,
                string.Empty,
                rawLine));
        }

        return new DiffDocument(result, added, removed);
    }

    private static bool IsMetadata(string line) =>
        line.StartsWith("diff --git ", StringComparison.Ordinal)
        || line.StartsWith("index ", StringComparison.Ordinal)
        || line.StartsWith("--- ", StringComparison.Ordinal)
        || line.StartsWith("+++ ", StringComparison.Ordinal)
        || line.StartsWith("new file mode ", StringComparison.Ordinal)
        || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
        || line.StartsWith("similarity index ", StringComparison.Ordinal)
        || line.StartsWith("rename from ", StringComparison.Ordinal)
        || line.StartsWith("rename to ", StringComparison.Ordinal)
        || line.StartsWith("Binary files ", StringComparison.Ordinal)
        || line.StartsWith("\\ No newline", StringComparison.Ordinal);

    private static bool TryParseHunkHeader(
        string line,
        out int oldStart,
        out int newStart)
    {
        oldStart = 0;
        newStart = 0;
        if (!line.StartsWith("@@ -", StringComparison.Ordinal))
        {
            return false;
        }

        var oldStartIndex = 4;
        var oldEnd = FindNumberEnd(line, oldStartIndex);
        var plus = line.IndexOf(" +", oldEnd, StringComparison.Ordinal);
        if (oldEnd == oldStartIndex || plus < 0)
        {
            return false;
        }
        var newStartIndex = plus + 2;
        var newEnd = FindNumberEnd(line, newStartIndex);
        return int.TryParse(
                line.AsSpan(oldStartIndex, oldEnd - oldStartIndex),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out oldStart)
            && int.TryParse(
                line.AsSpan(newStartIndex, newEnd - newStartIndex),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out newStart);
    }

    private static int FindNumberEnd(string value, int start)
    {
        var offset = start;
        while (offset < value.Length && char.IsAsciiDigit(value[offset]))
        {
            offset++;
        }
        return offset;
    }
}
