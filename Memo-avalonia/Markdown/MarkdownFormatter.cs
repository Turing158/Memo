using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Memo.Markdown;

public static partial class MarkdownFormatter {
    public static MarkdownEditResult Apply(
        string? source,
        int selectionStart,
        int selectionEnd,
        MarkdownFormatCommand command) {
        var text = Normalize(source);
        NormalizeSelection(text, ref selectionStart, ref selectionEnd);

        return command switch {
            MarkdownFormatCommand.Bold => Wrap(text, selectionStart, selectionEnd, "**", "**", "粗体文本"),
            MarkdownFormatCommand.Italic => Wrap(text, selectionStart, selectionEnd, "*", "*", "斜体文本"),
            MarkdownFormatCommand.Strikethrough => Wrap(text, selectionStart, selectionEnd, "~~", "~~", "删除文本"),
            MarkdownFormatCommand.InlineCode => Wrap(text, selectionStart, selectionEnd, "`", "`", "代码"),
            MarkdownFormatCommand.Heading => PrefixLines(text, selectionStart, selectionEnd, "# ", HeadingPrefix()),
            MarkdownFormatCommand.BulletList => PrefixLines(text, selectionStart, selectionEnd, "- ", ListPrefix()),
            MarkdownFormatCommand.OrderedList => PrefixOrderedLines(text, selectionStart, selectionEnd),
            MarkdownFormatCommand.TaskList => PrefixLines(text, selectionStart, selectionEnd, "- [ ] ", TaskPrefix()),
            MarkdownFormatCommand.Quote => PrefixLines(text, selectionStart, selectionEnd, "> ", QuotePrefix()),
            MarkdownFormatCommand.CodeBlock => WrapBlock(text, selectionStart, selectionEnd, "```\n", "\n```", "代码"),
            MarkdownFormatCommand.Link => InsertLink(text, selectionStart, selectionEnd),
            MarkdownFormatCommand.HorizontalRule => InsertBlock(text, selectionStart, selectionEnd, "---"),
            MarkdownFormatCommand.Table => InsertBlock(text, selectionStart, selectionEnd,
                "| 列 1 | 列 2 |\n| --- | --- |\n| 内容 | 内容 |"),
            _ => new MarkdownEditResult(text, selectionStart, selectionEnd),
        };
    }

    public static MarkdownEditResult InsertImage(
        string? source,
        int selectionStart,
        int selectionEnd,
        string altText,
        string uri) {
        var safeAlt = (altText ?? string.Empty).Replace("[", "\\[").Replace("]", "\\]");
        var syntax = $"![{safeAlt}]({uri})";
        return InsertBlock(Normalize(source), selectionStart, selectionEnd, syntax);
    }

    public static bool HasMeaningfulContent(string? source) =>
        MarkdownSummary.ExtractLines(source, 1).Count > 0;

    private static MarkdownEditResult Wrap(
        string text,
        int start,
        int end,
        string open,
        string close,
        string placeholder) {
        var hasSelection = end > start;
        var selected = hasSelection ? text[start..end] : placeholder;

        if (hasSelection && start >= open.Length && end + close.Length <= text.Length &&
            text.AsSpan(start - open.Length, open.Length).SequenceEqual(open) &&
            text.AsSpan(end, close.Length).SequenceEqual(close)) {
            var unwrapped = text.Remove(end, close.Length).Remove(start - open.Length, open.Length);
            return new MarkdownEditResult(unwrapped, start - open.Length, end - open.Length);
        }

        var replacement = open + selected + close;
        var result = text[..start] + replacement + text[end..];
        return new MarkdownEditResult(result, start + open.Length, start + open.Length + selected.Length);
    }

    private static MarkdownEditResult WrapBlock(
        string text,
        int start,
        int end,
        string open,
        string close,
        string placeholder) {
        var selected = end > start ? text[start..end] : placeholder;
        var leading = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
        var trailing = end < text.Length && text[end] != '\n' ? "\n" : string.Empty;
        var replacement = leading + open + selected + close + trailing;
        var result = text[..start] + replacement + text[end..];
        var contentStart = start + leading.Length + open.Length;
        return new MarkdownEditResult(result, contentStart, contentStart + selected.Length);
    }

    private static MarkdownEditResult InsertLink(string text, int start, int end) {
        var label = end > start ? text[start..end] : "链接文本";
        const string url = "https://";
        var replacement = $"[{label}]({url})";
        var result = text[..start] + replacement + text[end..];
        var urlStart = start + label.Length + 3;
        return new MarkdownEditResult(result, urlStart, urlStart + url.Length);
    }

    private static MarkdownEditResult InsertBlock(string text, int start, int end, string block) {
        NormalizeSelection(text, ref start, ref end);
        var leading = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
        var trailing = end < text.Length && text[end] != '\n' ? "\n" : string.Empty;
        var result = text[..start] + leading + block + trailing + text[end..];
        var caret = start + leading.Length + block.Length;
        return new MarkdownEditResult(result, caret, caret);
    }

    private static MarkdownEditResult PrefixLines(
        string text,
        int start,
        int end,
        string prefix,
        Regex removablePrefix) {
        GetLineRange(text, start, end, out var lineStart, out var lineEnd);
        var selected = text[lineStart..lineEnd];
        var lines = selected.Split('\n');
        var allPrefixed = lines.Where(line => line.Length > 0).All(line => removablePrefix.IsMatch(line));

        for (var index = 0; index < lines.Length; index++) {
            if (lines[index].Length == 0) continue;
            lines[index] = allPrefixed
                ? removablePrefix.Replace(lines[index], string.Empty, 1)
                : prefix + lines[index];
        }

        var replacement = string.Join("\n", lines);
        var result = text[..lineStart] + replacement + text[lineEnd..];
        return new MarkdownEditResult(result, lineStart, lineStart + replacement.Length);
    }

    private static MarkdownEditResult PrefixOrderedLines(string text, int start, int end) {
        GetLineRange(text, start, end, out var lineStart, out var lineEnd);
        var lines = text[lineStart..lineEnd].Split('\n');
        var allPrefixed = lines.Where(line => line.Length > 0).All(line => OrderedPrefix().IsMatch(line));
        var order = 1;
        for (var index = 0; index < lines.Length; index++) {
            if (lines[index].Length == 0) continue;
            lines[index] = allPrefixed
                ? OrderedPrefix().Replace(lines[index], string.Empty, 1)
                : $"{order++}. {lines[index]}";
        }

        var replacement = string.Join("\n", lines);
        var result = text[..lineStart] + replacement + text[lineEnd..];
        return new MarkdownEditResult(result, lineStart, lineStart + replacement.Length);
    }

    private static void GetLineRange(string text, int start, int end, out int lineStart, out int lineEnd) {
        NormalizeSelection(text, ref start, ref end);
        lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        lineEnd = end >= text.Length ? text.Length : text.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = text.Length;
    }

    private static void NormalizeSelection(string text, ref int start, ref int end) {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        if (start > end) (start, end) = (end, start);
    }

    private static string Normalize(string? source) =>
        (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^\s*[-+*]\s+")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"^\s*\d+[.)]\s+")]
    private static partial Regex OrderedPrefix();

    [GeneratedRegex(@"^\s*[-+*]\s+\[[ xX]\]\s+")]
    private static partial Regex TaskPrefix();

    [GeneratedRegex(@"^\s*>\s?")]
    private static partial Regex QuotePrefix();
}
