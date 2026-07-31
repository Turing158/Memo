using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Memo.Components.Dialogs;
using Memo.Markdown;
using Memo.Services;
using Memo.UI;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace Memo.Components;

public partial class MarkdownEditor : UserControl {
    public static readonly StyledProperty<bool> UseBorderlessChromeProperty =
        AvaloniaProperty.Register<MarkdownEditor, bool>(nameof(UseBorderlessChrome));

    private readonly MarkdownImageStore _imageStore;
    private readonly DebouncedAction _autoSave;
    private readonly MarkdownEditSession _editSession = new();
    private readonly MarkdownDocumentModel _model = new();
    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();
    private readonly MarkdownProjectionSnapshot _projectionSnapshot = new();
    private readonly MarkdownInlineGenerator _inlineGenerator;
    private DispatcherTimer? _statusTimer;
    private bool _suppressTextChanged;
    private bool _projectionRefreshQueued;
    private int _projectionRefreshTicket;
    private (int Start, int End) _pendingProjectionSelection;
    private bool _hideToolbarInPreview;
    private bool _blendBordersInPreview;
    private readonly HashSet<MarkdownFormatCommand> _pendingInlineFormats = [];
    private bool _hasPendingFormatOverride;
    private int _pendingCaretVisible = -1;
    private bool _updatingSelection;
    private (int Offset, int RemovalLength, int InsertionLength)? _pendingVisibleTextChange;

    public MarkdownEditor() : this(null) { }

    internal MarkdownEditor(string? imageRoot) {
        _imageStore = new MarkdownImageStore(imageRoot);
        InitializeComponent();
        _editor.TextArea.SelectionBrush = (IBrush)Application.Current!.Resources["BgHoverBrush"]!;
        _editor.TextArea.SelectionForeground = (IBrush)Application.Current.Resources["TextPrimaryBrush"]!;
        _editor.TextArea.Caret.CaretBrush = (IBrush)Application.Current.Resources["AccentPrimaryBrush"]!;
        _autoSave = new DebouncedAction(TimeSpan.FromMilliseconds(500));
        _editor.TextArea.TextView.LineTransformers.Add(new MarkdownColorizer(_model, _projectionSnapshot));
        _inlineGenerator = new MarkdownInlineGenerator(
            _model, _projectionSnapshot, _imageStore.RootDirectory, _editor.TextArea.TextView,
            ToggleTask, MoveCaretToVisibleOffset, UpdateTableSource);
        _inlineGenerator.EnableEmbeddedControlLayout();
        _editor.TextArea.TextView.ElementGenerators.Add(_inlineGenerator);
        _editor.Document.Changing += OnEditorDocumentChanging;
        _editor.TextArea.SelectionChanged += (_, _) => OnEditorSelectionChanged();
        _editor.TextArea.Caret.PositionChanged += (_, _) => OnEditorSelectionChanged();
        _editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        _editor.AddHandler(InputElement.TextInputEvent, OnEditorTextInput, RoutingStrategies.Tunnel);
        Loaded += OnLoaded;
        DetachedFromVisualTree += (_, _) => {
            _projectionRefreshTicket++;
            _projectionRefreshQueued = false;
            _projectionSnapshot.Invalidate();
            _inlineGenerator.Dispose();
            _autoSave.Dispose();
            _statusTimer?.Stop();
        };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        BeginNew();
    }

    public Func<MarkdownSaveRequest, Task<bool>>? SaveRequestedAsync { get; set; }
    public Func<string, Task>? CancelRequestedAsync { get; set; }
    public event EventHandler? EditRequested { add { } remove { } }
    public event EventHandler? NewRequested;
    public event Action<string>? DraftChanged;
    public event EventHandler? EditingCompleted;

    /// <summary>Compatibility flag. The single-surface editor is always editable.</summary>
    public bool IsEditing => true;
    public bool IsNewMemo { get; private set; }
    public bool ShowNewAction { get; set; }
    public bool HideToolbarInPreview { get => _hideToolbarInPreview; set { _hideToolbarInPreview = value; UpdateToolbarVisibility(); } }
    public bool BlendBordersInPreview { get => _blendBordersInPreview; set { _blendBordersInPreview = value; UpdateBorderBlending(); } }
    public bool UseBorderlessChrome {
        get => GetValue(UseBorderlessChromeProperty);
        set => SetValue(UseBorderlessChromeProperty, value);
    }
    public string Markdown => _model.Markdown;
    public string AssetPathRoot => _imageStore.RootDirectory;
    internal int ImageLoadRequestCount => _inlineGenerator.ImageLoadRequestCount;
    internal int SuccessfulImageLoadCount => _inlineGenerator.SuccessfulImageLoadCount;
    internal int FailedImageLoadCount => _inlineGenerator.FailedImageLoadCount;
    internal Action<Uri>? LinkLauncher { get; set; }

    public void BeginNew(string? draft = null) {
        ResetPendingFormats();
        IsNewMemo = true;
        _editSession.Begin(draft);
        LoadMarkdown(_editSession.Snapshot, clearHistory: true);
        UpdateActions();
        HideStatus();
        Dispatcher.UIThread.Post(FocusEditor, DispatcherPriority.Render);
    }

    public void ShowExistingPreview(string? markdown) => LoadExisting(markdown, focus: false);
    public void BeginExistingEdit(string? markdown) => LoadExisting(markdown, focus: true);

    private void LoadExisting(string? markdown, bool focus) {
        ResetPendingFormats();
        IsNewMemo = false;
        _editSession.Begin(markdown);
        LoadMarkdown(_editSession.Snapshot, clearHistory: true);
        UpdateActions();
        HideStatus();
        if (focus) Dispatcher.UIThread.Post(FocusEditor, DispatcherPriority.Render);
    }

    public void SetExternalMarkdown(string? markdown) {
        var next = markdown ?? string.Empty;
        if (next == Markdown) return;
        ResetPendingFormats();
        _editSession.Begin(next);
        LoadMarkdown(next, clearHistory: true);
    }

    public void FocusEditor() {
        _editor.Focus();
        _editor.CaretOffset = _editor.Text?.Length ?? 0;
    }

    public Task<bool> CompleteEditingAsync() => RequestSaveAsync(completeEditing: true);

    public async Task CancelEditingAsync() {
        _autoSave.Cancel();
        var restore = _editSession.Restore();
        LoadMarkdown(restore, clearHistory: true);
        if (CancelRequestedAsync != null) await CancelRequestedAsync(restore);
        if (IsNewMemo) BeginNew();
        EditingCompleted?.Invoke(this, EventArgs.Empty);
    }

    internal void AbortForSourceDeletion() {
        _autoSave.Cancel();
        ResetPendingFormats();
        LoadMarkdown(string.Empty, clearHistory: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == UseBorderlessChromeProperty) UpdateBorderlessChrome();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) {
        AutomationProperties.SetName(_editor, "所见即所得 Markdown 编辑器");
        AutomationProperties.SetName(_moreButton, "更多格式");
        AutomationProperties.SetName(_saveButton, "立即保存");
        AutomationProperties.SetName(_newButton, "新建备忘录");
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) {
        if (_suppressTextChanged) return;
        _projectionSnapshot.Invalidate();
        var before = CaptureState();
        var change = _pendingVisibleTextChange;
        _pendingVisibleTextChange = null;
        var selection = change is { } exact
            ? _model.ApplyVisibleText(
                _editor.Text, exact.Offset, exact.RemovalLength, exact.InsertionLength)
            : _model.ApplyVisibleText(_editor.Text);
        _projectionSnapshot.Synchronize(_editor.Document, _model);
        if (before.Markdown != Markdown) { _undo.Push(before); _redo.Clear(); }
        QueueProjectionRefresh(selection.Start, selection.End);
        UpdateFormatButtonStates();
        NotifyChanged();
    }

    private void OnEditorDocumentChanging(object? sender, DocumentChangeEventArgs e) {
        _pendingVisibleTextChange = _suppressTextChanged
            ? null
            : (e.Offset, e.RemovalLength, e.InsertionLength);
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e) {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (IsTableInputSource(e.Source)) {
            if (control && e.Key == Key.Enter) { e.Handled = true; await RequestSaveAsync(completeEditing: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; await CancelEditingAsync(); }
            return;
        }
        if (control && (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))) { e.Handled = true; ResetPendingFormats(); Redo(); return; }
        if (control && e.Key == Key.Z) { e.Handled = true; ResetPendingFormats(); Undo(); return; }
        if (control && e.Key == Key.V && HasClipboardImageData()) { e.Handled = true; await InsertClipboardImagesAsync(); return; }
        if (control && e.Key == Key.B) { ApplyFormat(MarkdownFormatCommand.Bold); e.Handled = true; return; }
        if (control && e.Key == Key.I) { ApplyFormat(MarkdownFormatCommand.Italic); e.Handled = true; return; }
        if (control && e.Key == Key.K) { e.Handled = true; await EditLinkAsync(); return; }
        if (control && e.Key == Key.Enter) { e.Handled = true; await RequestSaveAsync(completeEditing: true); return; }
        if (e.Key == Key.Escape) { e.Handled = true; await CancelEditingAsync(); return; }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteObject(e.Key)) { ResetPendingFormats(); e.Handled = true; return; }
        if (!control && e.Key == Key.Back && TryRemoveListMarker()) { ResetPendingFormats(); e.Handled = true; return; }
        if (e.Key == Key.Tab && TryIndentList(e.KeyModifiers.HasFlag(KeyModifiers.Shift))) { e.Handled = true; return; }
        if (e.Key == Key.Enter) ResetPendingFormats();
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            ResetPendingFormats();
        if (IsNewMemo && e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; await RequestSaveAsync(completeEditing: true); }
        else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && TryContinueList()) { e.Handled = true; }
    }

    private void OnEditorTextInput(object? sender, TextInputEventArgs e) {
        if (IsTableInputSource(e.Source)) return;
        if (!_hasPendingFormatOverride || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.Contains('\n') || e.Text.Contains('\r') || _editor.SelectionLength > 0) {
            ResetPendingFormats();
            return;
        }

        e.Handled = true;
        var inherited = ActiveInlineFormatsAtCaret();
        var source = _model.SourceOffsetFromVisible(
            _editor.CaretOffset,
            trailingAffinity: _editor.CaretOffset == 0 || _editor.CaretOffset >= _model.VisibleText.Length);
        var result = new MarkdownEditResult(
            Markdown.Insert(source, e.Text), source, source + e.Text.Length);
        foreach (var command in InlineFormatCommands) {
            if (inherited.Contains(command) == _pendingInlineFormats.Contains(command)) continue;
            result = MarkdownFormatter.Apply(
                result.Text, result.SelectionStart, result.SelectionEnd, command);
        }
        ApplyResult(new MarkdownEditResult(result.Text, result.SelectionEnd, result.SelectionEnd));
        _pendingCaretVisible = _editor.CaretOffset;
        UpdateFormatButtonStates();
    }

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (IsTableInputSource(e.Source)) return;
        if (!_editor.IsKeyboardFocusWithin)
            _editor.Focus();
        var point = e.GetPosition(_editor);
        Dispatcher.UIThread.Post(() => {
            var offset = VisibleOffsetFromPoint(point) ?? _editor.CaretOffset;
            var span = _model.VisualAt(offset, MarkdownVisualKind.Task);
            if (span is { } task) ToggleTask(task);
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) TryOpenLinkAtVisibleOffset(offset);
        }, DispatcherPriority.Input);
    }

    private static bool IsTableInputSource(object? source) =>
        source is Visual visual &&
        visual.GetVisualAncestors().Append(visual).OfType<MarkdownTableControl>().Any();

    private int? VisibleOffsetFromPoint(Point point) {
        var clicked = _editor.GetPositionFromPoint(point);
        return clicked is { } position ? _editor.Document.GetOffset(position.Location) : null;
    }

    private void MoveCaretToVisibleOffset(int offset) {
        var safeOffset = Math.Clamp(offset, 0, _model.VisibleText.Length);
        _editor.Focus();
        _editor.SelectionStart = safeOffset;
        _editor.SelectionLength = 0;
    }

    internal bool TryOpenLinkAtPoint(Point point) =>
        VisibleOffsetFromPoint(point) is { } offset && TryOpenLinkAtVisibleOffset(offset);

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await RequestSaveAsync(completeEditing: true);
    private void OnNewClick(object? sender, RoutedEventArgs e) => NewRequested?.Invoke(this, EventArgs.Empty);
    private void OnFormatClick(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string tag } && Enum.TryParse<MarkdownFormatCommand>(tag, out var command)) ApplyFormat(command); }
    private void OnFormatMenuClick(object? sender, RoutedEventArgs e) { if (sender is MenuItem { Tag: string tag } && Enum.TryParse<MarkdownFormatCommand>(tag, out var command)) ApplyFormat(command); }
    private async void OnLinkClick(object? sender, RoutedEventArgs e) => await EditLinkAsync();

    private void ApplyFormat(MarkdownFormatCommand command) {
        if (IsInlineFormat(command) && _editor.SelectionLength == 0) {
            if (!_hasPendingFormatOverride) {
                _pendingInlineFormats.Clear();
                _pendingInlineFormats.UnionWith(ActiveInlineFormatsAtCaret());
                _hasPendingFormatOverride = true;
            }
            _pendingCaretVisible = _editor.CaretOffset;
            if (!_pendingInlineFormats.Add(command)) _pendingInlineFormats.Remove(command);
            UpdateFormatButtonStates();
            _editor.Focus();
            return;
        }
        ResetPendingFormats();
        var start = _model.SourceOffsetFromVisible(_editor.SelectionStart);
        var end = _model.SourceOffsetFromVisible(_editor.SelectionStart + _editor.SelectionLength, trailingAffinity: false);
        ApplyResult(MarkdownFormatter.Apply(Markdown, start, end, command));
    }

    private void ApplyResult(MarkdownEditResult result) {
        PushUndo();
        _model.SetMarkdown(result.Text);
        RefreshProjection(_model.VisibleOffsetFromSource(result.SelectionStart), _model.VisibleOffsetFromSource(result.SelectionEnd));
        NotifyChanged();
        _editor.Focus();
    }

    private void OnEditorSelectionChanged() {
        if (_updatingSelection) return;
        if (_hasPendingFormatOverride &&
            (_editor.SelectionLength > 0 || _editor.CaretOffset != _pendingCaretVisible))
            ResetPendingFormats();
        UpdateFormatButtonStates();
    }

    private static bool IsInlineFormat(MarkdownFormatCommand command) =>
        command is MarkdownFormatCommand.Bold or MarkdownFormatCommand.Italic or
            MarkdownFormatCommand.Strikethrough or MarkdownFormatCommand.InlineCode;

    private static readonly MarkdownFormatCommand[] InlineFormatCommands = [
        MarkdownFormatCommand.Bold,
        MarkdownFormatCommand.Italic,
        MarkdownFormatCommand.Strikethrough,
        MarkdownFormatCommand.InlineCode,
    ];

    private HashSet<MarkdownFormatCommand> ActiveInlineFormatsAtCaret() {
        var active = new HashSet<MarkdownFormatCommand>();
        if (IsVisualFormatActive(MarkdownVisualKind.Bold)) active.Add(MarkdownFormatCommand.Bold);
        if (IsVisualFormatActive(MarkdownVisualKind.Italic)) active.Add(MarkdownFormatCommand.Italic);
        if (IsVisualFormatActive(MarkdownVisualKind.Strike)) active.Add(MarkdownFormatCommand.Strikethrough);
        if (IsVisualFormatActive(MarkdownVisualKind.Code)) active.Add(MarkdownFormatCommand.InlineCode);
        return active;
    }

    private void ResetPendingFormats() {
        _pendingInlineFormats.Clear();
        _hasPendingFormatOverride = false;
        _pendingCaretVisible = -1;
        UpdateFormatButtonStates();
    }

    private bool IsVisualFormatActive(MarkdownVisualKind kind) {
        var start = _editor.SelectionStart;
        var end = start + _editor.SelectionLength;
        var spans = _model.Spans.Where(span => span.Kind == kind).ToArray();
        if (end == start) {
            if (start == 0) return spans.Any(span => span.Start == 0 && span.End > 0);
            if (start >= _model.VisibleText.Length) return false;
            return spans.Any(span => start > span.Start && start <= span.End);
        }
        return Enumerable.Range(start, end - start)
            .All(offset => spans.Any(span => offset >= span.Start && offset < span.End));
    }

    private void UpdateFormatButtonStates() {
        if (_boldButton == null || _italicButton == null) return;
        var bold = _hasPendingFormatOverride
            ? _pendingInlineFormats.Contains(MarkdownFormatCommand.Bold)
            : IsVisualFormatActive(MarkdownVisualKind.Bold);
        var italic = _hasPendingFormatOverride
            ? _pendingInlineFormats.Contains(MarkdownFormatCommand.Italic)
            : IsVisualFormatActive(MarkdownVisualKind.Italic);
        var strike = _hasPendingFormatOverride
            ? _pendingInlineFormats.Contains(MarkdownFormatCommand.Strikethrough)
            : IsVisualFormatActive(MarkdownVisualKind.Strike);
        var code = _hasPendingFormatOverride
            ? _pendingInlineFormats.Contains(MarkdownFormatCommand.InlineCode)
            : IsVisualFormatActive(MarkdownVisualKind.Code);
        _boldButton.Classes.Set("formatActive", bold);
        _italicButton.Classes.Set("formatActive", italic);
        _strikeMenuItem.IsChecked = strike;
        _codeMenuItem.IsChecked = code;
    }

    private async Task EditLinkAsync() {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var sourceStart = _model.SourceOffsetFromVisible(_editor.SelectionStart);
        var sourceEnd = _model.SourceOffsetFromVisible(_editor.SelectionStart + _editor.SelectionLength);
        var label = sourceEnd > sourceStart ? Markdown[sourceStart..sourceEnd] : "链接文本";
        var url = "https://";
        var replaceStart = sourceStart; var replaceEnd = sourceEnd;
        var linkSpan = _model.VisualAt(_editor.CaretOffset, MarkdownVisualKind.Link);
        if (linkSpan is { } span) {
            var open = Markdown.LastIndexOf('[', span.SourceStart);
            var close = Markdown.IndexOf(')', span.SourceStart + span.SourceLength);
            if (open >= 0 && close > open) {
                var syntax = Markdown[open..(close + 1)];
                var match = Regex.Match(syntax, @"^\[([^]]+)\]\(([^)]+)\)$");
                if (match.Success) { label = match.Groups[1].Value; url = match.Groups[2].Value; replaceStart = open; replaceEnd = close + 1; }
            }
        }
        var value = await new LinkEditDialog(label, url).ShowDialog<LinkEditValue?>(owner);
        if (value == null) return;
        var safeLabel = value.Label.Replace("]", "\\]");
        ReplaceSource(replaceStart, replaceEnd, $"[{safeLabel}]({value.Url})", replaceStart + 1, replaceStart + 1 + safeLabel.Length);
    }

    internal bool TryOpenLinkAtVisibleOffset(int offset) {
        var span = _model.VisualAt(offset, MarkdownVisualKind.Link);
        if (span is not { } link || link.LinkTarget is not { } target) return false;
        var command = new SafeHyperlinkCommand(LinkLauncher);
        if (!command.CanExecute(target)) return false;
        command.Execute(target);
        return true;
    }

    private async void OnEditSourceClick(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var value = await new MarkdownSourceDialog(Markdown).ShowDialog<string?>(owner);
        if (value == null || value == Markdown) return;
        PushUndo(); _model.SetMarkdown(value); RefreshProjection(0, 0); NotifyChanged();
    }

    private bool TryContinueList() {
        var source = _model.SourceOffsetFromVisible(_editor.CaretOffset);
        var lineStart = source == 0 ? 0 : Markdown.LastIndexOf('\n', source - 1) + 1;
        var lineEnd = Markdown.IndexOf('\n', source); if (lineEnd < 0) lineEnd = Markdown.Length;
        var line = Markdown[lineStart..lineEnd];
        var match = ListItemLine().Match(line);
        if (!match.Success) return false;
        if (string.IsNullOrWhiteSpace(match.Groups[4].Value)) { ReplaceSource(lineStart, lineEnd, string.Empty, lineStart, lineStart); return true; }
        var marker = match.Groups[2].Value;
        if (char.IsDigit(marker[0])) marker = Regex.Replace(marker, @"\d+", value => (int.Parse(value.Value) + 1).ToString());
        var prefix = match.Groups[1].Value + marker + " " + (match.Groups[3].Success ? "[ ] " : string.Empty);
        ReplaceSource(source, source, "\n" + prefix, source + prefix.Length + 1, source + prefix.Length + 1);
        return true;
    }

    private bool TryRemoveListMarker() {
        if (_editor.SelectionLength > 0) return false;
        var source = _model.SourceOffsetFromVisible(_editor.CaretOffset);
        var lineStart = source == 0 ? 0 : Markdown.LastIndexOf('\n', source - 1) + 1;
        var lineEnd = Markdown.IndexOf('\n', lineStart); if (lineEnd < 0) lineEnd = Markdown.Length;
        var match = ListItemLine().Match(Markdown[lineStart..lineEnd]);
        if (!match.Success || source != lineStart + match.Groups[4].Index) return false;
        var markerStart = lineStart + match.Groups[2].Index;
        ReplaceSource(markerStart, lineStart + match.Groups[4].Index, string.Empty, markerStart, markerStart);
        return true;
    }

    private bool TryIndentList(bool outdent) {
        var source = _model.SourceOffsetFromVisible(_editor.CaretOffset);
        var lineStart = source == 0 ? 0 : Markdown.LastIndexOf('\n', source - 1) + 1;
        var lineEnd = Markdown.IndexOf('\n', lineStart); if (lineEnd < 0) lineEnd = Markdown.Length;
        if (!Regex.IsMatch(Markdown[lineStart..lineEnd], @"^\s*(?:[-+*]|\d+[.)])\s+")) return false;
        if (outdent) { var remove = Markdown.AsSpan(lineStart).StartsWith("  ") ? 2 : Markdown.AsSpan(lineStart).StartsWith("\t") ? 1 : 0; if (remove == 0) return true; ReplaceSource(lineStart, lineStart + remove, string.Empty, source - remove, source - remove); }
        else ReplaceSource(lineStart, lineStart, "  ", source + 2, source + 2);
        return true;
    }

    private bool TryDeleteObject(Key key) {
        var selectionStart = _editor.SelectionStart;
        var selectionEnd = selectionStart + _editor.SelectionLength;
        var objects = _model.Spans.Where(span => span.Kind is MarkdownVisualKind.Image or MarkdownVisualKind.Rule or MarkdownVisualKind.Table)
            .Where(span => _editor.SelectionLength > 0
                ? span.Start < selectionEnd && span.End > selectionStart
                : key == Key.Delete ? span.Start == _editor.CaretOffset : span.End == _editor.CaretOffset)
            .ToArray();
        if (objects.Length == 0) return false;
        var removeStart = _editor.SelectionLength > 0 ? selectionStart : objects.Min(span => span.Start);
        var removeEnd = _editor.SelectionLength > 0 ? selectionEnd : objects.Max(span => span.End);
        PushUndo();
        var nextVisible = _model.VisibleText.Remove(removeStart, removeEnd - removeStart);
        var selection = _model.ApplyVisibleText(nextVisible);
        RefreshProjection(selection.Start, selection.End);
        NotifyChanged();
        return true;
    }

    private void ToggleTask(MarkdownVisualSpan span) {
        var syntax = Markdown.Substring(span.SourceStart, span.SourceLength);
        var index = syntax.IndexOf('[', StringComparison.Ordinal);
        if (index < 0 || index + 2 >= syntax.Length) return;
        var absolute = span.SourceStart + index + 1;
        ReplaceSource(absolute, absolute + 1, char.ToLowerInvariant(Markdown[absolute]) == 'x' ? " " : "x", absolute, absolute);
    }

    private void ReplaceSource(int start, int end, string replacement, int selectionStart, int selectionEnd) {
        PushUndo();
        var result = Markdown[..start] + replacement + Markdown[end..];
        _model.SetMarkdown(result);
        RefreshProjection(_model.VisibleOffsetFromSource(selectionStart), _model.VisibleOffsetFromSource(selectionEnd));
        NotifyChanged();
    }

    private void UpdateTableSource(int visibleOffset, string replacement, bool isFirstChange) {
        var span = FindTableSpan(visibleOffset);
        if (span is not { } table) return;
        var current = Markdown.Substring(table.SourceStart, table.SourceLength);
        if (current == replacement) return;

        if (isFirstChange) PushUndo();
        var visibleText = _model.VisibleText;
        var selectionStart = _editor.SelectionStart;
        var selectionEnd = selectionStart + _editor.SelectionLength;
        _model.SetMarkdown(Markdown[..table.SourceStart] + replacement + Markdown[(table.SourceStart + table.SourceLength)..]);

        var latest = FindTableSpan(visibleOffset);
        if (_model.VisibleText == visibleText && latest is { } updated &&
            updated.Start == table.Start && updated.Length == table.Length) {
            _projectionRefreshTicket++;
            _projectionRefreshQueued = false;
            _projectionSnapshot.Synchronize(_editor.Document, _model);
            _watermark.IsVisible = _model.VisibleText.Length == 0;
            UpdateFormatButtonStates();
        }
        else {
            RefreshProjection(selectionStart, selectionEnd);
        }
        NotifyChanged();
    }

    private MarkdownVisualSpan? FindTableSpan(int visibleOffset) =>
        _model.Spans
            .Where(span => span.Kind == MarkdownVisualKind.Table && span.SourceLength > 3 &&
                visibleOffset >= span.Start && visibleOffset < span.End)
            .OrderByDescending(span => span.SourceLength)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();

    private void PushUndo() { _undo.Push(CaptureState()); _redo.Clear(); }
    private EditorState CaptureState() => new(Markdown, _model.SourceOffsetFromVisible(_editor.SelectionStart), _model.SourceOffsetFromVisible(_editor.SelectionStart + _editor.SelectionLength, trailingAffinity: false));
    private void Undo() { if (_undo.Count == 0) return; _redo.Push(CaptureState()); RestoreState(_undo.Pop()); }
    private void Redo() { if (_redo.Count == 0) return; _undo.Push(CaptureState()); RestoreState(_redo.Pop()); }
    private void RestoreState(EditorState state) { _model.SetMarkdown(state.Markdown); RefreshProjection(_model.VisibleOffsetFromSource(state.SelectionStart), _model.VisibleOffsetFromSource(state.SelectionEnd)); NotifyChanged(); }

    private void LoadMarkdown(string markdown, bool clearHistory) {
        _model.SetMarkdown(markdown);
        if (clearHistory) { _undo.Clear(); _redo.Clear(); }
        RefreshProjection(0, 0);
    }

    private void RefreshProjection(int selectionStart, int selectionEnd) {
        _projectionRefreshTicket++;
        _projectionRefreshQueued = false;
        ApplyProjection(selectionStart, selectionEnd);
    }

    private void ApplyProjection(int selectionStart, int selectionEnd) {
        var verticalOffset = _editor.VerticalOffset;
        var horizontalOffset = _editor.HorizontalOffset;
        _projectionSnapshot.Invalidate();
        _suppressTextChanged = true;
        _updatingSelection = true;
        try {
            if (_editor.Text != _model.VisibleText) _editor.Text = _model.VisibleText;
            _editor.SelectionStart = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, _model.VisibleText.Length);
            _editor.SelectionLength = Math.Clamp(Math.Abs(selectionEnd - selectionStart), 0, _model.VisibleText.Length - _editor.SelectionStart);
            _watermark.IsVisible = _model.VisibleText.Length == 0;
            _projectionSnapshot.Synchronize(_editor.Document, _model);
            _editor.TextArea.TextView.Redraw();
        }
        finally {
            _updatingSelection = false;
            _suppressTextChanged = false;
        }
        UpdateFormatButtonStates();
        Dispatcher.UIThread.Post(() => {
            _editor.ScrollToVerticalOffset(verticalOffset);
            _editor.ScrollToHorizontalOffset(horizontalOffset);
        }, DispatcherPriority.Render);
    }

    private void QueueProjectionRefresh(int selectionStart, int selectionEnd) {
        _pendingProjectionSelection = (selectionStart, selectionEnd);
        if (_projectionRefreshQueued) return;
        _projectionRefreshQueued = true;
        var ticket = ++_projectionRefreshTicket;
        Dispatcher.UIThread.Post(() => {
            if (ticket != _projectionRefreshTicket) return;
            _projectionRefreshQueued = false;
            var selection = _pendingProjectionSelection;
            ApplyProjection(selection.Start, selection.End);
        }, DispatcherPriority.Input);
    }

    private void NotifyChanged() {
        DraftChanged?.Invoke(Markdown);
        ShowStatus("未保存", false, false);
        if (!IsNewMemo) _autoSave.Schedule(() => RequestSaveAsync(completeEditing: false));
    }

    private async void OnLocalImageClick(object? sender, RoutedEventArgs e) {
        var top = TopLevel.GetTopLevel(this); if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "插入图片", AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp", "*.svg"], MimeTypes = ["image/*"] }] });
        await InsertStorageFilesAsync(files.OfType<IStorageFile>());
    }

    private async void OnRemoteImageClick(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var url = await new ImageUrlDialog().ShowDialog<string?>(owner); if (url == null) return;
        var source = _model.SourceOffsetFromVisible(_editor.CaretOffset); ApplyResult(MarkdownFormatter.InsertImage(Markdown, source, source, "网络图片", url));
    }

    private void OnDragOver(object? sender, DragEventArgs e) { if (e.Data.GetFiles()?.OfType<IStorageFile>().Any(file => MarkdownImageStore.IsSupportedFile(file.Name)) == true) { e.DragEffects = DragDropEffects.Copy; e.Handled = true; } }
    private async void OnDrop(object? sender, DragEventArgs e) { await InsertStorageFilesAsync(e.Data.GetFiles()?.OfType<IStorageFile>() ?? []); e.Handled = true; }
    private async Task InsertStorageFilesAsync(IEnumerable<IStorageFile> files) { foreach (var file in files.Where(file => MarkdownImageStore.IsSupportedFile(file.Name))) try { await using var stream = await file.OpenReadAsync(); var path = await _imageStore.StoreAsync(stream, Path.GetExtension(file.Name)); var source = _model.SourceOffsetFromVisible(_editor.CaretOffset); ApplyResult(MarkdownFormatter.InsertImage(Markdown, source, source, Path.GetFileNameWithoutExtension(file.Name), path)); } catch (Exception ex) { ShowStatus(ex.Message, true, false); } }
    private static bool HasClipboardImageData() { try { return WinFormsClipboard.ContainsImage() || WinFormsClipboard.ContainsFileDropList(); } catch { return false; } }
    private async Task InsertClipboardImagesAsync() {
        try {
            if (WinFormsClipboard.ContainsFileDropList()) { StringCollection paths = WinFormsClipboard.GetFileDropList(); foreach (var path in paths.Cast<string>().Where(MarkdownImageStore.IsSupportedFile)) { var stored = await _imageStore.StoreFileAsync(path); var source = _model.SourceOffsetFromVisible(_editor.CaretOffset); ApplyResult(MarkdownFormatter.InsertImage(Markdown, source, source, Path.GetFileNameWithoutExtension(path), stored)); } return; }
            using var image = WinFormsClipboard.GetImage(); if (image == null) return; await using var stream = new MemoryStream(); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png); stream.Position = 0; var storedPath = await _imageStore.StoreAsync(stream, ".png"); var offset = _model.SourceOffsetFromVisible(_editor.CaretOffset); ApplyResult(MarkdownFormatter.InsertImage(Markdown, offset, offset, "粘贴的图片", storedPath));
        } catch (Exception ex) { ShowStatus(ex.Message, true, false); }
    }

    private async Task<bool> RequestSaveAsync(bool completeEditing) {
        _autoSave.Cancel(); var markdown = Markdown;
        if (IsNewMemo && !MarkdownFormatter.HasMeaningfulContent(markdown)) { ShowStatus("内容不能为空", true, false); return false; }
        if (SaveRequestedAsync == null) return false; ShowStatus("保存中", false, false); var wasNew = IsNewMemo;
        try {
            var saved = await SaveRequestedAsync(new MarkdownSaveRequest(markdown, completeEditing, wasNew)); if (!saved) { ShowStatus("保存失败", true, false); return false; }
            ShowStatus("已保存", false, true);
            if (wasNew) BeginNew(); else EditingCompleted?.Invoke(this, EventArgs.Empty);
            return true;
        } catch (Exception ex) { ShowStatus(ex.Message, true, false); return false; }
    }

    private void UpdateActions() { _saveButton.IsVisible = true; _newButton.IsVisible = !IsNewMemo && ShowNewAction; UpdateToolbarVisibility(); UpdateBorderBlending(); UpdateBorderlessChrome(); }
    private void UpdateToolbarVisibility() => _toolbar.IsVisible = !_hideToolbarInPreview;
    private void UpdateBorderBlending() { _surface.Background = _blendBordersInPreview ? (IBrush)Application.Current!.Resources["BgPrimaryBrush"]! : (IBrush)Application.Current!.Resources["SurfacePrimaryBrush"]!; }
    private void UpdateBorderlessChrome() {
        if (_surface == null || _toolbar == null) return;
        _surface.BorderThickness = UseBorderlessChrome ? new Thickness(0) : new Thickness(1);
        _surface.CornerRadius = UseBorderlessChrome ? new CornerRadius(0) : new CornerRadius(10);
        _toolbar.BorderThickness = UseBorderlessChrome ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        _toolbar.CornerRadius = UseBorderlessChrome ? new CornerRadius(0) : new CornerRadius(9, 9, 0, 0);
        _surface.Classes.Set("borderless", UseBorderlessChrome);
        if (UseBorderlessChrome) _surface.ClearValue(Border.BackgroundProperty);
        else UpdateBorderBlending();
    }
    private void ShowStatus(string message, bool isError, bool autoHide) { _statusTimer?.Stop(); _statusText.Text = message; _statusText.Foreground = (IBrush)Application.Current!.Resources[isError ? "DangerPrimaryBrush" : "TextSecondaryBrush"]!; _statusBadge.Background = (IBrush)Application.Current.Resources[isError ? "DangerSubtleBrush" : "BgTertiaryBrush"]!; _statusBadge.Opacity = 1; if (!autoHide) return; _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) }; _statusTimer.Tick += (_, _) => { _statusTimer?.Stop(); _statusBadge.Opacity = 0; }; _statusTimer.Start(); }
    private void HideStatus() { _statusTimer?.Stop(); _statusBadge.Opacity = 0; }

    [GeneratedRegex(@"^([ \t]*)([-+*]|\d+[.)])[ \t]+(\[[ xX]\][ \t]+)?(.*)$")]
    private static partial Regex ListItemLine();

    private readonly record struct EditorState(string Markdown, int SelectionStart, int SelectionEnd);
}

internal sealed class MarkdownProjectionSnapshot {
    private TextDocument? _document;
    private ITextSourceVersion? _documentVersion;
    private long _modelVersion = -1;

    public void Invalidate() {
        _document = null;
        _documentVersion = null;
        _modelVersion = -1;
    }

    public void Synchronize(TextDocument document, MarkdownDocumentModel model) {
        if (document.TextLength != model.VisibleText.Length ||
            !document.Text.AsSpan().SequenceEqual(model.VisibleText)) {
            Invalidate();
            return;
        }
        _document = document;
        _documentVersion = document.Version;
        _modelVersion = model.ProjectionVersion;
    }

    public bool IsCurrent(TextDocument document, MarkdownDocumentModel model) =>
        ReferenceEquals(document, _document) &&
        ReferenceEquals(document.Version, _documentVersion) &&
        model.ProjectionVersion == _modelVersion;
}

internal sealed class MarkdownColorizer(
    MarkdownDocumentModel model,
    MarkdownProjectionSnapshot projectionSnapshot) : DocumentColorizingTransformer {
    protected override void ColorizeLine(DocumentLine line) {
        var document = CurrentContext.Document;
        if (!projectionSnapshot.IsCurrent(document, model)) return;
        foreach (var span in model.Spans) {
            var start = Math.Clamp(Math.Max(span.Start, line.Offset), line.Offset, line.EndOffset);
            var end = Math.Clamp(Math.Min(span.End, line.EndOffset), start, line.EndOffset);
            if (end <= start) continue;
            ChangeLinePart(start, end, element => {
                switch (span.Kind) {
                    case MarkdownVisualKind.Heading1: element.TextRunProperties.SetFontRenderingEmSize(22); element.TextRunProperties.SetTypeface(new Typeface("Microsoft YaHei UI", FontStyle.Normal, FontWeight.SemiBold)); break;
                    case MarkdownVisualKind.Heading2: element.TextRunProperties.SetFontRenderingEmSize(18); element.TextRunProperties.SetTypeface(new Typeface("Microsoft YaHei UI", FontStyle.Normal, FontWeight.SemiBold)); break;
                    case MarkdownVisualKind.Heading3: element.TextRunProperties.SetFontRenderingEmSize(16); element.TextRunProperties.SetTypeface(new Typeface("Microsoft YaHei UI", FontStyle.Normal, FontWeight.SemiBold)); break;
                    case MarkdownVisualKind.Bold:
                        element.TextRunProperties.SetTypeface(new Typeface("Microsoft YaHei UI",
                            IsCoveredBy(MarkdownVisualKind.Italic, start, end) ? FontStyle.Italic : FontStyle.Normal,
                            FontWeight.Bold));
                        break;
                    case MarkdownVisualKind.Italic:
                        element.TextRunProperties.SetTypeface(new Typeface("Microsoft YaHei UI", FontStyle.Italic,
                            IsCoveredBy(MarkdownVisualKind.Bold, start, end) ? FontWeight.Bold : FontWeight.Normal));
                        break;
                    case MarkdownVisualKind.Code: element.TextRunProperties.SetTypeface(new Typeface("Cascadia Mono, Consolas")); element.TextRunProperties.SetBackgroundBrush(Brushes.Linen); break;
                    case MarkdownVisualKind.Link: element.TextRunProperties.SetForegroundBrush(Brushes.Sienna); element.TextRunProperties.SetTextDecorations(TextDecorations.Underline); break;
                    case MarkdownVisualKind.Quote: element.TextRunProperties.SetForegroundBrush(Brushes.DimGray); element.TextRunProperties.SetBackgroundBrush(Brushes.Linen); break;
                    case MarkdownVisualKind.Strike: element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough); break;
                    case MarkdownVisualKind.Underline: element.TextRunProperties.SetTextDecorations(TextDecorations.Underline); break;
                    case MarkdownVisualKind.Mark: element.TextRunProperties.SetBackgroundBrush(Brushes.PaleGoldenrod); break;
                    case MarkdownVisualKind.Image: element.TextRunProperties.SetForegroundBrush(Brushes.Sienna); element.TextRunProperties.SetBackgroundBrush(Brushes.Linen); break;
                    case MarkdownVisualKind.Rule: element.TextRunProperties.SetForegroundBrush(Brushes.Gray); break;
                    case MarkdownVisualKind.Table: element.TextRunProperties.SetTypeface(new Typeface("Cascadia Mono, Consolas")); break;
                }
            });
        }
    }

    private bool IsCoveredBy(MarkdownVisualKind kind, int start, int end) =>
        model.Spans.Any(span => span.Kind == kind && span.Start <= start && span.End >= end);
}

internal sealed class MarkdownInlineGenerator(
    MarkdownDocumentModel model,
    MarkdownProjectionSnapshot projectionSnapshot,
    string assetRoot,
    TextView textView,
    Action<MarkdownVisualSpan> toggleTask,
    Action<int> moveCaret,
    Action<int, string, bool> updateTable) : VisualLineElementGenerator, IDisposable {
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    // AvaloniaEdit 11.1 fixes every TextLine to the paragraph font height, even for tall inline controls.
    // These members let embedded controls report their real measured height to layout and the scroll height tree.
    private static readonly System.Reflection.MethodInfo VisualLineSetTextLines =
        typeof(VisualLine).GetMethod("SetTextLines", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.MethodInfo VisualLineVisualTopSetter =
        typeof(VisualLine).GetProperty(nameof(VisualLine.VisualTop))!.GetSetMethod(nonPublic: true)!;
    private static readonly System.Reflection.FieldInfo TextViewHeightTreeField =
        typeof(TextView).GetField("_heightTree", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.MethodInfo HeightTreeSetHeight =
        TextViewHeightTreeField.FieldType.GetMethod("SetHeight")!;
    private static readonly System.Reflection.MethodInfo HeightTreeGetVisualPosition =
        TextViewHeightTreeField.FieldType.GetMethod("GetVisualPosition")!;
    private static readonly System.Reflection.FieldInfo VisualLineDrawingVisualField =
        typeof(VisualLine).GetField("_visual", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.FieldInfo DrawingVisualLineHeightField =
        VisualLineDrawingVisualField.FieldType.GetField("<LineHeight>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    private static readonly Thickness ImageMargin = new(2, 6);
    private static readonly Thickness ImagePadding = new(6);
    private static readonly Thickness ImageBorderThickness = new(1);
    private const double TrailingCaretWidth = 24;
    private const double TableTrailingCaretWidth = 1;
    // Keep the caret after a full-width rule on the same visual line instead of wrapping to the left.
    private const double RuleTrailingCaretWidth = 1;
    private readonly Dictionary<string, ImageCacheEntry> _imageCache = new(StringComparer.Ordinal);
    private readonly Dictionary<int, MarkdownTableControl> _tableControls = [];
    private readonly Dictionary<int, TableInlineElement> _tableElements = [];
    private readonly HashSet<Border> _ruleControls = [];
    private readonly HashSet<int> _pendingImageRelayoutOffsets = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _isListeningForResize;
    private bool _isBuildingVisualLines;
    private bool _imageRelayoutQueued;
    private bool _embeddedHeightCorrectionQueued;
    private double _requestedVerticalOffset = double.NaN;
    private double _lastImageLayoutWidth = double.NaN;

    internal int ImageLoadRequestCount { get; private set; }
    internal int SuccessfulImageLoadCount =>
        _imageCache.Values.Count(entry => entry.Result?.Bitmap != null);
    internal int FailedImageLoadCount =>
        _imageCache.Values.Count(entry => entry.Result?.Error != null);

    public override int GetFirstInterestedOffset(int startOffset) {
        if (!ProjectionMatchesDocument()) return -1;
        var document = CurrentContext.Document;
        var safeOffset = Math.Clamp(startOffset, 0, document.TextLength);
        var line = document.GetLineByOffset(safeOffset);
        var candidate = InlineSpans()
            .Where(span => span.Start >= safeOffset && span.End <= line.EndOffset)
            .OrderBy(span => span.Start).FirstOrDefault();
        return candidate.Length > 0 ? candidate.Start : -1;
    }

    public override VisualLineElement? ConstructElement(int offset) {
        if (!ProjectionMatchesDocument()) return null;
        var span = InlineSpans().FirstOrDefault(candidate => candidate.Start == offset);
        if (span.Length <= 0 || span.End > CurrentContext.Document.TextLength) return null;
        if (span.Kind == MarkdownVisualKind.Image) return CreateImage(span);
        if (span.Kind == MarkdownVisualKind.Table) return CreateTable(span);
        Control control = span.Kind switch {
            MarkdownVisualKind.Task => CreateTask(span),
            MarkdownVisualKind.Rule => CreateRule(span),
            _ => new TextBlock { Text = model.VisibleText.Substring(span.Start, span.Length) },
        };
        return new InlineObjectElement(span.Length, control);
    }

    private IEnumerable<MarkdownVisualSpan> InlineSpans() {
        var tables = model.Spans.Where(span => span.Kind == MarkdownVisualKind.Table && span.SourceLength > 3)
            .GroupBy(span => span.SourceStart).Select(group => group.OrderByDescending(span => span.Length).First());
        return model.Spans.Where(span => span.Kind is MarkdownVisualKind.Image or MarkdownVisualKind.Task or MarkdownVisualKind.Rule)
            .Concat(tables);
    }

    private bool ProjectionMatchesDocument() {
        var document = CurrentContext.Document;
        return !_disposed && projectionSnapshot.IsCurrent(document, model);
    }

    private Control CreateTask(MarkdownVisualSpan span) {
        var source = model.Markdown.Substring(span.SourceStart, span.SourceLength);
        var check = new CheckBox {
            IsChecked = source.Contains("[x]", StringComparison.OrdinalIgnoreCase),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            MinWidth = 24, MinHeight = 24, Padding = new Thickness(2), Cursor = new Cursor(StandardCursorType.Hand),
        };
        check.Click += (_, _) => toggleTask(span);
        AutomationProperties.SetName(check, "任务完成状态");
        return check;
    }

    private Control CreateRule(MarkdownVisualSpan span) {
        var rule = new Border {
            Height = 21,
            Background = Brushes.Transparent,
            Child = new Border {
                Height = 1,
                Background = Brushes.Gray,
                IsHitTestVisible = false,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        rule.Classes.Add("MarkdownRule");
        rule.PointerPressed += (_, e) => {
            moveCaret(span.End);
            e.Handled = true;
        };
        ResizeRule(rule);
        rule.AttachedToVisualTree += (_, _) => {
            _ruleControls.Add(rule);
            ResizeRule(rule);
        };
        rule.DetachedFromVisualTree += (_, _) => _ruleControls.Remove(rule);
        EnsureResizeListener();
        return rule;
    }

    private void ResizeRule(Border rule) => rule.Width = Math.Max(
        1,
        textView.Bounds.Width - rule.Margin.Left - rule.Margin.Right - RuleTrailingCaretWidth);

    private TableInlineElement CreateTable(MarkdownVisualSpan span) {
        var source = model.Markdown.Substring(span.SourceStart, span.SourceLength);
        var created = false;
        if (!_tableControls.TryGetValue(span.Start, out var table)) {
            table = new MarkdownTableControl(
                source,
                (markdown, isFirstChange) => updateTable(span.Start, markdown, isFirstChange));
            _tableControls[span.Start] = table;
            created = true;
        }
        else table.UpdateMarkdown(source);
        var element = new TableInlineElement(span.Length, table);
        _tableElements[span.Start] = element;
        if (created)
            table.LayoutUpdated += (_, _) => UpdateTableLineHeight(span.Start, table);
        ResizeTable(table);
        element.SetDesiredLineHeight(table.DesiredSize.Height);
        EnsureResizeListener();
        return element;
    }

    private void ResizeTable(MarkdownTableControl table) {
        var availableOuterWidth = Math.Max(1, textView.Bounds.Width - TableTrailingCaretWidth);
        table.UpdateAvailableWidth(availableOuterWidth);
        table.Measure(new Size(availableOuterWidth, double.PositiveInfinity));
    }

    private void UpdateTableLineHeight(int visibleOffset, MarkdownTableControl table) {
        if (!_tableElements.TryGetValue(visibleOffset, out var element) ||
            Math.Abs(element.DesiredLineHeight - table.DesiredSize.Height) < 0.01) return;
        element.SetDesiredLineHeight(table.DesiredSize.Height);
        QueueEmbeddedHeightCorrection();
    }

    private ImageInlineElement CreateImage(MarkdownVisualSpan span) {
        var alt = string.IsNullOrWhiteSpace(span.AltText) ? "图片" : span.AltText;
        var uri = span.ImageUri ?? string.Empty;
        var host = new Border {
            MinHeight = 54,
            Margin = ImageMargin, Padding = ImagePadding, CornerRadius = new CornerRadius(6),
            BorderThickness = ImageBorderThickness, BorderBrush = Brushes.LightGray, Background = Brushes.Linen,
        };
        var status = new SelectableTextBlock { Text = $"正在加载图片：{alt}", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Sienna };
        host.Child = status;
        var element = new ImageInlineElement(span.Length, host);
        if (!_imageCache.TryGetValue(uri, out var entry)) {
            entry = new ImageCacheEntry();
            _imageCache.Add(uri, entry);
            ImageLoadRequestCount++;
            entry.LoadTask = LoadAndPublishImageAsync(entry, uri, _lifetime.Token);
        }
        var target = new ImageTarget(host, status, element, alt, span.Start);
        entry.Targets.Add(target);
        EnsureResizeListener();
        ResizeTarget(target);
        host.AttachedToVisualTree += (_, _) => { ResizeTarget(target); ApplyCachedResult(entry, target); };
        host.DetachedFromVisualTree += (_, _) => entry.Targets.Remove(target);
        ApplyCachedResult(entry, target);
        return element;
    }

    private double AvailableImageHostWidth(Border host) => Math.Max(
        1,
        textView.Bounds.Width - host.Margin.Left - host.Margin.Right - TrailingCaretWidth);

    internal void EnableEmbeddedControlLayout() => EnsureResizeListener();

    private void EnsureResizeListener() {
        if (_isListeningForResize) return;
        _isListeningForResize = true;
        _lastImageLayoutWidth = textView.Bounds.Width;
        textView.PropertyChanged += OnTextViewPropertyChanged;
        textView.VisualLineConstructionStarting += OnVisualLineConstructionStarting;
        textView.VisualLinesChanged += OnVisualLinesChanged;
        textView.ScrollOffsetChanged += OnScrollOffsetChanged;
    }

    private void OnVisualLineConstructionStarting(
        object? sender,
        VisualLineConstructionStartEventArgs e) {
        _isBuildingVisualLines = true;
    }

    private void OnScrollOffsetChanged(object? sender, EventArgs e) {
        if (!_isBuildingVisualLines)
            _requestedVerticalOffset = textView.VerticalOffset;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) {
        _isBuildingVisualLines = false;
        CorrectEmbeddedControlHeights();
    }

    private void QueueEmbeddedHeightCorrection() {
        if (_disposed || _embeddedHeightCorrectionQueued) return;
        _embeddedHeightCorrectionQueued = true;
        Dispatcher.UIThread.Post(() => {
            _embeddedHeightCorrectionQueued = false;
            CorrectEmbeddedControlHeights();
        }, DispatcherPriority.Render);
    }

    private void CorrectEmbeddedControlHeights() {
        if (_disposed) return;
        var heightTree = TextViewHeightTreeField.GetValue(textView);
        if (heightTree is null) return;

        var changed = false;
        foreach (var visualLine in textView.VisualLines) {
            var embeddedControls = visualLine.Elements.OfType<HeightAwareInlineElement>().ToArray();
            if (embeddedControls.Length == 0) continue;
            var textLines = visualLine.TextLines.ToList();
            var lineChanged = false;
            for (var index = 0; index < textLines.Count; index++) {
                var textLine = textLines[index];
                var desiredHeight = embeddedControls
                    .Where(element => textLine.TextRuns.OfType<InlineObjectRun>()
                        .Any(run => ReferenceEquals(run.Element, element.Host)))
                    .Select(element => element.DesiredLineHeight)
                    .DefaultIfEmpty(0)
                    .Max();
                if (desiredHeight <= 0) continue;

                var innerLine = textLine is EmbeddedControlHeightTextLine adjusted ? adjusted.Inner : textLine;
                var targetHeight = Math.Max(innerLine.Height, desiredHeight);
                if (textLine is EmbeddedControlHeightTextLine current &&
                    Math.Abs(current.Height - targetHeight) < 0.01) continue;
                textLines[index] = new EmbeddedControlHeightTextLine(innerLine, targetHeight);
                lineChanged = true;
            }
            if (!lineChanged) continue;

            VisualLineSetTextLines.Invoke(visualLine, new object[] { textLines });
            HeightTreeSetHeight.Invoke(heightTree,
                new object[] { visualLine.FirstDocumentLine, visualLine.Height });
            UpdateDrawingVisualHeight(visualLine);
            changed = true;
        }
        if (!changed) return;

        foreach (var visualLine in textView.VisualLines) {
            var visualTop = HeightTreeGetVisualPosition.Invoke(
                heightTree, new object[] { visualLine.FirstDocumentLine });
            VisualLineVisualTopSetter.Invoke(visualLine, new[] { visualTop });
        }
        RestoreTextViewOffset(_requestedVerticalOffset);
        QueueCorrectedEmbeddedControlLayout(_requestedVerticalOffset);
    }

    private static void UpdateDrawingVisualHeight(VisualLine visualLine) {
        var drawingVisual = VisualLineDrawingVisualField.GetValue(visualLine);
        if (drawingVisual is not Control control) return;
        DrawingVisualLineHeightField.SetValue(drawingVisual, visualLine.Height);
        control.InvalidateVisual();
    }

    private void RestoreTextViewOffset(double requestedVerticalOffset) {
        if (!double.IsFinite(requestedVerticalOffset) ||
            Math.Abs(textView.VerticalOffset - requestedVerticalOffset) < 0.01) return;
        ((Avalonia.Controls.Primitives.IScrollable)textView).Offset =
            new Vector(textView.HorizontalOffset, requestedVerticalOffset);
    }

    private void QueueCorrectedEmbeddedControlLayout(double requestedVerticalOffset) {
        Dispatcher.UIThread.Post(() => {
            if (_disposed) return;
            textView.InvalidateMeasure();
            Dispatcher.UIThread.Post(() => {
                if (_disposed || !double.IsFinite(requestedVerticalOffset) ||
                    Math.Abs(_requestedVerticalOffset - requestedVerticalOffset) >= 0.01) return;
                var scrollViewer = textView.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (scrollViewer is null) return;
                var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                scrollViewer.Offset = new Vector(
                    scrollViewer.Offset.X,
                    Math.Min(requestedVerticalOffset, maximumOffset));
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    private void OnTextViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (e.Property != Visual.BoundsProperty) return;
        var width = textView.Bounds.Width;
        if (Math.Abs(width - _lastImageLayoutWidth) < 0.01) return;
        _lastImageLayoutWidth = width;
        foreach (var rule in _ruleControls.ToArray())
            ResizeRule(rule);
        foreach (var (offset, table) in _tableControls.ToArray()) {
            ResizeTable(table);
            UpdateTableLineHeight(offset, table);
        }
        var targets = _imageCache.Values.SelectMany(entry => entry.Targets).ToArray();
        foreach (var target in targets)
            ResizeTarget(target);
        RequestImageRelayout(targets);
        if (_tableControls.Count > 0) {
            textView.InvalidateMeasure();
            QueueEmbeddedHeightCorrection();
        }
    }

    private void RequestImageRelayout(IEnumerable<ImageTarget> targets) {
        if (_disposed) return;
        foreach (var target in targets)
            _pendingImageRelayoutOffsets.Add(target.DocumentOffset);
        if (_imageRelayoutQueued || _pendingImageRelayoutOffsets.Count == 0) return;
        _imageRelayoutQueued = true;
        Dispatcher.UIThread.Post(() => {
            _imageRelayoutQueued = false;
            if (_disposed) return;
            var offsets = _pendingImageRelayoutOffsets.ToArray();
            _pendingImageRelayoutOffsets.Clear();
            foreach (var offset in offsets) {
                if (offset >= 0 && offset < textView.Document.TextLength)
                    textView.Redraw(offset, 1);
            }
            textView.InvalidateMeasure();
        }, DispatcherPriority.Render);
    }

    private void ResizeTarget(ImageTarget target) {
        if (!target.TryGetControls(out var host, out _)) return;
        var availableHostWidth = AvailableImageHostWidth(host);
        if (host.Child is not Image image || target.PixelSize.Width <= 0 || target.PixelSize.Height <= 0) {
            host.Width = availableHostWidth;
            host.MinWidth = Math.Min(80, availableHostWidth);
            MeasureTarget(target, host, availableHostWidth);
            return;
        }

        host.MinWidth = 0;
        host.MinHeight = 0;
        var horizontalChrome = host.Padding.Left + host.Padding.Right +
                               host.BorderThickness.Left + host.BorderThickness.Right;
        var availableContentWidth = Math.Max(1, availableHostWidth - horizontalChrome);
        var width = Math.Min(target.PixelSize.Width, availableContentWidth);
        var scale = width / target.PixelSize.Width;
        image.Width = width;
        image.Height = target.PixelSize.Height * scale;
        host.Width = Math.Min(availableHostWidth, width + horizontalChrome);
        MeasureTarget(target, host, availableHostWidth);
    }

    private static void MeasureTarget(
        ImageTarget target,
        Border host,
        double availableHostWidth) {
        var availableOuterWidth = availableHostWidth + host.Margin.Left + host.Margin.Right;
        host.Measure(new Size(availableOuterWidth, double.PositiveInfinity));
        target.Element.SetDesiredLineHeight(host.DesiredSize.Height);
    }

    private async Task LoadAndPublishImageAsync(
        ImageCacheEntry entry,
        string uri,
        CancellationToken cancellationToken) {
        ImageLoadResult result;
        try {
            var bytes = await ReadImageBytesAsync(uri, cancellationToken).ConfigureAwait(false);
            var decoded = await Task.Run(
                () => DecodeBitmap(bytes, cancellationToken), cancellationToken).ConfigureAwait(false);
            result = new ImageLoadResult(decoded.Bitmap, decoded.PixelSize, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return;
        }
        catch (Exception exception) {
            result = new ImageLoadResult(null, default, exception.Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (_disposed) {
                result.Bitmap?.Dispose();
                return;
            }
            entry.Result = result;
            var targets = entry.Targets.ToArray();
            foreach (var target in targets) ApplyCachedResult(entry, target);
            RequestImageRelayout(targets);
        });
    }

    private static DecodedImage DecodeBitmap(byte[] bytes, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasSupportedImageSignature(bytes))
            throw new InvalidDataException("无法识别图片格式。");
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new Bitmap(stream);
        if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) {
            bitmap.Dispose();
            throw new InvalidDataException("无法解码图片。");
        }
        var pixelSize = ReadOriginalPixelSize(bytes, bitmap.PixelSize);
        if (!cancellationToken.IsCancellationRequested) return new DecodedImage(bitmap, pixelSize);
        bitmap.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
    }

    private static PixelSize ReadOriginalPixelSize(byte[] bytes, PixelSize fallback) {
        try {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return image.Width > 0 && image.Height > 0
                ? new PixelSize(image.Width, image.Height)
                : fallback;
        }
        catch {
            return fallback;
        }
    }

    private static bool HasSupportedImageSignature(ReadOnlySpan<byte> bytes) {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var isPng = bytes.Length >= 24 && bytes[..png.Length].SequenceEqual(png) &&
                    bytes.Slice(12, 4).SequenceEqual("IHDR"u8);
        var isJpeg = bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var isGif = bytes.Length >= 10 &&
                    (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));
        var isBitmap = bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M';
        var isWebP = bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8) &&
                     bytes.Slice(8, 4).SequenceEqual("WEBP"u8);
        return isPng || isJpeg || isGif || isBitmap || isWebP;
    }

    private void ApplyCachedResult(ImageCacheEntry entry, ImageTarget target) {
        if (target.Applied || entry.Result is not { } result) return;
        if (!target.TryGetControls(out var host, out var status)) {
            entry.Targets.Remove(target);
            return;
        }
        ApplyImageResult(host, status, target, result);
        ResizeTarget(target);
        target.Applied = true;
    }

    private static void ApplyImageResult(Border host, SelectableTextBlock status, ImageTarget target, ImageLoadResult result) {
        if (result.Bitmap != null) {
            target.PixelSize = result.PixelSize;
            host.Child = new Image { Source = result.Bitmap, Stretch = Stretch.Fill };
            AutomationProperties.SetName(host, target.Alt);
            return;
        }
        status.Text = $"图片加载失败：{target.Alt}";
        status.Foreground = Brushes.Firebrick;
        AutomationProperties.SetName(host, $"图片加载失败：{target.Alt}");
    }

    private async Task<byte[]> ReadImageBytesAsync(string uri, CancellationToken cancellationToken) {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var remote)) {
            if (remote.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("仅支持 HTTPS 图片地址。");
            using var response = await HttpClient.GetAsync(
                remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MarkdownImageStore.MaximumImageBytes)
                throw new InvalidDataException("图片超过 20 MB。");
            await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadBoundedAsync(remoteStream, cancellationToken).ConfigureAwait(false);
        }
        var relative = uri.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(assetRoot, relative));
        var root = Path.GetFullPath(assetRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MarkdownImageStore.MaximumImageBytes) throw new InvalidDataException("图片不存在或超过 20 MB。");
        await using var localStream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return await ReadBoundedAsync(localStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken) {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true) {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MarkdownImageStore.MaximumImageBytes)
                throw new InvalidDataException("图片超过 20 MB。");
            output.Write(buffer, 0, read);
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_isListeningForResize) {
            textView.PropertyChanged -= OnTextViewPropertyChanged;
            textView.VisualLineConstructionStarting -= OnVisualLineConstructionStarting;
            textView.VisualLinesChanged -= OnVisualLinesChanged;
            textView.ScrollOffsetChanged -= OnScrollOffsetChanged;
        }
        foreach (var entry in _imageCache.Values) entry.Result?.Bitmap?.Dispose();
        _imageCache.Clear();
        _ruleControls.Clear();
        _tableElements.Clear();
        _tableControls.Clear();
        _lifetime.Dispose();
    }

    private sealed class ImageCacheEntry {
        public ImageLoadResult? Result { get; set; }
        public Task? LoadTask { get; set; }
        public List<ImageTarget> Targets { get; } = [];
    }
    private sealed class ImageTarget(
        Border host,
        SelectableTextBlock status,
        ImageInlineElement element,
        string alt,
        int documentOffset) {
        private readonly WeakReference<Border> _host = new(host);
        private readonly WeakReference<SelectableTextBlock> _status = new(status);

        public string Alt { get; } = alt;
        public int DocumentOffset { get; } = documentOffset;
        public ImageInlineElement Element { get; } = element;
        public bool Applied { get; set; }
        public PixelSize PixelSize { get; set; }

        public bool TryGetControls(out Border host, out SelectableTextBlock status) {
            var hasHost = _host.TryGetTarget(out host!);
            var hasStatus = _status.TryGetTarget(out status!);
            return hasHost && hasStatus;
        }
    }
    private sealed record DecodedImage(Bitmap Bitmap, PixelSize PixelSize);
    private sealed record ImageLoadResult(Bitmap? Bitmap, PixelSize PixelSize, string? Error);
}

internal abstract class HeightAwareInlineElement : VisualLineElement {
    private const string CaretPlaceholder = "M";
    private readonly Border _host;
    private double _desiredLineHeight = 1;

    protected HeightAwareInlineElement(int documentLength, Border host)
        : base(visualLength: 2, documentLength: documentLength) {
        _host = host;
    }

    public void SetDesiredLineHeight(double height) {
        if (!double.IsFinite(height) || height <= 0) return;
        _desiredLineHeight = height;
    }

    public Border Host => _host;
    public double DesiredLineHeight => _desiredLineHeight;

    public override TextRun CreateTextRun(int visualColumn, ITextRunConstructionContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var runIndex = visualColumn - VisualColumn;
        if (runIndex == 0)
            return new InlineObjectRun(1, TextRunProperties, _host);
        if (runIndex != 1)
            throw new ArgumentOutOfRangeException(nameof(visualColumn));
        var defaultProperties = context.GlobalTextRunProperties;
        var fontSize = defaultProperties.FontRenderingEmSize;
        var heightProperties = new GenericTextRunProperties(
            defaultProperties.Typeface,
            defaultProperties.FontFeatures,
            fontSize,
            textDecorations: null,
            foregroundBrush: Brushes.Transparent,
            backgroundBrush: null,
            baselineAlignment: BaselineAlignment.Baseline,
            cultureInfo: defaultProperties.CultureInfo);
        var glyphTypeface = defaultProperties.Typeface.GlyphTypeface;
        var shapedBuffer = new ShapedBuffer(
            CaretPlaceholder.AsMemory(),
            CaretPlaceholder.Length,
            glyphTypeface,
            fontSize,
            bidiLevel: 0);
        // Keep a trailing caret column without consuming horizontal space beside the image.
        shapedBuffer[0] = new GlyphInfo(
            glyphTypeface.GetGlyph(CaretPlaceholder[0]),
            0,
            0,
            default);
        return new ShapedTextRun(shapedBuffer, heightProperties);
    }
}

internal sealed class ImageInlineElement(int documentLength, Border host)
    : HeightAwareInlineElement(documentLength, host);

internal sealed class TableInlineElement(int documentLength, Border host)
    : HeightAwareInlineElement(documentLength, host);

// Delegating all text behavior preserves AvaloniaEdit hit testing while exposing embedded controls' height.
internal sealed class EmbeddedControlHeightTextLine(TextLine inner, double height) : TextLine {
    internal TextLine Inner { get; } = inner;
    private readonly double _height = Math.Max(inner.Height, height);

    public override IReadOnlyList<TextRun> TextRuns => Inner.TextRuns;
    public override int FirstTextSourceIndex => Inner.FirstTextSourceIndex;
    public override int Length => Inner.Length;
    public override TextLineBreak? TextLineBreak => Inner.TextLineBreak;
    public override double Baseline => _height;
    public override double Extent => Math.Max(Inner.Extent, _height);
    public override bool HasCollapsed => Inner.HasCollapsed;
    public override bool HasOverflowed => Inner.HasOverflowed;
    public override double Height => _height;
    public override int NewLineLength => Inner.NewLineLength;
    public override double OverhangAfter => Inner.OverhangAfter;
    public override double OverhangLeading => Inner.OverhangLeading;
    public override double OverhangTrailing => Inner.OverhangTrailing;
    public override double Start => Inner.Start;
    public override int TrailingWhitespaceLength => Inner.TrailingWhitespaceLength;
    public override double Width => Inner.Width;
    public override double WidthIncludingTrailingWhitespace => Inner.WidthIncludingTrailingWhitespace;

    public override void Draw(DrawingContext drawingContext, Point lineOrigin) =>
        Inner.Draw(drawingContext, new Point(lineOrigin.X, lineOrigin.Y + _height - Inner.Height));

    public override TextLine Collapse(params TextCollapsingProperties?[] collapsingProperties) =>
        new EmbeddedControlHeightTextLine(Inner.Collapse(collapsingProperties), _height);

    public override void Justify(JustificationProperties justificationProperties) =>
        Inner.Justify(justificationProperties);

    public override CharacterHit GetCharacterHitFromDistance(double distance) =>
        Inner.GetCharacterHitFromDistance(distance);

    public override double GetDistanceFromCharacterHit(CharacterHit characterHit) =>
        Inner.GetDistanceFromCharacterHit(characterHit);

    public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit) =>
        Inner.GetNextCaretCharacterHit(characterHit);

    public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit) =>
        Inner.GetPreviousCaretCharacterHit(characterHit);

    public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit) =>
        Inner.GetBackspaceCaretCharacterHit(characterHit);

    public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceIndex, int textLength) =>
        Inner.GetTextBounds(firstTextSourceIndex, textLength);

    public override void Dispose() => Inner.Dispose();
}

internal sealed class MarkdownTableControl : Border {
    private const double EdgeHitSlop = 5;
    private const double EdgeButtonWidth = 24;
    private const double EdgeButtonHeight = 20;
    private const double EdgeButtonHiddenScale = 0.9;
    private const double EdgeMenuHiddenOffset = -4;
    private const double ColumnEdgeButtonViewportOverflow = 20;
    private const double MinimumReadableColumnWidth = 72;
    private static readonly TimeSpan EdgePopupCloseDelay = TimeSpan.FromMilliseconds(500);
    private readonly List<List<TextBox>> _cells = [];
    private readonly Action<string, bool> _changed;
    private readonly Grid _surface = new() { Background = Brushes.Transparent };
    private readonly Grid _grid = new();
    private readonly Button _rowEdgeButton;
    private readonly Button _columnEdgeButton;
    private readonly Popup _rowEdgePopup;
    private readonly Popup _columnEdgePopup;
    private readonly MenuFlyout _rowMenu;
    private readonly MenuFlyout _columnMenu;
    private readonly MenuItem _deleteAboveRowItem;
    private readonly MenuItem _deleteBelowRowItem;
    private readonly MenuItem _deleteLeftColumnItem;
    private readonly MenuItem _deleteRightColumnItem;
    private readonly DispatcherTimer _edgePopupCloseTimer;
    private bool _changeSessionStarted;
    private bool _rowMenuOpen;
    private bool _columnMenuOpen;
    private bool _rowMenuClosing;
    private bool _columnMenuClosing;
    private bool _rowMenuCloseCommitted;
    private bool _columnMenuCloseCommitted;
    private bool _rowEdgePopupShown;
    private bool _columnEdgePopupShown;
    private bool _suppressEdgeAnimations;
    private bool _normalizingCellText;
    private EdgePopupKind _activeEdgePopup;
    private EdgePopupKind _pendingCloseEdgePopup;
    private Rect? _effectiveViewport;
    private TopLevel? _interactionTopLevel;
    private InputElement? _rowMenuInputRoot;
    private InputElement? _columnMenuInputRoot;
    private int _rowBoundary = -1;
    private int _columnBoundary = -1;
    private double _availableOuterWidth = double.NaN;
    private string _lastPublishedMarkdown;
    private string _sourceMarkdown;

    public MarkdownTableControl(string markdown, Action<string, bool> changed) {
        _changed = changed;
        _edgePopupCloseTimer = new DispatcherTimer { Interval = EdgePopupCloseDelay };
        _edgePopupCloseTimer.Tick += OnEdgePopupCloseTimerTick;
        BorderBrush = Brushes.LightGray; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6);
        Background = Brushes.White; Padding = new Thickness(5); Margin = new Thickness(2, 5);
        var rows = Parse(markdown);
        _surface.Children.Add(_grid);

        var insertRowItem = MenuItem("插入行", isDanger: false, () => InsertRow(_rowBoundary));
        _deleteAboveRowItem = MenuItem("删除上行", isDanger: true, () => DeleteRow(_rowBoundary - 1));
        _deleteBelowRowItem = MenuItem("删除下行", isDanger: true, () => DeleteRow(_rowBoundary));
        _rowMenu = EdgeMenu();
        _rowMenu.Items.Add(insertRowItem);
        _rowMenu.Items.Add(_deleteAboveRowItem);
        _rowMenu.Items.Add(_deleteBelowRowItem);
        _rowMenu.Opened += (_, _) => OnEdgeMenuOpened(EdgePopupKind.Row);
        _rowMenu.Closing += (_, e) => OnEdgeMenuClosing(EdgePopupKind.Row, e);
        _rowMenu.Closed += (_, _) => OnEdgeMenuClosed(isRow: true);
        _rowEdgeButton = EdgeButton("RowEdgeMenuButton", "行操作", _rowMenu);
        _rowEdgeButton.PointerEntered += (_, _) => OnEdgeButtonPointerEntered(EdgePopupKind.Row);
        _rowEdgeButton.PointerExited += (_, _) => ScheduleEdgePopupClose(EdgePopupKind.Row);
        _rowEdgePopup = EdgePopup(_rowEdgeButton);

        var insertColumnItem = MenuItem("插入列", isDanger: false, () => InsertColumn(_columnBoundary));
        _deleteLeftColumnItem = MenuItem("删除左列", isDanger: true, () => DeleteColumn(_columnBoundary - 1));
        _deleteRightColumnItem = MenuItem("删除右列", isDanger: true, () => DeleteColumn(_columnBoundary));
        _columnMenu = EdgeMenu();
        _columnMenu.Items.Add(insertColumnItem);
        _columnMenu.Items.Add(_deleteLeftColumnItem);
        _columnMenu.Items.Add(_deleteRightColumnItem);
        _columnMenu.Opened += (_, _) => OnEdgeMenuOpened(EdgePopupKind.Column);
        _columnMenu.Closing += (_, e) => OnEdgeMenuClosing(EdgePopupKind.Column, e);
        _columnMenu.Closed += (_, _) => OnEdgeMenuClosed(isRow: false);
        _columnEdgeButton = EdgeButton("ColumnEdgeMenuButton", "列操作", _columnMenu);
        _columnEdgeButton.PointerEntered += (_, _) => OnEdgeButtonPointerEntered(EdgePopupKind.Column);
        _columnEdgeButton.PointerExited += (_, _) => ScheduleEdgePopupClose(EdgePopupKind.Column);
        _columnEdgePopup = EdgePopup(_columnEdgeButton);

        _surface.AddHandler(InputElement.PointerMovedEvent, OnSurfacePointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _surface.PointerExited += OnSurfacePointerExited;
        DetachedFromVisualTree += (_, _) => {
            DetachInteractionDismissHandlers();
            _suppressEdgeAnimations = true;
            try {
                _rowMenu.Hide(); _columnMenu.Hide();
                DetachMenuInputDismissHandlers(EdgePopupKind.Row);
                DetachMenuInputDismissHandlers(EdgePopupKind.Column);
                HideAllEdgePopups(animate: false);
            }
            finally {
                _suppressEdgeAnimations = false;
            }
        };
        Child = _surface;
        Build(rows);
        _sourceMarkdown = markdown;
        _lastPublishedMarkdown = Serialize();
        AttachedToVisualTree += (_, _) => {
            RecalculateColumnWidths();
            AttachInteractionDismissHandlers();
        };
        EffectiveViewportChanged += (_, e) => {
            _effectiveViewport = e.EffectiveViewport;
            if (_columnEdgePopup.IsOpen) PositionColumnButtonVertically();
        };
    }

    internal Button RowEdgeButton => _rowEdgeButton;
    internal Button ColumnEdgeButton => _columnEdgeButton;
    internal Popup RowEdgePopup => _rowEdgePopup;
    internal Popup ColumnEdgePopup => _columnEdgePopup;

    internal void UpdateAvailableWidth(double availableOuterWidth) {
        if (!double.IsFinite(availableOuterWidth) || availableOuterWidth <= 0) return;
        var width = Math.Max(1, availableOuterWidth - Margin.Left - Margin.Right);
        if (Math.Abs(_availableOuterWidth - availableOuterWidth) < 0.01 &&
            Math.Abs(Width - width) < 0.01) return;
        _availableOuterWidth = availableOuterWidth;
        Width = width;
        RecalculateColumnWidths();
        InvalidateMeasure();
    }

    public void UpdateMarkdown(string markdown) {
        if (_sourceMarkdown == markdown) return;
        _sourceMarkdown = markdown;
        _changeSessionStarted = false;
        Build(Parse(markdown));
        _lastPublishedMarkdown = Serialize();
    }

    private static List<List<string>> Parse(string markdown) {
        var rows = markdown.Split('\n').Where(line => line.Contains('|') && !Regex.IsMatch(line, @"^\s*\|?\s*:?-+"))
            .Select(line => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList()).ToList();
        return rows.Count > 0 ? rows : [new List<string> { "列 1", "列 2" }, new List<string> { "内容", "内容" }];
    }

    private void Build(List<List<string>> rows) {
        if (!_rowMenuOpen) HideEdgePopup(EdgePopupKind.Row);
        if (!_columnMenuOpen) HideEdgePopup(EdgePopupKind.Column);
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear(); _cells.Clear();
        var columns = Math.Max(1, rows.Max(row => row.Count));
        for (var column = 0; column < columns; column++)
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < rows.Count; row++) {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var controls = new List<TextBox>();
            for (var column = 0; column < columns; column++) {
                var cellLines = new Border {
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(
                        0,
                        0,
                        column < columns - 1 ? 1 : 0,
                        row < rows.Count - 1 ? 1 : 0),
                    Background = Brushes.Transparent,
                    IsHitTestVisible = false,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                };
                cellLines.Classes.Add("MarkdownTableCellLines");
                Grid.SetRow(cellLines, row); Grid.SetColumn(cellLines, column);
                _grid.Children.Add(cellLines);

                var box = new TextBox {
                    Text = NormalizeCellText(column < rows[row].Count ? rows[row][column] : string.Empty),
                    MinWidth = 0,
                    Padding = new Thickness(6, 3),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FocusAdorner = null,
                    AcceptsReturn = false,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                };
                box.Classes.Add("MarkdownTableCellInput");
                Grid.SetRow(box, row); Grid.SetColumn(box, column);
                box.TextChanged += OnCellTextChanged;
                box.KeyDown += OnCellKeyDown;
                box.AddHandler(InputElement.GotFocusEvent, OnTableDescendantGotFocus,
                    RoutingStrategies.Bubble, handledEventsToo: true);
                box.LostFocus += OnDescendantLostFocus;
                box.AddHandler(InputElement.PointerPressedEvent, OnCellPointerPressed,
                    RoutingStrategies.Bubble, handledEventsToo: true);
                _grid.Children.Add(box); controls.Add(box);
            }
            _cells.Add(controls);
        }
        RecalculateColumnWidths();
    }

    private void OnCellTextChanged(object? sender, TextChangedEventArgs e) {
        if (_normalizingCellText || sender is not TextBox box) return;
        var value = box.Text ?? string.Empty;
        var normalized = NormalizeCellText(value);
        if (normalized != value) {
            var caret = NormalizedOffset(value, box.CaretIndex);
            var selectionStart = NormalizedOffset(value, box.SelectionStart);
            var selectionEnd = NormalizedOffset(value, box.SelectionEnd);
            _normalizingCellText = true;
            try {
                box.Text = normalized;
                box.CaretIndex = caret;
                box.SelectionStart = selectionStart;
                box.SelectionEnd = selectionEnd;
            }
            finally {
                _normalizingCellText = false;
            }
        }
        RecalculateColumnWidths();
        PublishChange();
    }

    private void OnCellKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Control) || sender is not TextBox box)
            return;
        e.Handled = true;
        MoveToCellAfter(box);
    }

    private void MoveToCellAfter(TextBox current) {
        for (var row = 0; row < _cells.Count; row++) {
            var column = _cells[row].IndexOf(current);
            if (column < 0) continue;
            if (column + 1 < _cells[row].Count) {
                FocusCell(_cells[row][column + 1]);
                return;
            }
            if (row + 1 < _cells.Count) {
                FocusCell(_cells[row + 1][0]);
                return;
            }

            var rows = Values();
            rows.Add(Enumerable.Repeat(string.Empty, _cells[row].Count).ToList());
            Build(rows);
            PublishChange();
            FocusCell(_cells[^1][0]);
            return;
        }
    }

    private static void FocusCell(TextBox box) {
        void ApplyFocus() {
            box.Focus();
            box.CaretIndex = (box.Text ?? string.Empty).Length;
            box.SelectionStart = box.CaretIndex;
            box.SelectionEnd = box.CaretIndex;
        }

        ApplyFocus();
        Dispatcher.UIThread.Post(ApplyFocus, DispatcherPriority.Input);
    }

    private void RecalculateColumnWidths() {
        if (_grid.ColumnDefinitions.Count == 0 || !double.IsFinite(_availableOuterWidth)) return;
        var availableWidth = Math.Max(
            1,
            Width - Padding.Left - Padding.Right - BorderThickness.Left - BorderThickness.Right);
        var columnCount = _grid.ColumnDefinitions.Count;
        if (availableWidth < MinimumReadableColumnWidth * columnCount) {
            SetEqualColumnWidths(availableWidth, columnCount);
            return;
        }

        var demands = new double[columnCount];
        for (var column = 0; column < columnCount; column++)
            demands[column] = Math.Max(1, _cells.Max(row => MeasureSingleLineWidth(row[column])));
        var remainingWidth = availableWidth - MinimumReadableColumnWidth * columnCount;
        var totalDemand = demands.Sum();
        var allocated = 0d;
        for (var column = 0; column < columnCount; column++) {
            var width = column == columnCount - 1
                ? availableWidth - allocated
                : MinimumReadableColumnWidth + remainingWidth * demands[column] / totalDemand;
            _grid.ColumnDefinitions[column].Width = new GridLength(Math.Max(0, width), GridUnitType.Pixel);
            allocated += width;
        }
    }

    private void SetEqualColumnWidths(double availableWidth, int columnCount) {
        var allocated = 0d;
        for (var column = 0; column < columnCount; column++) {
            var width = column == columnCount - 1
                ? availableWidth - allocated
                : availableWidth / columnCount;
            _grid.ColumnDefinitions[column].Width = new GridLength(Math.Max(0, width), GridUnitType.Pixel);
            allocated += width;
        }
    }

    private static double MeasureSingleLineWidth(TextBox box) {
        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        using var layout = new TextLayout(box.Text ?? string.Empty, typeface, box.FontSize, box.Foreground);
        return layout.Width + box.Padding.Left + box.Padding.Right +
               box.BorderThickness.Left + box.BorderThickness.Right;
    }

    private static string NormalizeCellText(string value) =>
        Regex.Replace(value, "\r\n|\r|\n", " ");

    private static int NormalizedOffset(string value, int offset) =>
        NormalizeCellText(value[..Math.Clamp(offset, 0, value.Length)]).Length;

    private Button EdgeButton(string name, string automationName, MenuFlyout flyout) {
        var iconDefault = ThemeBrush("IconDefaultBrush", Brushes.Gray);
        var iconHover = ThemeBrush("IconHoverBrush", Brushes.Black);
        var accent = ThemeBrush("AccentPrimaryBrush", Brushes.DarkOrange);
        var defaultBackground = ThemeBrush("BgHoverBrush", Brushes.LightGray);
        var hoverBackground = ThemeBrush("BorderDefaultBrush", Brushes.Gray);
        var activeBackground = ThemeBrush("AccentMutedBrush", Brushes.SandyBrown);
        var edgeButtonTheme = Application.Current?.Resources["MarkdownTableEdgeButtonTheme"] as ControlTheme;
        var ellipsis = new TextBlock {
            Text = "...",
            Width = 16,
            Height = 16,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = iconDefault,
            LineHeight = 16,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            RenderTransform = new TranslateTransform { Y = -4 }
        };
        var button = new Button {
            Name = name,
            Content = ellipsis,
            Width = EdgeButtonWidth,
            Height = EdgeButtonHeight,
            MinWidth = EdgeButtonWidth,
            MinHeight = EdgeButtonHeight,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = defaultBackground,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = iconDefault,
            FocusAdorner = null,
            Theme = edgeButtonTheme,
            Opacity = 0,
            RenderTransform = new ScaleTransform(EdgeButtonHiddenScale, EdgeButtonHiddenScale),
            RenderTransformOrigin = RelativePoint.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Flyout = flyout
        };
        button.Classes.Add("MarkdownTableEdgeButton");

        void ApplyDefault() {
            button.Background = defaultBackground;
            button.Foreground = iconDefault;
            ellipsis.Foreground = iconDefault;
        }
        void ApplyHover() {
            button.Background = hoverBackground;
            button.Foreground = iconHover;
            ellipsis.Foreground = iconHover;
        }
        void ApplyPressed() {
            button.Background = activeBackground;
            button.Foreground = accent;
            ellipsis.Foreground = accent;
        }
        void ApplyFocus() {
            button.Background = activeBackground;
            button.Foreground = accent;
            ellipsis.Foreground = accent;
        }
        void ApplyRestingState() {
            if (button.IsKeyboardFocusWithin) ApplyFocus();
            else if (button.IsPointerOver) ApplyHover();
            else ApplyDefault();
        }

        button.PointerEntered += (_, _) => ApplyHover();
        button.PointerPressed += (_, _) => ApplyPressed();
        button.PointerReleased += (_, _) => ApplyRestingState();
        button.PointerExited += (_, _) => ApplyRestingState();
        button.GotFocus += (_, _) => ApplyFocus();
        button.LostFocus += (_, _) => ApplyRestingState();
        AutomationProperties.SetName(button, automationName);
        ToolTip.SetTip(button, automationName);
        button.AddHandler(InputElement.GotFocusEvent, OnTableDescendantGotFocus,
            RoutingStrategies.Bubble, handledEventsToo: true);
        button.LostFocus += OnDescendantLostFocus;
        return button;
    }

    private static IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.Resources[key] as IBrush ?? fallback;

    private Popup EdgePopup(Button button) {
        var popup = new Popup {
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementTarget = this,
            IsLightDismissEnabled = false,
            WindowManagerAddShadowHint = false,
            Child = button
        };
        popup.Opened += (_, _) => ConfigureTransparentPopupRoot(button);
        return popup;
    }

    private static void ConfigureTransparentPopupRoot(Control content) {
        if (TopLevel.GetTopLevel(content) is not PopupRoot popupRoot) return;
        popupRoot.Background = Brushes.Transparent;
        popupRoot.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        popupRoot.TransparencyBackgroundFallback = Brushes.Transparent;
        popupRoot.WindowManagerAddShadowHint = false;
    }

    private static MenuFlyout EdgeMenu() => new() {
        FlyoutPresenterTheme = ThemeResource<ControlTheme>("MarkdownTableEdgeMenuPresenterTheme")
    };

    private MenuItem MenuItem(string header, bool isDanger, Action action) {
        var item = new MenuItem {
            Header = header,
            Theme = ThemeResource<ControlTheme>("MarkdownTableEdgeMenuItemTheme")
        };
        item.Classes.Add(isDanger ? "Danger" : "Accent");
        item.Click += (_, _) => {
            action();
            DismissEdgeMenusAndPopups();
        };
        return item;
    }

    private static T? ThemeResource<T>(string key) where T : class =>
        Application.Current?.Resources[key] as T;

    private void AttachInteractionDismissHandlers() {
        var topLevel = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(_interactionTopLevel, topLevel)) return;
        DetachInteractionDismissHandlers();
        _interactionTopLevel = topLevel;
        if (topLevel == null) return;
        topLevel.AddHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(InputElement.TextInputEvent, OnTopLevelTextInput,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DetachInteractionDismissHandlers() {
        if (_interactionTopLevel == null) return;
        _interactionTopLevel.RemoveHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed);
        _interactionTopLevel.RemoveHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown);
        _interactionTopLevel.RemoveHandler(InputElement.TextInputEvent, OnTopLevelTextInput);
        _interactionTopLevel = null;
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (!HasOpenEdgeMenu() || IsScrollInteraction(e.Source)) return;
        QueueEdgeMenuDismissal();
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e) {
        if (!HasOpenEdgeMenu() || IsMenuNavigationKey(e.Key)) return;
        QueueEdgeMenuDismissal();
    }

    private void OnTopLevelTextInput(object? sender, TextInputEventArgs e) {
        if (!HasOpenEdgeMenu()) return;
        QueueEdgeMenuDismissal();
    }

    private static bool IsScrollInteraction(object? source) =>
        source is Visual visual &&
        visual.GetVisualAncestors().Append(visual).Any(ancestor => ancestor is ScrollBar);

    private static bool IsMenuNavigationKey(Key key) => key is
        Key.Up or Key.Down or Key.Left or Key.Right or
        Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private bool HasOpenEdgeMenu() =>
        _rowMenuOpen || _columnMenuOpen || _rowMenu.IsOpen || _columnMenu.IsOpen;

    private void QueueEdgeMenuDismissal() =>
        Dispatcher.UIThread.Post(DismissEdgeMenusAndPopups, DispatcherPriority.Input);

    private void DismissEdgeMenusAndPopups() {
        _rowMenu.Hide();
        _columnMenu.Hide();
        HideAllEdgePopups();
        ResetEdgeButtonAfterDismissal(_rowEdgeButton);
        ResetEdgeButtonAfterDismissal(_columnEdgeButton);
    }

    private void OnSurfacePointerMoved(object? sender, PointerEventArgs e) {
        if (_grid.Bounds.Width <= 0 || _grid.Bounds.Height <= 0) return;
        if (_rowMenuOpen || _columnMenuOpen) return;
        var point = e.GetPosition(_grid);
        var rowBoundary = point.X >= 0 && point.X <= _grid.Bounds.Width
            ? NearestRowBoundary(point.Y)
            : -1;
        var columnBoundary = point.Y >= 0 && point.Y <= _grid.Bounds.Height
            ? NearestColumnBoundary(point.X)
            : -1;

        var triggeredEdge = ResolveTriggeredEdge(rowBoundary, columnBoundary);
        switch (triggeredEdge) {
            case EdgePopupKind.Row:
                PositionRowButton(rowBoundary);
                ActivateEdgePopup(EdgePopupKind.Row);
                break;
            case EdgePopupKind.Column:
                PositionColumnButton(columnBoundary);
                ActivateEdgePopup(EdgePopupKind.Column);
                break;
            default:
                ScheduleEdgePopupClose(_activeEdgePopup);
                break;
        }
    }

    private EdgePopupKind ResolveTriggeredEdge(int rowBoundary, int columnBoundary) {
        if (rowBoundary < 0) return columnBoundary >= 0 ? EdgePopupKind.Column : EdgePopupKind.None;
        if (columnBoundary < 0) return EdgePopupKind.Row;
        return _activeEdgePopup is EdgePopupKind.Row or EdgePopupKind.Column
            ? _activeEdgePopup
            : EdgePopupKind.Row;
    }

    private void OnSurfacePointerExited(object? sender, PointerEventArgs e) =>
        ScheduleEdgePopupClose(_activeEdgePopup);

    private void ActivateEdgePopup(EdgePopupKind edge) {
        if (edge == EdgePopupKind.None || edge == _activeEdgePopup && IsEdgePopupOpen(edge)) {
            CancelEdgePopupClose();
            return;
        }

        CancelEdgePopupClose();
        HideEdgePopup(edge == EdgePopupKind.Row ? EdgePopupKind.Column : EdgePopupKind.Row);
        _activeEdgePopup = edge;
        SetEdgePopupShown(edge, shown: true);
    }

    private void OnEdgeButtonPointerEntered(EdgePopupKind edge) {
        if (_activeEdgePopup == edge) CancelEdgePopupClose();
    }

    private void ScheduleEdgePopupClose(EdgePopupKind edge) {
        if (edge == EdgePopupKind.None || edge != _activeEdgePopup || IsEdgeMenuOpen(edge)) return;
        if (EdgeButton(edge).IsPointerOver) {
            CancelEdgePopupClose();
            return;
        }
        if (_edgePopupCloseTimer.IsEnabled && _pendingCloseEdgePopup == edge) return;

        _edgePopupCloseTimer.Stop();
        _pendingCloseEdgePopup = edge;
        _edgePopupCloseTimer.Start();
    }

    private void CancelEdgePopupClose() {
        _edgePopupCloseTimer.Stop();
        _pendingCloseEdgePopup = EdgePopupKind.None;
    }

    private void OnEdgePopupCloseTimerTick(object? sender, EventArgs e) {
        var edge = _pendingCloseEdgePopup;
        CancelEdgePopupClose();
        if (edge != _activeEdgePopup || IsEdgeMenuOpen(edge) || EdgeButton(edge).IsPointerOver) return;
        HideEdgePopup(edge);
    }

    private void HideEdgePopup(EdgePopupKind edge, bool animate = true) {
        if (edge == EdgePopupKind.None) return;
        SetEdgePopupShown(edge, shown: false, animate);
        if (_pendingCloseEdgePopup == edge) CancelEdgePopupClose();
        if (_activeEdgePopup == edge) _activeEdgePopup = EdgePopupKind.None;
    }

    private void HideAllEdgePopups(bool animate = true) {
        CancelEdgePopupClose();
        SetEdgePopupShown(EdgePopupKind.Row, shown: false, animate);
        SetEdgePopupShown(EdgePopupKind.Column, shown: false, animate);
        _activeEdgePopup = EdgePopupKind.None;
    }

    private Popup EdgePopup(EdgePopupKind edge) =>
        edge == EdgePopupKind.Row ? _rowEdgePopup : _columnEdgePopup;

    private Button EdgeButton(EdgePopupKind edge) =>
        edge == EdgePopupKind.Row ? _rowEdgeButton : _columnEdgeButton;

    private bool IsEdgePopupOpen(EdgePopupKind edge) => EdgePopup(edge).IsOpen;

    private bool IsEdgeMenuOpen(EdgePopupKind edge) => edge switch {
        EdgePopupKind.Row => _rowMenuOpen,
        EdgePopupKind.Column => _columnMenuOpen,
        _ => false
    };

    private bool IsEdgePopupShown(EdgePopupKind edge) => edge switch {
        EdgePopupKind.Row => _rowEdgePopupShown,
        EdgePopupKind.Column => _columnEdgePopupShown,
        _ => false
    };

    private void SetEdgePopupShownState(EdgePopupKind edge, bool shown) {
        if (edge == EdgePopupKind.Row) _rowEdgePopupShown = shown;
        else if (edge == EdgePopupKind.Column) _columnEdgePopupShown = shown;
    }

    private void SetEdgePopupShown(EdgePopupKind edge, bool shown, bool animate = true) {
        var popup = EdgePopup(edge);
        var button = EdgeButton(edge);
        var scale = EnsureEdgeButtonScale(button);

        if (shown) {
            if (IsEdgePopupShown(edge) && popup.IsOpen && button.IsHitTestVisible) return;
            SetEdgePopupShownState(edge, shown: true);
            var wasOpen = popup.IsOpen;
            if (!wasOpen) {
                button.Opacity = 0;
                scale.ScaleX = EdgeButtonHiddenScale;
                scale.ScaleY = EdgeButtonHiddenScale;
                popup.IsOpen = true;
            }
            button.IsHitTestVisible = true;

            var topLevel = TopLevel.GetTopLevel(this);
            if (!animate || _suppressEdgeAnimations || topLevel == null) {
                MotionAnimations.Cancel(button);
                button.Opacity = 1;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                return;
            }

            var fromOpacity = button.Opacity;
            var fromScale = scale.ScaleX;
            MotionAnimations.Start(button, topLevel, MotionPreferences.FastDuration, new CubicEaseOut(),
                progress => {
                    button.Opacity = MotionAnimations.Lerp(fromOpacity, 1, progress);
                    var value = MotionAnimations.Lerp(fromScale, 1, progress);
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                });
            return;
        }

        if (!animate || _suppressEdgeAnimations) {
            SetEdgePopupShownState(edge, shown: false);
            MotionAnimations.Cancel(button);
            button.IsHitTestVisible = false;
            button.Opacity = 0;
            scale.ScaleX = EdgeButtonHiddenScale;
            scale.ScaleY = EdgeButtonHiddenScale;
            popup.IsOpen = false;
            return;
        }
        if (!IsEdgePopupShown(edge)) return;

        SetEdgePopupShownState(edge, shown: false);
        button.IsHitTestVisible = false;
        var animationTopLevel = TopLevel.GetTopLevel(this);
        if (animationTopLevel == null) {
            MotionAnimations.Cancel(button);
            button.Opacity = 0;
            scale.ScaleX = EdgeButtonHiddenScale;
            scale.ScaleY = EdgeButtonHiddenScale;
            popup.IsOpen = false;
            return;
        }

        var exitOpacity = button.Opacity;
        var exitScale = scale.ScaleX;
        MotionAnimations.Start(button, animationTopLevel, MotionPreferences.FastDuration, new CubicEaseIn(),
            progress => {
                button.Opacity = MotionAnimations.Lerp(exitOpacity, 0, progress);
                var value = MotionAnimations.Lerp(exitScale, EdgeButtonHiddenScale, progress);
                scale.ScaleX = value;
                scale.ScaleY = value;
            },
            () => {
                if (IsEdgePopupShown(edge)) return;
                popup.IsOpen = false;
            });
    }

    private static ScaleTransform EnsureEdgeButtonScale(Button button) {
        if (button.RenderTransform is ScaleTransform scale) return scale;
        scale = new ScaleTransform(EdgeButtonHiddenScale, EdgeButtonHiddenScale);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = RelativePoint.Center;
        return scale;
    }

    private int NearestRowBoundary(double y) {
        var nearest = -1;
        var nearestDistance = EdgeHitSlop;
        for (var boundary = 0; boundary <= _cells.Count; boundary++) {
            var edge = boundary == 0 ? 0 : _cells[boundary - 1][0].Bounds.Bottom;
            var distance = Math.Abs(y - edge);
            if (distance > nearestDistance) continue;
            nearest = boundary;
            nearestDistance = distance;
        }
        return nearest;
    }

    private int NearestColumnBoundary(double x) {
        var nearest = -1;
        var nearestDistance = EdgeHitSlop;
        for (var boundary = 0; boundary <= _cells[0].Count; boundary++) {
            var edge = boundary == 0 ? 0 : _cells[0][boundary - 1].Bounds.Right;
            var distance = Math.Abs(x - edge);
            if (distance > nearestDistance) continue;
            nearest = boundary;
            nearestDistance = distance;
        }
        return nearest;
    }

    private void PositionRowButton(int boundary) {
        _rowBoundary = boundary;
        var edge = boundary == 0 ? 0 : _cells[boundary - 1][0].Bounds.Bottom;
        var edgeInTable = _grid.TranslatePoint(new Point(_grid.Bounds.Width, edge), this);
        _rowEdgePopup.HorizontalOffset = Bounds.Width;
        _rowEdgePopup.VerticalOffset = (edgeInTable?.Y ?? edge) - EdgeButtonHeight / 2;
        _deleteAboveRowItem.IsEnabled = _cells.Count > 1 && boundary > 0;
        _deleteBelowRowItem.IsEnabled = _cells.Count > 1 && boundary < _cells.Count;
    }

    private void PositionColumnButton(int boundary) {
        _columnBoundary = boundary;
        var edge = boundary == 0 ? 0 : _cells[0][boundary - 1].Bounds.Right;
        var edgeInTable = _grid.TranslatePoint(new Point(edge, _grid.Bounds.Height), this);
        _columnEdgePopup.HorizontalOffset = (edgeInTable?.X ?? edge) - EdgeButtonWidth / 2;
        PositionColumnButtonVertically();
        _deleteLeftColumnItem.IsEnabled = _cells[0].Count > 1 && boundary > 0;
        _deleteRightColumnItem.IsEnabled = _cells[0].Count > 1 && boundary < _cells[0].Count;
    }

    private void PositionColumnButtonVertically() {
        var desiredOffset = Bounds.Height;
        if (_effectiveViewport is not { } viewport || viewport.Height <= 0) {
            _columnEdgePopup.VerticalOffset = desiredOffset;
            return;
        }

        var maximumOffset = Math.Max(
            viewport.Top,
            viewport.Bottom - EdgeButtonHeight + ColumnEdgeButtonViewportOverflow);
        _columnEdgePopup.VerticalOffset = Math.Clamp(desiredOffset, viewport.Top, maximumOffset);
    }

    private void OnEdgeMenuOpened(EdgePopupKind edge) {
        if (edge == EdgePopupKind.Row) _rowMenuOpen = true;
        else _columnMenuOpen = true;
        SetEdgeMenuClosing(edge, closing: false);
        SetEdgeMenuCloseCommitted(edge, committed: false);
        ConfigureTransparentPopupRoot(
            edge == EdgePopupKind.Row ? _deleteAboveRowItem : _deleteLeftColumnItem);
        AttachMenuInputDismissHandlers(edge);
        CancelEdgePopupClose();
        AnimateEdgeMenuIn(edge);
    }

    private void OnEdgeMenuClosing(EdgePopupKind edge, CancelEventArgs e) {
        HideEdgePopup(edge);
        if (IsEdgeMenuCloseCommitted(edge)) {
            SetEdgeMenuCloseCommitted(edge, committed: false);
            return;
        }

        var presenter = EdgeMenuPresenter(edge);
        if (_suppressEdgeAnimations) {
            if (presenter != null) MotionAnimations.Cancel(presenter);
            SetEdgeMenuClosing(edge, closing: false);
            return;
        }
        if (IsEdgeMenuClosing(edge)) {
            e.Cancel = true;
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (!MotionPreferences.AnimationsEnabled || presenter == null || topLevel == null) return;

        e.Cancel = true;
        SetEdgeMenuClosing(edge, closing: true);
        presenter.IsHitTestVisible = false;
        var transform = EnsureEdgeMenuTransform(presenter);
        var fromOpacity = presenter.Opacity;
        var fromOffset = transform.Y;
        MotionAnimations.Start(presenter, topLevel, MotionPreferences.FastDuration, new CubicEaseIn(),
            progress => {
                presenter.Opacity = MotionAnimations.Lerp(fromOpacity, 0, progress);
                transform.Y = MotionAnimations.Lerp(fromOffset, EdgeMenuHiddenOffset, progress);
            },
            () => {
                if (!IsEdgeMenuClosing(edge)) return;
                SetEdgeMenuCloseCommitted(edge, committed: true);
                EdgeMenu(edge).Hide();
            });
    }

    private void OnEdgeMenuClosed(bool isRow) {
        var edge = isRow ? EdgePopupKind.Row : EdgePopupKind.Column;
        if (isRow) _rowMenuOpen = false;
        else _columnMenuOpen = false;
        SetEdgeMenuClosing(edge, closing: false);
        SetEdgeMenuCloseCommitted(edge, committed: false);
        DetachMenuInputDismissHandlers(edge);
        HideEdgePopup(edge);
        ResetEdgeButtonAfterDismissal(EdgeButton(edge));
        if (!HasTableInteractionFocus() && !_rowMenuOpen && !_columnMenuOpen) _changeSessionStarted = false;
    }

    private void AnimateEdgeMenuIn(EdgePopupKind edge) {
        var presenter = EdgeMenuPresenter(edge);
        if (presenter == null) return;
        var transform = EnsureEdgeMenuTransform(presenter);
        presenter.IsHitTestVisible = true;
        presenter.Opacity = 0;
        transform.Y = EdgeMenuHiddenOffset;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) {
            presenter.Opacity = 1;
            transform.Y = 0;
            return;
        }

        MotionAnimations.Start(presenter, topLevel, MotionPreferences.FastDuration, new CubicEaseOut(),
            progress => {
                presenter.Opacity = progress;
                transform.Y = MotionAnimations.Lerp(EdgeMenuHiddenOffset, 0, progress);
            });
    }

    private MenuFlyout EdgeMenu(EdgePopupKind edge) =>
        edge == EdgePopupKind.Row ? _rowMenu : _columnMenu;

    private Control? EdgeMenuPresenter(EdgePopupKind edge) =>
        (edge == EdgePopupKind.Row ? _deleteAboveRowItem : _deleteLeftColumnItem).Parent as Control;

    private bool IsEdgeMenuClosing(EdgePopupKind edge) =>
        edge == EdgePopupKind.Row ? _rowMenuClosing : _columnMenuClosing;

    private void SetEdgeMenuClosing(EdgePopupKind edge, bool closing) {
        if (edge == EdgePopupKind.Row) _rowMenuClosing = closing;
        else if (edge == EdgePopupKind.Column) _columnMenuClosing = closing;
    }

    private bool IsEdgeMenuCloseCommitted(EdgePopupKind edge) =>
        edge == EdgePopupKind.Row ? _rowMenuCloseCommitted : _columnMenuCloseCommitted;

    private void SetEdgeMenuCloseCommitted(EdgePopupKind edge, bool committed) {
        if (edge == EdgePopupKind.Row) _rowMenuCloseCommitted = committed;
        else if (edge == EdgePopupKind.Column) _columnMenuCloseCommitted = committed;
    }

    private static TranslateTransform EnsureEdgeMenuTransform(Control presenter) {
        if (presenter.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform { Y = EdgeMenuHiddenOffset };
        presenter.RenderTransform = transform;
        return transform;
    }

    private void ResetEdgeButtonAfterDismissal(Button button) {
        ResetEdgeButtonVisualState(button);
        Dispatcher.UIThread.Post(() => {
            var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
            if (ReferenceEquals(focusManager?.GetFocusedElement(), button)) focusManager.ClearFocus();
            ResetEdgeButtonVisualState(button);
        }, DispatcherPriority.Background);
    }

    private void AttachMenuInputDismissHandlers(EdgePopupKind edge) {
        var root = (edge == EdgePopupKind.Row ? _deleteAboveRowItem : _deleteLeftColumnItem).Parent
            as InputElement;
        if (edge == EdgePopupKind.Row) {
            if (ReferenceEquals(_rowMenuInputRoot, root)) return;
            DetachMenuInputDismissHandlers(edge);
            _rowMenuInputRoot = root;
        } else {
            if (ReferenceEquals(_columnMenuInputRoot, root)) return;
            DetachMenuInputDismissHandlers(edge);
            _columnMenuInputRoot = root;
        }
        if (root == null) return;
        root.AddHandler(InputElement.KeyDownEvent, OnMenuKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.TextInputEvent, OnMenuTextInput,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DetachMenuInputDismissHandlers(EdgePopupKind edge) {
        var root = edge == EdgePopupKind.Row ? _rowMenuInputRoot : _columnMenuInputRoot;
        if (root != null) {
            root.RemoveHandler(InputElement.KeyDownEvent, OnMenuKeyDown);
            root.RemoveHandler(InputElement.TextInputEvent, OnMenuTextInput);
        }
        if (edge == EdgePopupKind.Row) _rowMenuInputRoot = null;
        else _columnMenuInputRoot = null;
    }

    private void OnMenuKeyDown(object? sender, KeyEventArgs e) {
        if (!IsMenuNavigationKey(e.Key)) QueueEdgeMenuDismissal();
    }

    private void OnMenuTextInput(object? sender, TextInputEventArgs e) => QueueEdgeMenuDismissal();

    private static void ResetEdgeButtonVisualState(Button button) {
        var foreground = ThemeBrush("IconDefaultBrush", Brushes.Gray);
        button.Background = ThemeBrush("BgHoverBrush", Brushes.LightGray);
        button.Foreground = foreground;
        if (button.Content is TextBlock ellipsis) ellipsis.Foreground = foreground;
    }

    private void OnDescendantLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => {
            if (!HasTableInteractionFocus() && !_rowMenuOpen && !_columnMenuOpen) _changeSessionStarted = false;
        }, DispatcherPriority.Input);

    private bool HasTableInteractionFocus() => IsKeyboardFocusWithin ||
        _rowEdgeButton.IsKeyboardFocusWithin || _columnEdgeButton.IsKeyboardFocusWithin;

    private static void OnCellPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (sender is TextBox box)
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
        e.Handled = true;
    }

    private static void OnTableDescendantGotFocus(object? sender, GotFocusEventArgs e) => e.Handled = true;

    private void PublishChange() {
        var markdown = Serialize();
        if (markdown == _lastPublishedMarkdown) return;
        _sourceMarkdown = markdown;
        _lastPublishedMarkdown = markdown;
        var isFirstChange = !_changeSessionStarted;
        _changeSessionStarted = true;
        _changed(markdown, isFirstChange);
    }

    private void InsertRow(int boundary) { var rows = Values(); rows.Insert(Math.Clamp(boundary, 0, rows.Count), Enumerable.Repeat(string.Empty, rows[0].Count).ToList()); Build(rows); PublishChange(); }
    private void DeleteRow(int row) { var rows = Values(); if (rows.Count > 1 && row >= 0 && row < rows.Count) rows.RemoveAt(row); Build(rows); PublishChange(); }
    private void InsertColumn(int boundary) { var rows = Values(); var column = Math.Clamp(boundary, 0, rows[0].Count); foreach (var row in rows) row.Insert(column, string.Empty); Build(rows); PublishChange(); }
    private void DeleteColumn(int column) { var rows = Values(); if (rows[0].Count > 1 && column >= 0 && column < rows[0].Count) foreach (var row in rows) row.RemoveAt(column); Build(rows); PublishChange(); }
    private List<List<string>> Values() => _cells
        .Select(row => row.Select(box => NormalizeCellText(box.Text ?? string.Empty)).ToList())
        .ToList();
    private string Serialize() { var rows = Values(); var lines = new List<string> { "| " + string.Join(" | ", rows[0]) + " |", "| " + string.Join(" | ", rows[0].Select(_ => "---")) + " |" }; lines.AddRange(rows.Skip(1).Select(row => "| " + string.Join(" | ", row) + " |")); return string.Join("\n", lines); }

    private enum EdgePopupKind { None, Row, Column }
}
