using System;
using System.Collections.Generic;
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
        if (command is MarkdownFormatCommand.Bold or MarkdownFormatCommand.Italic or
            MarkdownFormatCommand.Strikethrough or MarkdownFormatCommand.InlineCode)
            text = BoldItalicRuns().Replace(text, match =>
                selectionEnd > match.Index + 3 && selectionStart < match.Index + match.Length - 3
                    ? $"**_{match.Groups[1].Value}_**"
                    : match.Value);

        return command switch {
            MarkdownFormatCommand.Bold => ToggleInline(text, selectionStart, selectionEnd, "**", [BoldRuns(), BoldUnderscoreRuns()], "__"),
            MarkdownFormatCommand.Italic => ToggleInline(text, selectionStart, selectionEnd, "*", [ItalicRuns(), ItalicUnderscoreRuns()], "_"),
            MarkdownFormatCommand.Strikethrough => ToggleInline(text, selectionStart, selectionEnd, "~~", [StrikeRuns()]),
            MarkdownFormatCommand.InlineCode => ToggleInline(text, selectionStart, selectionEnd, "`", [CodeRuns()]),
            MarkdownFormatCommand.Heading => SetHeadingLevel(text, selectionStart, selectionEnd, 1),
            MarkdownFormatCommand.Heading2 => SetHeadingLevel(text, selectionStart, selectionEnd, 2),
            MarkdownFormatCommand.Heading3 => SetHeadingLevel(text, selectionStart, selectionEnd, 3),
            MarkdownFormatCommand.Heading4 => SetHeadingLevel(text, selectionStart, selectionEnd, 4),
            MarkdownFormatCommand.BulletList => PrefixLines(text, selectionStart, selectionEnd, "- ", ListPrefix()),
            MarkdownFormatCommand.OrderedList => PrefixOrderedLines(text, selectionStart, selectionEnd),
            MarkdownFormatCommand.TaskList => PrefixLines(text, selectionStart, selectionEnd, "- [ ] ", TaskPrefix()),
            MarkdownFormatCommand.Quote => PrefixLines(text, selectionStart, selectionEnd, "> ", QuotePrefix()),
            MarkdownFormatCommand.CodeBlock => WrapBlock(text, selectionStart, selectionEnd, "```\n", "\n```", "代码"),
            MarkdownFormatCommand.Link => InsertLink(text, selectionStart, selectionEnd),
            MarkdownFormatCommand.HorizontalRule => InsertHorizontalRule(text, selectionStart, selectionEnd),
            MarkdownFormatCommand.Table => InsertTable(text, selectionStart, selectionEnd,
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

    private static MarkdownEditResult ToggleInline(
        string text,
        int start,
        int end,
        string delimiter,
        IReadOnlyList<Regex> syntaxes,
        string? adjacentAsteriskDelimiter = null) {
        if (end <= start) return new MarkdownEditResult(text, start, end);

        var matches = syntaxes.SelectMany(syntax => syntax.Matches(text).Cast<Match>())
            .OrderBy(match => match.Index).ToArray();
        var projection = new MarkdownDocumentModel(text);
        var delimiterRanges = matches
            .SelectMany(match => new[] {
                (Start: match.Index, End: match.Index + delimiter.Length),
                (Start: match.Index + match.Length - delimiter.Length, End: match.Index + match.Length),
            }).OrderBy(range => range.Start).ToArray();

        var baseText = new System.Text.StringBuilder(text.Length);
        var originalToBase = new int[text.Length + 1];
        var formatted = new List<bool>(text.Length);
        var visibleCharacters = new List<bool>(text.Length);
        var rangeIndex = 0;
        for (var source = 0; source < text.Length;) {
            originalToBase[source] = baseText.Length;
            if (rangeIndex < delimiterRanges.Length && source == delimiterRanges[rangeIndex].Start) {
                var rangeEnd = delimiterRanges[rangeIndex].End;
                while (source < rangeEnd) originalToBase[++source] = baseText.Length;
                rangeIndex++;
                continue;
            }
            baseText.Append(text[source]);
            var isVisible = projection.VisibleOffsetFromSource(source + 1) >
                projection.VisibleOffsetFromSource(source);
            visibleCharacters.Add(isVisible);
            formatted.Add(isVisible && matches.Any(match =>
                source >= match.Index + delimiter.Length &&
                source < match.Index + match.Length - delimiter.Length));
            originalToBase[++source] = baseText.Length;
        }

        var baseStart = originalToBase[start];
        var baseEnd = originalToBase[end];
        if (baseEnd <= baseStart) return new MarkdownEditResult(text, start, end);
        var selectedVisible = Enumerable.Range(baseStart, baseEnd - baseStart)
            .Where(index => visibleCharacters[index]).ToArray();
        if (selectedVisible.Length == 0) return new MarkdownEditResult(text, start, end);
        var remove = selectedVisible.All(index => formatted[index]);
        foreach (var index in selectedVisible) formatted[index] = !remove;
        FillFormattingAcrossHiddenSyntax(formatted, visibleCharacters);

        var output = new System.Text.StringBuilder(baseText.Length + delimiter.Length * 4);
        var baseToOutput = new int[baseText.Length + 1];
        var active = false;
        var activeDelimiter = delimiter;
        for (var index = 0; index < baseText.Length; index++) {
            if (formatted[index] != active) {
                if (formatted[index]) {
                    var runEnd = index;
                    while (runEnd < formatted.Count && formatted[runEnd]) runEnd++;
                    activeDelimiter = adjacentAsteriskDelimiter != null &&
                        ((index > 0 && baseText[index - 1] == '*') ||
                         (runEnd < baseText.Length && baseText[runEnd] == '*'))
                        ? adjacentAsteriskDelimiter
                        : delimiter;
                }
                output.Append(activeDelimiter);
                active = formatted[index];
            }
            baseToOutput[index] = output.Length;
            output.Append(baseText[index]);
        }
        if (active) output.Append(activeDelimiter);
        baseToOutput[baseText.Length] = active ? output.Length - activeDelimiter.Length : output.Length;
        return new MarkdownEditResult(output.ToString(), baseToOutput[baseStart], baseToOutput[baseEnd]);
    }

    private static void FillFormattingAcrossHiddenSyntax(
        IList<bool> formatted,
        IReadOnlyList<bool> visibleCharacters) {
        for (var index = 0; index < visibleCharacters.Count; index++) {
            if (visibleCharacters[index]) continue;
            var left = index - 1;
            while (left >= 0 && !visibleCharacters[left]) left--;
            var right = index + 1;
            while (right < visibleCharacters.Count && !visibleCharacters[right]) right++;
            if (left >= 0 && right < visibleCharacters.Count && formatted[left] == formatted[right])
                formatted[index] = formatted[left];
        }
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

    private static MarkdownEditResult InsertHorizontalRule(string text, int start, int end) {
        NormalizeSelection(text, ref start, ref end);
        var leadingBreaks = 0;
        for (var index = start - 1; index >= 0 && text[index] == '\n'; index--)
            leadingBreaks++;
        // A rule at the beginning of the document does not need a separator.
        var requiredLeadingBreaks = start == 0 ? 0 : 2;
        var leading = new string('\n', Math.Max(0, requiredLeadingBreaks - leadingBreaks));
        var trailingBreaks = 0;
        while (end + trailingBreaks < text.Length && text[end + trailingBreaks] == '\n')
            trailingBreaks++;
        var trailing = trailingBreaks == 0 ? "\n" : string.Empty;
        var result = text[..start] + leading + "---" + trailing + text[end..];
        var caret = start + leading.Length + 3 + trailing.Length;
        return new MarkdownEditResult(result, caret, caret);
    }

    private static MarkdownEditResult InsertTable(string text, int start, int end, string table) {
        NormalizeSelection(text, ref start, ref end);
        var leading = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
        var existingLineBreaks = 0;
        while (end + existingLineBreaks < text.Length &&
               text[end + existingLineBreaks] == '\n' && existingLineBreaks < 3)
            existingLineBreaks++;
        var addedLineBreaks = new string('\n', 3 - existingLineBreaks);
        var result = text[..start] + leading + table + addedLineBreaks + text[end..];
        var caret = start + leading.Length + table.Length + 3;
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

    private static MarkdownEditResult SetHeadingLevel(string text, int start, int end, int level) {
        GetLineRange(text, start, end, out var lineStart, out var lineEnd);
        var lines = text[lineStart..lineEnd].Split('\n');
        var requestedPrefix = new string('#', level) + " ";
        var nonEmptyLines = lines.Where(line => line.Length > 0).ToArray();
        var removeHeading = nonEmptyLines.Length > 0 &&
            nonEmptyLines.All(line => line.StartsWith(requestedPrefix, StringComparison.Ordinal));

        for (var index = 0; index < lines.Length; index++) {
            if (lines[index].Length == 0) continue;
            var content = HeadingPrefix().Replace(lines[index], string.Empty, 1);
            lines[index] = removeHeading ? content : requestedPrefix + content;
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

    [GeneratedRegex(@"(?<!\*)\*\*(?!\*)(?=\S)(.+?)(?<=\S)\*\*(?!\*)", RegexOptions.Singleline)]
    private static partial Regex BoldRuns();

    [GeneratedRegex(@"(?<!\*)\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*(?!\*)", RegexOptions.Singleline)]
    private static partial Regex BoldItalicRuns();

    [GeneratedRegex(@"(?<!_)__(?!_)(?=\S)(.+?)(?<=\S)__(?!_)", RegexOptions.Singleline)]
    private static partial Regex BoldUnderscoreRuns();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(?=\S)(.+?)(?<=\S)\*(?!\*)", RegexOptions.Singleline)]
    private static partial Regex ItalicRuns();

    [GeneratedRegex(@"(?<!_)_(?!_)(?=\S)(.+?)(?<=\S)_(?!_)", RegexOptions.Singleline)]
    private static partial Regex ItalicUnderscoreRuns();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline)]
    private static partial Regex StrikeRuns();

    [GeneratedRegex(@"(?<!`)`([^`\n]+)`(?!`)")]
    private static partial Regex CodeRuns();
}
