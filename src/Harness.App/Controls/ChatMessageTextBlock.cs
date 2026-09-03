using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Harness.App.Controls;

public sealed class ChatMessageTextBlock : SelectableTextBlock
{
    private static readonly Regex LinkPattern = new(
        @"(?<markdown>!?\[(?<label>[^\]\r\n]*)\]\((?<target><[^>\r\n]+>|[^)\r\n]+)\))|(?<url>(?:https?|file)://[^\s<>\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly StyledProperty<string> MessageTextProperty =
        AvaloniaProperty.Register<ChatMessageTextBlock, string>(
            nameof(MessageText),
            string.Empty);

    private readonly MenuItem _copySelectionItem;

    static ChatMessageTextBlock()
    {
        MessageTextProperty.Changed.AddClassHandler<ChatMessageTextBlock>(
            static (control, _) => control.RenderMessage());
    }

    public ChatMessageTextBlock()
    {
        _copySelectionItem = new MenuItem { Header = "Copy selection" };
        _copySelectionItem.Click += (_, _) => Copy();
        var contextMenu = new ContextMenu
        {
            Items =
            {
                _copySelectionItem
            }
        };
        contextMenu.Opened += (_, _) => _copySelectionItem.IsEnabled = CanCopy;
        ContextMenu = contextMenu;
    }

    public string MessageText
    {
        get => GetValue(MessageTextProperty);
        set => SetValue(MessageTextProperty, value);
    }

    private void RenderMessage()
    {
        var message = MessageText ?? string.Empty;
        var links = ParseLinks(message);
        if (links.Count == 0)
        {
            Inlines?.Clear();
            Text = message;
            return;
        }

        Text = null;
        Inlines ??= [];
        Inlines.Clear();
        var cursor = 0;
        foreach (var link in links)
        {
            if (link.Start > cursor)
            {
                Inlines.Add(new Run(message[cursor..link.Start]));
            }

            var button = new HyperlinkButton
            {
                Content = link.Label,
                NavigateUri = link.Uri
            };
            button.Classes.Add("chat-link");
            ToolTip.SetTip(button, link.Uri.AbsoluteUri);
            Inlines.Add(new InlineUIContainer(button));
            cursor = link.Start + link.Length;
        }

        if (cursor < message.Length)
        {
            Inlines.Add(new Run(message[cursor..]));
        }
    }

    internal static IReadOnlyList<ChatLinkMatch> ParseLinks(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return [];
        var result = new List<ChatLinkMatch>();
        foreach (Match match in LinkPattern.Matches(message))
        {
            var isMarkdown = match.Groups["markdown"].Success;
            var rawTarget = isMarkdown
                ? match.Groups["target"].Value
                : TrimRawUrl(match.Groups["url"].Value);
            if (!TryCreateSafeUri(rawTarget, out var uri)) continue;

            var consumedLength = isMarkdown ? match.Length : rawTarget.Length;
            var requestedLabel = isMarkdown ? match.Groups["label"].Value.Trim() : string.Empty;
            var label = BuildLinkLabel(requestedLabel, uri);
            result.Add(new ChatLinkMatch(match.Index, consumedLength, label, uri));
        }
        return result;
    }

    private static string TrimRawUrl(string value) =>
        value.TrimEnd('.', ',', ';', ':', '!', ')', ']', '}');

    private static bool TryCreateSafeUri(string candidate, out Uri uri)
    {
        var cleaned = candidate.Trim().Trim('<', '>').Trim();
        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https" or "file")
        {
            uri = parsed;
            return true;
        }

        if (Path.IsPathFullyQualified(cleaned))
        {
            try
            {
                uri = new Uri(Path.GetFullPath(cleaned));
                return uri.IsFile;
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
            {
            }
        }

        uri = null!;
        return false;
    }

    private static string BuildLinkLabel(string requestedLabel, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(requestedLabel)
            && !Uri.TryCreate(requestedLabel, UriKind.Absolute, out _))
        {
            return Shorten(requestedLabel, 58);
        }

        if (uri.IsFile)
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrWhiteSpace(fileName) ? "Open file" : Shorten(fileName, 58);
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 0) return host;
        var pathSummary = string.Join("/", segments.TakeLast(Math.Min(2, segments.Length)));
        return Shorten($"{host} › {pathSummary}", 58);
    }

    private static string Shorten(string value, int maximumLength)
    {
        var normalized = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..(maximumLength - 1)].TrimEnd()}…";
    }
}

internal sealed record ChatLinkMatch(
    int Start,
    int Length,
    string Label,
    Uri Uri);
