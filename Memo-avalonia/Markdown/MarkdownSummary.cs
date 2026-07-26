using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Memo.Markdown;

public static partial class MarkdownSummary {
    public static string GetTitle(string? markdown) {
        var lines = ExtractLines(markdown, 1);
        if (lines.Count > 0) return lines[0];
        return ContainsImage().IsMatch(markdown ?? string.Empty) ? "图片备忘录" : string.Empty;
    }

    public static string GetSubtitle(string? markdown) {
        var lines = ExtractLines(markdown, 2);
        return lines.Count > 1 ? lines[1] : string.Empty;
    }

    public static IReadOnlyList<string> ExtractLines(string? markdown, int maximum) {
        var result = new List<string>(Math.Max(0, maximum));
        if (maximum <= 0 || string.IsNullOrWhiteSpace(markdown)) return result;

        var inFence = false;
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
            var trimmed = rawLine.Trim();
            if (Fence().IsMatch(trimmed)) {
                inFence = !inFence;
                continue;
            }

            if (!inFence && (trimmed.Length == 0 || Rule().IsMatch(trimmed) || TableDivider().IsMatch(trimmed)))
                continue;

            var line = trimmed;
            if (!inFence) {
                line = BlockPrefix().Replace(line, string.Empty);
                line = TaskPrefix().Replace(line, string.Empty);
            }

            line = Image().Replace(line, match => match.Groups[1].Value);
            line = Link().Replace(line, match => match.Groups[1].Value);
            line = HtmlTag().Replace(line, string.Empty);
            line = InlineMarker().Replace(line, string.Empty);
            line = EscapedMarker().Replace(line, "$1");
            line = TablePipe().Replace(line, " ");
            line = Whitespace().Replace(line, " ").Trim();

            if (line.Length == 0) continue;
            result.Add(line);
            if (result.Count == maximum) break;
        }

        return result;
    }

    [GeneratedRegex(@"^(```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*((#{1,6}|>+)\s*|([-+*]|\d+[.)])\s+)")]
    private static partial Regex BlockPrefix();

    [GeneratedRegex(@"^\[[ xX]\]\s+")]
    private static partial Regex TaskPrefix();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"(?<!!)\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<!\\)(\*\*|__|~~|`|\*|_)")]
    private static partial Regex InlineMarker();

    [GeneratedRegex(@"\\([\\`*{}\[\]()#+.!_>-])")]
    private static partial Regex EscapedMarker();

    [GeneratedRegex(@"^\s*([-*_]\s*){3,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$")]
    private static partial Regex TableDivider();

    [GeneratedRegex(@"\|")]
    private static partial Regex TablePipe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ContainsImage();
}
