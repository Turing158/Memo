namespace Memo.Markdown;

public sealed class MarkdownEditSession {
    public string Snapshot { get; private set; } = string.Empty;

    public void Begin(string? markdown) => Snapshot = markdown ?? string.Empty;

    public void Commit(string? markdown) => Snapshot = markdown ?? string.Empty;

    public string Restore() => Snapshot;
}
