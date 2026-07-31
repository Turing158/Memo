using Markdig;
using Markdig.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Memo.Markdown;

internal enum MarkdownVisualKind { Normal, Heading1, Heading2, Heading3, Bold, Italic, Strike, Underline, Mark, Code, Link, Quote, Image, Rule, Table, Task }

internal readonly record struct MarkdownVisualSpan(
    int Start,
    int Length,
    MarkdownVisualKind Kind,
    int SourceStart,
    int SourceLength,
    string? LinkTarget = null,
    string? ImageUri = null,
    string? AltText = null) {
    public int End => Start + Length;
}

/// <summary>
/// Owns the Markdown source and its editable, marker-free projection. Markdig remains the
/// authority for block boundaries; only the changed source range is replaced after input.
/// </summary>
internal sealed partial class MarkdownDocumentModel {
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private int[] _visibleToSourceBefore = [0];
    private int[] _visibleToSourceAfter = [0];
    private int[] _sourceToVisible = [0];
    private IReadOnlyList<SourceRange> _visibleCharacters = [];

    public MarkdownDocumentModel(string? markdown = null) => SetMarkdown(markdown);

    public string Markdown { get; private set; } = string.Empty;
    public string VisibleText { get; private set; } = string.Empty;
    public long ProjectionVersion { get; private set; }
    public MarkdownDocument Ast { get; private set; } = new();
    public IReadOnlyList<MarkdownVisualSpan> Spans { get; private set; } = [];

    public void SetMarkdown(string? markdown) {
        Markdown = Normalize(markdown);
        RebuildProjection();
    }

    public int SourceOffsetFromVisible(int visibleOffset, bool trailingAffinity = true) {
        var map = trailingAffinity ? _visibleToSourceAfter : _visibleToSourceBefore;
        return map[Math.Clamp(visibleOffset, 0, map.Length - 1)];
    }

    public int VisibleOffsetFromSource(int sourceOffset) =>
        _sourceToVisible[Math.Clamp(sourceOffset, 0, _sourceToVisible.Length - 1)];

    public (int Start, int End) ApplyVisibleText(string? visibleText) =>
        ApplyVisibleText(visibleText, null);

    public (int Start, int End) ApplyVisibleText(
        string? visibleText,
        int changeOffset,
        int removalLength,
        int insertionLength) =>
        ApplyVisibleText(visibleText, new VisibleTextChange(
            changeOffset, removalLength, insertionLength));

    private (int Start, int End) ApplyVisibleText(
        string? visibleText,
        VisibleTextChange? change) {
        var next = Normalize(visibleText);
        if (next == VisibleText) return (0, 0);

        int prefix;
        int oldSuffix;
        int newSuffix;
        if (IsExactChangeValid(next, change)) {
            var exact = change!.Value;
            prefix = exact.Offset;
            oldSuffix = exact.Offset + exact.RemovalLength;
            newSuffix = exact.Offset + exact.InsertionLength;
        }
        else {
            prefix = 0;
            var shared = Math.Min(VisibleText.Length, next.Length);
            while (prefix < shared && VisibleText[prefix] == next[prefix]) prefix++;
            oldSuffix = VisibleText.Length;
            newSuffix = next.Length;
            while (oldSuffix > prefix && newSuffix > prefix &&
                   VisibleText[oldSuffix - 1] == next[newSuffix - 1]) {
                oldSuffix--;
                newSuffix--;
            }
        }

        var inserted = next[prefix..newSuffix];
        if (oldSuffix == prefix) {
            var sourceStart = SourceOffsetFromVisible(
                prefix, trailingAffinity: prefix == 0 || prefix >= VisibleText.Length);
            Markdown = Markdown.Insert(sourceStart, inserted);
        }
        else {
            var visibleRanges = _visibleCharacters.Skip(prefix).Take(oldSuffix - prefix)
                .Distinct().ToArray();
            var insertionPoint = visibleRanges.Length == 0
                ? SourceOffsetFromVisible(prefix)
                : visibleRanges.Min(range => range.Start);
            var markerRanges = inserted.Length == 0
                ? EmptyMarkerRangesForSelection(prefix, oldSuffix)
                : [];
            var removed = MergeRanges(visibleRanges.Concat(markerRanges));
            var output = new StringBuilder(Markdown.Length - removed.Sum(range => range.Length) + inserted.Length);
            var rangeIndex = 0;
            for (var source = 0; source < Markdown.Length;) {
                if (source == insertionPoint) output.Append(inserted);
                if (rangeIndex < removed.Length && source == removed[rangeIndex].Start) {
                    source = removed[rangeIndex].End;
                    rangeIndex++;
                }
                else output.Append(Markdown[source++]);
            }
            if (insertionPoint == Markdown.Length) output.Append(inserted);
            Markdown = output.ToString();
        }
        RebuildProjection();
        var caret = Math.Clamp(prefix + inserted.Length, 0, VisibleText.Length);
        return (caret, caret);
    }

    private bool IsExactChangeValid(string next, VisibleTextChange? change) {
        if (change is not { } exact ||
            exact.Offset < 0 || exact.RemovalLength < 0 || exact.InsertionLength < 0 ||
            exact.Offset + exact.RemovalLength > VisibleText.Length ||
            exact.Offset + exact.InsertionLength > next.Length ||
            VisibleText.Length - exact.RemovalLength + exact.InsertionLength != next.Length)
            return false;

        return VisibleText.AsSpan(0, exact.Offset).SequenceEqual(next.AsSpan(0, exact.Offset)) &&
               VisibleText.AsSpan(exact.Offset + exact.RemovalLength)
                   .SequenceEqual(next.AsSpan(exact.Offset + exact.InsertionLength));
    }

    private IEnumerable<SourceRange> EmptyMarkerRangesForSelection(int visibleStart, int visibleEnd) {
        foreach (var span in Spans.Where(span =>
            span.Start >= visibleStart && span.End <= visibleEnd &&
            span.Kind is MarkdownVisualKind.Bold or MarkdownVisualKind.Italic or
                MarkdownVisualKind.Strike or MarkdownVisualKind.Code)) {
            var delimiterLength = span.Kind is MarkdownVisualKind.Bold or MarkdownVisualKind.Strike ? 2 : 1;
            var openStart = span.SourceStart - delimiterLength;
            var closeStart = span.SourceStart + span.SourceLength;
            if (openStart < 0 || closeStart + delimiterLength > Markdown.Length) continue;
            var open = Markdown.AsSpan(openStart, delimiterLength);
            var close = Markdown.AsSpan(closeStart, delimiterLength);
            if (open.SequenceEqual(close)) {
                yield return new SourceRange(openStart, span.SourceStart);
                yield return new SourceRange(closeStart, closeStart + delimiterLength);
            }
        }
    }

    private static SourceRange[] MergeRanges(IEnumerable<SourceRange> ranges) {
        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        if (ordered.Length == 0) return [];
        var merged = new List<SourceRange> { ordered[0] };
        foreach (var range in ordered.Skip(1)) {
            var previous = merged[^1];
            if (range.Start > previous.End) merged.Add(range);
            else if (range.End > previous.End) merged[^1] = new SourceRange(previous.Start, range.End);
        }
        return merged.ToArray();
    }

    public MarkdownVisualSpan? VisualAt(int visibleOffset, MarkdownVisualKind kind) =>
        Spans.FirstOrDefault(span => span.Kind == kind && visibleOffset >= span.Start && visibleOffset < span.End) is var found &&
        found.Length > 0 ? found : null;

    private void RebuildProjection() {
        Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
        var hidden = new bool[Markdown.Length];
        var replacements = new Dictionary<int, Replacement>();
        var sourceStyles = new List<SourceStyle>();

        AnalyzeBlocks(hidden, replacements, sourceStyles);
        AnalyzeInline(hidden, replacements, sourceStyles);
        AnalyzeSafeHtml(hidden, replacements, sourceStyles);

        var visible = new StringBuilder(Markdown.Length);
        var visibleToSource = new List<int>(Markdown.Length + 1) { 0 };
        var visibleCharacters = new List<SourceRange>(Markdown.Length);
        var sourceToVisible = new int[Markdown.Length + 1];
        var generatedSpans = new List<MarkdownVisualSpan>();

        for (var source = 0; source < Markdown.Length;) {
            sourceToVisible[source] = visible.Length;
            if (replacements.TryGetValue(source, out var replacement)) {
                var start = visible.Length;
                AppendReplacement(replacement.Text, source, replacement.SourceEnd, visible, visibleToSource, visibleCharacters);
                generatedSpans.Add(new MarkdownVisualSpan(start, replacement.Text.Length, replacement.Kind,
                    source, replacement.SourceEnd - source, replacement.LinkTarget, replacement.ImageUri, replacement.AltText));
                for (var index = source; index < replacement.SourceEnd; index++) sourceToVisible[index] = start;
                source = replacement.SourceEnd;
                sourceToVisible[source] = visible.Length;
                continue;
            }
            if (!hidden[source]) {
                visible.Append(Markdown[source]);
                visibleToSource.Add(source + 1);
                visibleCharacters.Add(new SourceRange(source, source + 1));
            }
            source++;
            sourceToVisible[source] = visible.Length;
        }

        foreach (var style in sourceStyles) {
            var start = sourceToVisible[Math.Clamp(style.Start, 0, Markdown.Length)];
            var end = sourceToVisible[Math.Clamp(style.End, 0, Markdown.Length)];
            if (end > start) generatedSpans.Add(new MarkdownVisualSpan(start, end - start, style.Kind,
                style.Start, style.End - style.Start, style.LinkTarget));
        }

        VisibleText = visible.ToString();
        _sourceToVisible = sourceToVisible;
        _visibleCharacters = visibleCharacters;
        BuildVisibleAffinityMaps();
        Spans = generatedSpans.OrderBy(span => span.Start).ThenByDescending(span => span.Length).ToArray();
        ProjectionVersion++;
    }

    private void AnalyzeBlocks(bool[] hidden, Dictionary<int, Replacement> replacements, List<SourceStyle> styles) {
        foreach (var block in Ast) {
            var start = Math.Clamp(block.Span.Start, 0, Markdown.Length);
            var end = Math.Clamp(block.Span.End + 1, start, Markdown.Length);
            switch (block) {
                case HeadingBlock heading:
                    var prefixEnd = FindContentStart(start, end);
                    Hide(hidden, start, prefixEnd);
                    styles.Add(new SourceStyle(prefixEnd, end, heading.Level switch { 1 => MarkdownVisualKind.Heading1, 2 => MarkdownVisualKind.Heading2, _ => MarkdownVisualKind.Heading3 }));
                    break;
                case QuoteBlock:
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.Quote));
                    HideLinePrefixes(hidden, start, end, QuotePrefix());
                    break;
                case ListBlock:
                    RewriteListPrefixes(hidden, replacements, start, end);
                    break;
                case FencedCodeBlock:
                    HideFenceLines(hidden, start, end);
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.Code));
                    break;
                case CodeBlock:
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.Code));
                    break;
                case ThematicBreakBlock:
                    replacements[start] = new Replacement(end, "────────────────", MarkdownVisualKind.Rule);
                    break;
                default:
                    if (block.GetType().Name.Contains("Table", StringComparison.Ordinal)) {
                        replacements[start] = new Replacement(end, "￼", MarkdownVisualKind.Table);
                    }
                    break;
            }
        }
    }

    private void BuildVisibleAffinityMaps() {
        _visibleToSourceBefore = Enumerable.Repeat(int.MaxValue, VisibleText.Length + 1).ToArray();
        _visibleToSourceAfter = new int[VisibleText.Length + 1];
        for (var source = 0; source < _sourceToVisible.Length; source++) {
            var visible = Math.Clamp(_sourceToVisible[source], 0, VisibleText.Length);
            _visibleToSourceBefore[visible] = Math.Min(_visibleToSourceBefore[visible], source);
            _visibleToSourceAfter[visible] = Math.Max(_visibleToSourceAfter[visible], source);
        }
        for (var visible = 0; visible < _visibleToSourceBefore.Length; visible++) {
            if (_visibleToSourceBefore[visible] == int.MaxValue)
                _visibleToSourceBefore[visible] = visible == 0 ? 0 : _visibleToSourceAfter[visible - 1];
        }
    }

    private void AnalyzeInline(bool[] hidden, Dictionary<int, Replacement> replacements, List<SourceStyle> styles) {
        var dangerousRanges = DangerousHtmlRanges();
        foreach (Match match in ImageSyntax().Matches(Markdown)) {
            if (IsInside(match.Index, dangerousRanges) || OverlapsReplacement(replacements, match.Index)) continue;
            replacements[match.Index] = new Replacement(match.Index + match.Length, "\uFFFC", MarkdownVisualKind.Image,
                ImageUri: match.Groups[2].Value,
                AltText: match.Groups[1].Value.Replace("\\]", "]").Replace("\\[", "["));
        }
        foreach (Match match in LinkSyntax().Matches(Markdown)) {
            if (IsInside(match.Index, dangerousRanges) || OverlapsReplacement(replacements, match.Index)) continue;
            Hide(hidden, match.Index, match.Groups[1].Index);
            Hide(hidden, match.Groups[1].Index + match.Groups[1].Length, match.Index + match.Length);
            styles.Add(new SourceStyle(match.Groups[1].Index, match.Groups[1].Index + match.Groups[1].Length,
                MarkdownVisualKind.Link, match.Groups[2].Value));
        }
        foreach (Match match in BoldItalicSyntax().Matches(Markdown)) {
            if (IsInside(match.Index, dangerousRanges)) continue;
            Hide(hidden, match.Index, match.Index + 3);
            Hide(hidden, match.Index + match.Length - 3, match.Index + match.Length);
            styles.Add(new SourceStyle(match.Index + 2, match.Index + match.Length - 2, MarkdownVisualKind.Bold));
            styles.Add(new SourceStyle(match.Index + 3, match.Index + match.Length - 3, MarkdownVisualKind.Italic));
        }
        AnalyzeDelimited(hidden, styles, BoldSyntax(), 2, MarkdownVisualKind.Bold, dangerousRanges);
        AnalyzeDelimited(hidden, styles, BoldUnderscoreSyntax(), 2, MarkdownVisualKind.Bold, dangerousRanges);
        AnalyzeDelimited(hidden, styles, StrikeSyntax(), 2, MarkdownVisualKind.Strike, dangerousRanges);
        AnalyzeDelimited(hidden, styles, CodeSyntax(), 1, MarkdownVisualKind.Code, dangerousRanges);
        AnalyzeDelimited(hidden, styles, ItalicSyntax(), 1, MarkdownVisualKind.Italic, dangerousRanges);
        AnalyzeDelimited(hidden, styles, ItalicUnderscoreSyntax(), 1, MarkdownVisualKind.Italic, dangerousRanges);
    }

    private void AnalyzeSafeHtml(bool[] hidden, Dictionary<int, Replacement> replacements, List<SourceStyle> styles) {
        var stacks = new Dictionary<string, Stack<HtmlOpen>>(StringComparer.OrdinalIgnoreCase);
        var dangerousRanges = DangerousHtmlRanges();
        foreach (Match match in HtmlTagSyntax().Matches(Markdown)) {
            if (dangerousRanges.Any(range => match.Index >= range.Start && match.Index < range.End)) continue;
            var tag = match.Groups[2].Value.ToLowerInvariant();
            var closing = match.Groups[1].Success;
            var attributes = match.Groups[3].Value;
            if (!TryReadSafeHtmlTag(tag, closing, attributes, out var target, out var alt)) continue;

            if (tag == "br") {
                replacements[match.Index] = new Replacement(match.Index + match.Length, "\n", MarkdownVisualKind.Normal);
                continue;
            }
            if (tag == "img") {
                replacements[match.Index] = new Replacement(match.Index + match.Length, "\uFFFC", MarkdownVisualKind.Image,
                    ImageUri: target, AltText: alt);
                continue;
            }

            var family = tag switch { "strong" => "b", "em" => "i", "del" or "strike" => "s", _ => tag };
            if (!closing) {
                if (!stacks.TryGetValue(family, out var stack)) stacks[family] = stack = new Stack<HtmlOpen>();
                stack.Push(new HtmlOpen(match.Index, match.Index + match.Length, target));
                continue;
            }
            if (!stacks.TryGetValue(family, out var opens) || opens.Count == 0) continue;
            var open = opens.Pop();
            Hide(hidden, open.TagStart, open.ContentStart);
            Hide(hidden, match.Index, match.Index + match.Length);
            var kind = family switch {
                "b" => MarkdownVisualKind.Bold,
                "i" => MarkdownVisualKind.Italic,
                "s" => MarkdownVisualKind.Strike,
                "u" => MarkdownVisualKind.Underline,
                "mark" => MarkdownVisualKind.Mark,
                "code" => MarkdownVisualKind.Code,
                "a" => MarkdownVisualKind.Link,
                _ => MarkdownVisualKind.Normal,
            };
            styles.Add(new SourceStyle(open.ContentStart, match.Index, kind, open.Target));
        }
    }

    private SourceRange[] DangerousHtmlRanges() => DangerousHtmlContainerSyntax().Matches(Markdown).Cast<Match>()
        .Select(match => new SourceRange(match.Index, match.Index + match.Length)).ToArray();

    private static bool IsInside(int sourceOffset, IEnumerable<SourceRange> ranges) =>
        ranges.Any(range => sourceOffset >= range.Start && sourceOffset < range.End);

    private static bool TryReadSafeHtmlTag(
        string tag, bool closing, string attributes, out string? target, out string? alt) {
        target = null;
        alt = null;
        if (tag is not ("b" or "strong" or "i" or "em" or "s" or "del" or "strike" or
            "u" or "mark" or "code" or "br" or "a" or "img")) return false;
        if (closing) return string.IsNullOrWhiteSpace(attributes) && tag is not ("br" or "img");
        if (tag is not ("a" or "img")) return string.IsNullOrWhiteSpace(attributes.Trim().TrimEnd('/'));

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in HtmlAttributeSyntax().Matches(attributes)) {
            var name = attribute.Groups[1].Value;
            if (parsed.ContainsKey(name)) return false;
            parsed[name] = attribute.Groups[3].Success
                ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
        }
        var residue = HtmlAttributeSyntax().Replace(attributes, string.Empty).Trim().TrimEnd('/').Trim();
        if (residue.Length > 0) return false;
        if (tag == "a") {
            if (parsed.Count != 1 || !parsed.TryGetValue("href", out target) || !IsSafeLink(target)) return false;
            return true;
        }
        if (parsed.Keys.Any(key => key is not ("src" or "alt")) ||
            !parsed.TryGetValue("src", out target) || !IsSafeImage(target)) return false;
        parsed.TryGetValue("alt", out alt);
        return true;
    }

    private static bool IsSafeLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "mailto";

    private static bool IsSafeImage(string target) {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return !string.IsNullOrWhiteSpace(target) && !target.StartsWith("//", StringComparison.Ordinal);
        return uri.Scheme == Uri.UriSchemeHttps;
    }

    private void AnalyzeDelimited(bool[] hidden, List<SourceStyle> styles, Regex regex, int delimiter,
        MarkdownVisualKind kind, IEnumerable<SourceRange> dangerousRanges) {
        foreach (Match match in regex.Matches(Markdown)) {
            if (IsInside(match.Index, dangerousRanges)) continue;
            Hide(hidden, match.Index, match.Index + delimiter);
            Hide(hidden, match.Index + match.Length - delimiter, match.Index + match.Length);
            styles.Add(new SourceStyle(match.Index + delimiter, match.Index + match.Length - delimiter, kind));
        }
    }

    private void RewriteListPrefixes(bool[] hidden, Dictionary<int, Replacement> replacements, int start, int end) {
        foreach (Match match in ListLinePrefix().Matches(Markdown[start..end])) {
            var absolute = start + match.Index + match.Groups[1].Length;
            var marker = match.Groups[2].Value;
            var task = match.Groups[3].Success;
            var prefixEnd = start + match.Index + match.Length;
            Hide(hidden, absolute, prefixEnd);
            var replacement = task ? (match.Groups[3].Value.Contains('x', StringComparison.OrdinalIgnoreCase) ? "☑ " : "☐ ") :
                char.IsDigit(marker[0]) ? marker + " " : "• ";
            replacements[absolute] = new Replacement(prefixEnd, replacement, task ? MarkdownVisualKind.Task : MarkdownVisualKind.Normal);
        }
    }

    private void RewriteTable(bool[] hidden, Dictionary<int, Replacement> replacements, int start, int end) {
        var lineStart = start;
        while (lineStart < end) {
            var lineEnd = Markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0 || lineEnd > end) lineEnd = end;
            var line = Markdown[lineStart..lineEnd];
            if (TableDelimiter().IsMatch(line)) Hide(hidden, lineStart, lineEnd);
            else {
                for (var index = lineStart; index < lineEnd; index++) {
                    if (Markdown[index] != '|') continue;
                    if (index == lineStart || index == lineEnd - 1) hidden[index] = true;
                    else replacements[index] = new Replacement(index + 1, "  │  ", MarkdownVisualKind.Table);
                }
            }
            lineStart = Math.Min(lineEnd + 1, end);
        }
    }

    private void HideFenceLines(bool[] hidden, int start, int end) {
        var firstEnd = Markdown.IndexOf('\n', start);
        if (firstEnd < 0 || firstEnd > end) firstEnd = end;
        Hide(hidden, start, firstEnd);
        var lastStart = Markdown.LastIndexOf('\n', Math.Max(start, end - 1));
        if (lastStart >= start) Hide(hidden, lastStart + 1, end);
    }

    private int FindContentStart(int start, int end) {
        while (start < end && (Markdown[start] == '#' || char.IsWhiteSpace(Markdown[start]))) start++;
        return start;
    }

    private void HideLinePrefixes(bool[] hidden, int start, int end, Regex prefix) {
        foreach (Match match in prefix.Matches(Markdown[start..end])) Hide(hidden, start + match.Index, start + match.Index + match.Length);
    }

    private static void Hide(bool[] hidden, int start, int end) {
        for (var index = Math.Clamp(start, 0, hidden.Length); index < Math.Clamp(end, 0, hidden.Length); index++) hidden[index] = true;
    }

    private static bool OverlapsReplacement(Dictionary<int, Replacement> replacements, int offset) =>
        replacements.Any(entry => offset >= entry.Key && offset < entry.Value.SourceEnd);

    private static void AppendReplacement(string text, int sourceStart, int sourceEnd, StringBuilder output,
        List<int> map, List<SourceRange> visibleCharacters) {
        for (var index = 0; index < text.Length; index++) {
            output.Append(text[index]);
            map.Add(index == text.Length - 1 ? sourceEnd : sourceStart);
            visibleCharacters.Add(new SourceRange(sourceStart, sourceEnd));
        }
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private readonly record struct Replacement(int SourceEnd, string Text, MarkdownVisualKind Kind,
        string? LinkTarget = null, string? ImageUri = null, string? AltText = null);
    private readonly record struct VisibleTextChange(int Offset, int RemovalLength, int InsertionLength);
    private readonly record struct SourceStyle(int Start, int End, MarkdownVisualKind Kind, string? LinkTarget = null);
    private readonly record struct SourceRange(int Start, int End) { public int Length => End - Start; }
    private readonly record struct HtmlOpen(int TagStart, int ContentStart, string? Target);

    [GeneratedRegex(@"!\[((?:\\.|[^\]\n])*)\]\(([^)\n]+)\)")] private static partial Regex ImageSyntax();
    [GeneratedRegex(@"(?<!!)\[((?:\\.|[^\]\n])+)]\(([^)\n]+)\)")] private static partial Regex LinkSyntax();
    [GeneratedRegex(@"(?<!\*)\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*(?!\*)", RegexOptions.Singleline)] private static partial Regex BoldItalicSyntax();
    [GeneratedRegex(@"(?<!\*)\*\*(?!\*)(?=\S)(.+?)(?<=\S)\*\*(?!\*)", RegexOptions.Singleline)] private static partial Regex BoldSyntax();
    [GeneratedRegex(@"(?<!_)__(?!_)(?=\S)(.+?)(?<=\S)__(?!_)", RegexOptions.Singleline)] private static partial Regex BoldUnderscoreSyntax();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline)] private static partial Regex StrikeSyntax();
    [GeneratedRegex(@"(?<!`)`([^`\n]+)`(?!`)")] private static partial Regex CodeSyntax();
    [GeneratedRegex(@"(?<!\*)\*(?!\*)(?=\S)(.+?)(?<=\S)\*(?!\*)", RegexOptions.Singleline)] private static partial Regex ItalicSyntax();
    [GeneratedRegex(@"(?<!_)_(?!_)(?=\S)(.+?)(?<=\S)_(?!_)", RegexOptions.Singleline)] private static partial Regex ItalicUnderscoreSyntax();
    [GeneratedRegex(@"(?m)^([ \t]*)([-+*]|\d+[.)])[ \t]+(\[[ xX]\][ \t]+)?")] private static partial Regex ListLinePrefix();
    [GeneratedRegex(@"(?m)^\s*>\s?")] private static partial Regex QuotePrefix();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$")] private static partial Regex TableDelimiter();
    [GeneratedRegex(@"<(/)?([A-Za-z][A-Za-z0-9]*)([^<>]*)>")] private static partial Regex HtmlTagSyntax();
    [GeneratedRegex(@"<(script|style|iframe|object|embed|svg|math)\b[^<>]*>(?:.*?</\1\s*>|.*\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex DangerousHtmlContainerSyntax();
    [GeneratedRegex("\\s+([A-Za-z_:][\\w:.-]*)\\s*=\\s*(?:(['\\\"])(.*?)\\2|([^\\s'\\\"=<>`]+))", RegexOptions.Singleline)] private static partial Regex HtmlAttributeSyntax();
}
