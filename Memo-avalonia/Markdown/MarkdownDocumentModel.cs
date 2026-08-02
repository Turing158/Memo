using Markdig;
using Markdig.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Memo.Markdown;

internal enum MarkdownVisualKind { Normal, Heading1, Heading2, Heading3, Heading4, Bold, Italic, Strike, Underline, Mark, Code, Link, Quote, Image, Rule, Table, Task, OrderedListMarker }

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

    public void SetEditableMarkdown(string? markdown) {
        Markdown = Normalize(markdown);
        RebuildProjection(ensureTableTrailingLineBreaks: true);
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
            var sourceStart = SourceOffsetForInsertion(prefix);
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
        RebuildProjection(ensureTableTrailingLineBreaks: true);
        var caret = Math.Clamp(prefix + inserted.Length, 0, VisibleText.Length);
        return (caret, caret);
    }

    internal int SourceOffsetForInsertion(int visibleOffset) {
        if (visibleOffset == 0 || visibleOffset >= VisibleText.Length)
            return SourceOffsetFromVisible(visibleOffset, trailingAffinity: true);

        var before = SourceOffsetFromVisible(visibleOffset, trailingAffinity: false);
        var after = SourceOffsetFromVisible(visibleOffset, trailingAffinity: true);
        // The projection hides one of the two Markdown line breaks that terminate a quote.
        // At the first visible position below the quote, cross only that separator. Using the
        // full trailing affinity could also cross the next paragraph's hidden formatting or
        // object syntax, while inserting before the separator creates a lazy quote continuation.
        foreach (var quote in Spans.Where(span =>
                     span.Kind == MarkdownVisualKind.Quote && span.End + 1 == visibleOffset)) {
            var quoteSourceEnd = Math.Clamp(
                quote.SourceStart + quote.SourceLength, quote.SourceStart, Markdown.Length);
            var separatorEnd = quoteSourceEnd + 2;
            if (separatorEnd <= Markdown.Length &&
                Markdown[quoteSourceEnd] == '\n' && Markdown[quoteSourceEnd + 1] == '\n' &&
                _sourceToVisible[separatorEnd] == visibleOffset)
                return separatorEnd;
        }
        if (after > before && QuotePrefixesOnly().IsMatch(Markdown[before..after]))
            return after;
        return before;
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

    private void RebuildProjection(bool ensureTableTrailingLineBreaks = false) {
        Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
        if (ensureTableTrailingLineBreaks) {
            // Reparse after each normalization so later block spans use the updated source offsets.
            if (EnsureExplicitQuotePrefixes())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureTableTrailingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureRuleTrailingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureQuoteTrailingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
        }

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
            if (end > start || style.Kind == MarkdownVisualKind.Quote)
                generatedSpans.Add(new MarkdownVisualSpan(start, end - start, style.Kind,
                    style.Start, style.End - style.Start, style.LinkTarget));
        }

        VisibleText = visible.ToString();
        _sourceToVisible = sourceToVisible;
        _visibleCharacters = visibleCharacters;
        BuildVisibleAffinityMaps();
        Spans = generatedSpans.OrderBy(span => span.Start).ThenByDescending(span => span.Length).ToArray();
        ProjectionVersion++;
    }

    private bool EnsureExplicitQuotePrefixes() {
        var replacements = new List<(int Start, int End, string Prefix)>();
        foreach (var block in Ast.OfType<QuoteBlock>()) {
            var blockStart = Math.Clamp(block.Span.Start, 0, Markdown.Length);
            var blockEnd = Math.Clamp(block.Span.End + 1, blockStart, Markdown.Length);
            var firstLineStart = blockStart == 0 ? 0 : Markdown.LastIndexOf('\n', blockStart - 1) + 1;
            var firstLineEnd = Markdown.IndexOf('\n', firstLineStart);
            if (firstLineEnd < 0 || firstLineEnd > blockEnd) firstLineEnd = blockEnd;
            var markerOffset = Markdown.IndexOf('>', firstLineStart, firstLineEnd - firstLineStart);
            if (markerOffset < 0) continue;
            var indentation = Markdown[firstLineStart..markerOffset];
            var prefix = indentation + "> ";

            var lineStart = firstLineEnd < blockEnd ? firstLineEnd + 1 : blockEnd;
            while (lineStart < blockEnd) {
                var lineEnd = Markdown.IndexOf('\n', lineStart);
                if (lineEnd < 0 || lineEnd > blockEnd) lineEnd = blockEnd;
                if (!QuoteItemLine().IsMatch(Markdown[lineStart..lineEnd])) {
                    var replacementEnd = lineStart;
                    while (replacementEnd < lineEnd &&
                           replacementEnd - lineStart < indentation.Length &&
                           Markdown[replacementEnd] is ' ' or '\t')
                        replacementEnd++;
                    replacements.Add((lineStart, replacementEnd, prefix));
                }

                lineStart = lineEnd + 1;
            }
        }
        if (replacements.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var replacement in replacements.Distinct().OrderByDescending(candidate => candidate.Start)) {
            output.Remove(replacement.Start, replacement.End - replacement.Start);
            output.Insert(replacement.Start, replacement.Prefix);
        }
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureTableTrailingLineBreaks() {
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast) {
            if (!block.GetType().Name.Contains("Table", StringComparison.Ordinal)) continue;
            var end = Math.Clamp(block.Span.End + 1, 0, Markdown.Length);
            var existingLineBreaks = 0;
            while (end + existingLineBreaks < Markdown.Length && existingLineBreaks < 3 &&
                   Markdown[end + existingLineBreaks] == '\n')
                existingLineBreaks++;
            if (existingLineBreaks < 3) insertions.Add((end, 3 - existingLineBreaks));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureRuleTrailingLineBreaks() {
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast.OfType<ThematicBreakBlock>()) {
            var end = Math.Clamp(block.Span.End + 1, 0, Markdown.Length);
            if (end >= Markdown.Length || Markdown[end] != '\n')
                insertions.Add((end, 1));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureQuoteTrailingLineBreaks() {
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast.OfType<QuoteBlock>()) {
            var end = Math.Clamp(block.Span.End + 1, 0, Markdown.Length);
            var quoteLineStart = Markdown.LastIndexOf('\n', Math.Max(0, end - 2)) + 1;
            var quoteLine = Markdown[quoteLineStart..Math.Clamp(block.Span.End + 1, quoteLineStart, Markdown.Length)];
            var hasEmptyTrailingQuoteLine = Regex.IsMatch(quoteLine, @"^\s*>\s*$");
            var requiredLineBreaks = hasEmptyTrailingQuoteLine ? 1 : 2;
            var existingLineBreaks = 0;
            while (end + existingLineBreaks < Markdown.Length && existingLineBreaks < requiredLineBreaks &&
                   Markdown[end + existingLineBreaks] == '\n')
                existingLineBreaks++;
            if (existingLineBreaks < requiredLineBreaks)
                insertions.Add((end, requiredLineBreaks - existingLineBreaks));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private void AnalyzeBlocks(bool[] hidden, Dictionary<int, Replacement> replacements, List<SourceStyle> styles) {
        foreach (var block in Ast) {
            var start = Math.Clamp(block.Span.Start, 0, Markdown.Length);
            var end = Math.Clamp(block.Span.End + 1, start, Markdown.Length);
            switch (block) {
                case HeadingBlock heading:
                    var prefixEnd = FindContentStart(start, end);
                    Hide(hidden, start, prefixEnd);
                    styles.Add(new SourceStyle(prefixEnd, end, heading.Level switch {
                        1 => MarkdownVisualKind.Heading1,
                        2 => MarkdownVisualKind.Heading2,
                        3 => MarkdownVisualKind.Heading3,
                        _ => MarkdownVisualKind.Heading4,
                    }));
                    break;
                case QuoteBlock:
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.Quote));
                    HideLinePrefixes(hidden, start, end, QuotePrefix());
                    // Keep Markdown's blank separator after the quote without adding a second
                    // editable empty line to the WYSIWYG projection.
                    if (end + 1 < Markdown.Length &&
                        Markdown[end] == '\n' && Markdown[end + 1] == '\n')
                        hidden[end + 1] = true;
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
                    // A table already hides its own structural separator. In that case the last
                    // newline is still needed to place the rule on the following visual line.
                    if (start >= 2 && Markdown[start - 1] == '\n' && Markdown[start - 2] == '\n' &&
                        !hidden[start - 2])
                        hidden[start - 1] = true;
                    break;
                default:
                    if (block.GetType().Name.Contains("Table", StringComparison.Ordinal)) {
                        replacements[start] = new Replacement(end, "￼", MarkdownVisualKind.Table);
                        // The first two line breaks stay in Markdown source, but never become
                        // editable caret positions in the projected editor.
                        if (end + 1 < Markdown.Length &&
                            Markdown[end] == '\n' && Markdown[end + 1] == '\n')
                            Hide(hidden, end, end + 2);
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
            var kind = task
                ? MarkdownVisualKind.Task
                : char.IsDigit(marker[0]) ? MarkdownVisualKind.OrderedListMarker : MarkdownVisualKind.Normal;
            replacements[absolute] = new Replacement(prefixEnd, replacement, kind);
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
    [GeneratedRegex(@"^[ \t]*>")] private static partial Regex QuoteItemLine();
    [GeneratedRegex(@"^[ \t]*(?:>[ \t]?)+$")] private static partial Regex QuotePrefixesOnly();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$")] private static partial Regex TableDelimiter();
    [GeneratedRegex(@"<(/)?([A-Za-z][A-Za-z0-9]*)([^<>]*)>")] private static partial Regex HtmlTagSyntax();
    [GeneratedRegex(@"<(script|style|iframe|object|embed|svg|math)\b[^<>]*>(?:.*?</\1\s*>|.*\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex DangerousHtmlContainerSyntax();
    [GeneratedRegex("\\s+([A-Za-z_:][\\w:.-]*)\\s*=\\s*(?:(['\\\"])(.*?)\\2|([^\\s'\\\"=<>`]+))", RegexOptions.Singleline)] private static partial Regex HtmlAttributeSyntax();
}
