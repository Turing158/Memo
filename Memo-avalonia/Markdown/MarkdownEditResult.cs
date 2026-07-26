namespace Memo.Markdown;

public readonly record struct MarkdownEditResult(
    string Text,
    int SelectionStart,
    int SelectionEnd);
