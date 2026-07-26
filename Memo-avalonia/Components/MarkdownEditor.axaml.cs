using Avalonia;
using Avalonia.Automation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Memo.Components.Dialogs;
using Memo.Markdown;
using Memo.Services;
using Memo.UI;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace Memo.Components;

public partial class MarkdownEditor : UserControl {
    private readonly MarkdownImageStore _imageStore = new();
    private readonly DebouncedAction _autoSave;
    private readonly MarkdownEditSession _editSession = new();
    private readonly object _transitionChannel = new();
    private DispatcherTimer? _statusTimer;
    private bool _suppressTextChanged;
    private bool _isTransitioning;

    public MarkdownEditor() {
        InitializeComponent();
        _autoSave = new DebouncedAction(TimeSpan.FromMilliseconds(500));

        _preview.AssetPathRoot = _imageStore.RootDirectory;
        Loaded += OnLoaded;
        DetachedFromVisualTree += (_, _) => {
            _autoSave.Dispose();
            _statusTimer?.Stop();
            MotionAnimations.Cancel(_transitionChannel);
        };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        BeginNew();
    }

    public Func<MarkdownSaveRequest, Task<bool>>? SaveRequestedAsync { get; set; }
    public Func<string, Task>? CancelRequestedAsync { get; set; }
    public event EventHandler? EditRequested;
    public event EventHandler? NewRequested;
    public event Action<string>? DraftChanged;
    public event EventHandler? EditingCompleted;

    public bool IsEditing { get; private set; }
    public bool IsNewMemo { get; private set; }
    public bool ShowNewAction { get; set; }
    public string Markdown => _editor.Text ?? string.Empty;
    public string AssetPathRoot => _imageStore.RootDirectory;

    public void BeginNew(string? draft = null) {
        IsNewMemo = true;
        _editSession.Begin(draft);
        SetText(_editSession.Snapshot);
        SetEditingMode(true, animate: IsLoaded);
        HideStatus();
        Dispatcher.UIThread.Post(FocusEditor, DispatcherPriority.Render);
    }

    public void ShowExistingPreview(string? markdown) {
        IsNewMemo = false;
        _editSession.Begin(markdown);
        SetText(_editSession.Snapshot);
        SetEditingMode(false, animate: IsLoaded);
        HideStatus();
    }

    public void BeginExistingEdit(string? markdown) {
        IsNewMemo = false;
        _editSession.Begin(markdown);
        SetText(_editSession.Snapshot);
        SetEditingMode(true, animate: IsLoaded);
        HideStatus();
        Dispatcher.UIThread.Post(FocusEditor, DispatcherPriority.Render);
    }

    public void SetExternalMarkdown(string? markdown) {
        if (IsEditing) return;
        _editSession.Begin(markdown);
        SetText(_editSession.Snapshot);
    }

    public void FocusEditor() {
        if (!IsEditing) return;
        _editor.Focus();
        _editor.CaretIndex = _editor.Text?.Length ?? 0;
    }

    public Task<bool> CompleteEditingAsync() => RequestSaveAsync(completeEditing: true);

    public async Task CancelEditingAsync() {
        _autoSave.Cancel();
        var restore = _editSession.Restore();
        SetText(restore);
        if (CancelRequestedAsync != null) await CancelRequestedAsync(restore);

        if (IsNewMemo) BeginNew();
        else SetEditingMode(false, animate: true);
        EditingCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) {
        AutomationProperties.SetName(_modeButton, "\u9884\u89c8 Markdown");
        var formatLabels = new[] {
            "\u7c97\u4f53", "\u659c\u4f53", "\u65e0\u5e8f\u5217\u8868",
            "\u63d2\u5165\u94fe\u63a5", "\u63d2\u5165\u672c\u5730\u56fe\u7247",
        };
        var formatButtons = _formatToolbar.Children.OfType<Button>().ToArray();
        for (var index = 0; index < Math.Min(formatButtons.Length, formatLabels.Length); index++)
            AutomationProperties.SetName(formatButtons[index], formatLabels[index]);
        AutomationProperties.SetName(_moreButton, "\u66f4\u591a\u683c\u5f0f");
        AutomationProperties.SetName(_saveButton, "\u4fdd\u5b58");
        AutomationProperties.SetName(_newButton, "\u65b0\u5efa\u5907\u5fd8\u5f55");
        if (_preview.Engine is global::Markdown.Avalonia.IMarkdownEngine engine)
            engine.HyperlinkCommand = new SafeHyperlinkCommand();
        else if (_preview.Engine is global::Markdown.Avalonia.IMarkdownEngine2 engine2)
            engine2.HyperlinkCommand = new SafeHyperlinkCommand();
    }

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e) {
        if (_suppressTextChanged) return;
        var markdown = Markdown;
        _preview.Markdown = markdown;
        DraftChanged?.Invoke(markdown);
        ShowStatus("未保存", isError: false, autoHide: false);

        if (!IsNewMemo && IsEditing)
            _autoSave.Schedule(() => RequestSaveAsync(completeEditing: false));
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e) {
        var control = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (control && e.Key == Key.V && HasClipboardImageData()) {
            e.Handled = true;
            await InsertClipboardImagesAsync();
            return;
        }
        if (control && e.Key == Key.B) {
            ApplyFormat(MarkdownFormatCommand.Bold);
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.I) {
            ApplyFormat(MarkdownFormatCommand.Italic);
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.K) {
            ApplyFormat(MarkdownFormatCommand.Link);
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.Enter) {
            e.Handled = true;
            await RequestSaveAsync(completeEditing: true);
            return;
        }
        if (e.Key == Key.Escape) {
            e.Handled = true;
            await CancelEditingAsync();
            return;
        }
        if (IsNewMemo && e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0) {
            e.Handled = true;
            await RequestSaveAsync(completeEditing: true);
        }
    }

    private async void OnModeClick(object? sender, RoutedEventArgs e) {
        if (_isTransitioning) return;
        if (IsEditing) {
            if (IsNewMemo) SetEditingMode(false, animate: true);
            else await RequestSaveAsync(completeEditing: true);
        }
        else if (IsNewMemo) {
            SetEditingMode(true, animate: true);
            FocusEditor();
        }
        else {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e) {
        if (IsNewMemo) {
            SetEditingMode(true, animate: true);
            FocusEditor();
        }
        else {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e) =>
        await RequestSaveAsync(completeEditing: true);

    private void OnNewClick(object? sender, RoutedEventArgs e) =>
        NewRequested?.Invoke(this, EventArgs.Empty);

    private void OnFormatClick(object? sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MarkdownFormatCommand>(tag, out var command))
            ApplyFormat(command);
    }

    private void OnFormatMenuClick(object? sender, RoutedEventArgs e) {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<MarkdownFormatCommand>(tag, out var command))
            ApplyFormat(command);
    }

    private void ApplyFormat(MarkdownFormatCommand command) {
        if (!IsEditing) return;
        ApplyResult(MarkdownFormatter.Apply(
            Markdown, _editor.SelectionStart, _editor.SelectionEnd, command));
    }

    private void ApplyResult(MarkdownEditResult result) {
        _editor.Text = result.Text;
        _editor.SelectionStart = result.SelectionStart;
        _editor.SelectionEnd = result.SelectionEnd;
        _editor.Focus();
    }

    private async void OnLocalImageClick(object? sender, RoutedEventArgs e) {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "插入图片",
            AllowMultiple = true,
            FileTypeFilter = new[] {
                new FilePickerFileType("图片") {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp", "*.svg" },
                    MimeTypes = new[] { "image/*" },
                },
            },
        });
        await InsertStorageFilesAsync(files.OfType<IStorageFile>());
    }

    private async void OnRemoteImageClick(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var url = await new ImageUrlDialog().ShowDialog<string?>(owner);
        if (url == null) return;
        var result = MarkdownFormatter.InsertImage(
            Markdown, _editor.SelectionStart, _editor.SelectionEnd, "网络图片", url);
        ApplyResult(result);
    }

    private void OnDragOver(object? sender, DragEventArgs e) {
        var files = e.Data.GetFiles();
        if (files?.OfType<IStorageFile>().Any(file => MarkdownImageStore.IsSupportedFile(file.Name)) == true) {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e) {
        var files = e.Data.GetFiles()?.OfType<IStorageFile>() ?? Enumerable.Empty<IStorageFile>();
        await InsertStorageFilesAsync(files);
        e.Handled = true;
    }

    private async Task InsertStorageFilesAsync(IEnumerable<IStorageFile> files) {
        foreach (var file in files.Where(file => MarkdownImageStore.IsSupportedFile(file.Name))) {
            try {
                await using var stream = await file.OpenReadAsync();
                var path = await _imageStore.StoreAsync(stream, Path.GetExtension(file.Name));
                ApplyResult(MarkdownFormatter.InsertImage(
                    Markdown, _editor.SelectionStart, _editor.SelectionEnd,
                    Path.GetFileNameWithoutExtension(file.Name), path));
            }
            catch (Exception exception) {
                ShowStatus(exception.Message, isError: true, autoHide: false);
            }
        }
    }

    private static bool HasClipboardImageData() {
        try {
            return WinFormsClipboard.ContainsImage() || WinFormsClipboard.ContainsFileDropList();
        }
        catch { return false; }
    }

    private async Task InsertClipboardImagesAsync() {
        try {
            if (WinFormsClipboard.ContainsFileDropList()) {
                StringCollection paths = WinFormsClipboard.GetFileDropList();
                foreach (var path in paths.Cast<string>().Where(MarkdownImageStore.IsSupportedFile)) {
                    var stored = await _imageStore.StoreFileAsync(path);
                    ApplyResult(MarkdownFormatter.InsertImage(
                        Markdown, _editor.SelectionStart, _editor.SelectionEnd,
                        Path.GetFileNameWithoutExtension(path), stored));
                }
                return;
            }

            using var image = WinFormsClipboard.GetImage();
            if (image == null) return;
            await using var stream = new MemoryStream();
            image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var storedPath = await _imageStore.StoreAsync(stream, ".png");
            ApplyResult(MarkdownFormatter.InsertImage(
                Markdown, _editor.SelectionStart, _editor.SelectionEnd, "粘贴的图片", storedPath));
        }
        catch (Exception exception) {
            ShowStatus(exception.Message, isError: true, autoHide: false);
        }
    }

    private async Task<bool> RequestSaveAsync(bool completeEditing) {
        _autoSave.Cancel();
        var markdown = Markdown.TrimEnd('\r', '\n', ' ', '\t');
        if (IsNewMemo && !MarkdownFormatter.HasMeaningfulContent(markdown)) {
            ShowStatus("内容不能为空", isError: true, autoHide: false);
            return false;
        }
        if (SaveRequestedAsync == null) return false;

        ShowStatus("保存中", isError: false, autoHide: false);
        var wasNew = IsNewMemo;
        try {
            var saved = await SaveRequestedAsync(new MarkdownSaveRequest(markdown, completeEditing, wasNew));
            if (!saved) {
                ShowStatus("保存失败", isError: true, autoHide: false);
                return false;
            }

            ShowStatus("已保存", isError: false, autoHide: true);
            if (wasNew) {
                BeginNew();
            }
            else if (completeEditing) {
                _editSession.Commit(markdown);
                SetText(markdown);
                SetEditingMode(false, animate: true);
                EditingCompleted?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        catch (Exception exception) {
            ShowStatus(exception.Message, isError: true, autoHide: false);
            return false;
        }
    }

    private void SetText(string text) {
        _suppressTextChanged = true;
        _editor.Text = text;
        _preview.Markdown = text;
        _suppressTextChanged = false;
    }

    private void SetEditingMode(bool editing, bool animate) {
        IsEditing = editing;
        _formatToolbar.IsVisible = editing;
        _moreButton.IsVisible = editing;
        _saveButton.IsVisible = editing || IsNewMemo;
        _newButton.IsVisible = !editing && !IsNewMemo && ShowNewAction;
        _modeIcon.Data = (Geometry)Resources[editing ? "MdPreviewIcon" : "MdEditIcon"]!;
        ToolTip.SetTip(_modeButton, editing ? "预览 Markdown" : "编辑 Markdown");
        AutomationProperties.SetName(_modeButton, editing ? "\u9884\u89c8 Markdown" : "\u7f16\u8f91 Markdown");
        TransitionLayers(editing, animate);
    }

    private void TransitionLayers(bool showEditor, bool animate) {
        var incoming = showEditor ? (Control)_editor : _preview;
        var outgoing = showEditor ? (Control)_preview : _editor;
        var topLevel = TopLevel.GetTopLevel(this);
        MotionAnimations.Cancel(_transitionChannel);

        incoming.IsVisible = true;
        if (!animate || topLevel == null || !MotionPreferences.AnimationsEnabled) {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            outgoing.IsVisible = false;
            _isTransitioning = false;
            return;
        }

        _isTransitioning = true;
        var incomingFrom = incoming.Opacity;
        var outgoingFrom = outgoing.Opacity;
        MotionAnimations.Start(_transitionChannel, topLevel, MotionPreferences.StandardDuration,
            new CubicEaseOut(), progress => {
                incoming.Opacity = MotionAnimations.Lerp(incomingFrom, 1, progress);
                outgoing.Opacity = MotionAnimations.Lerp(outgoingFrom, 0, progress);
            }, () => {
                outgoing.IsVisible = false;
                _isTransitioning = false;
            });
    }

    private void ShowStatus(string message, bool isError, bool autoHide) {
        _statusTimer?.Stop();
        _statusText.Text = message;
        _statusText.Foreground = (IBrush)Application.Current!.Resources[
            isError ? "DangerPrimaryBrush" : "TextSecondaryBrush"]!;
        _statusBadge.Background = (IBrush)Application.Current.Resources[
            isError ? "DangerSubtleBrush" : "BgTertiaryBrush"]!;
        _statusBadge.Opacity = 1;
        if (!autoHide) return;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _statusTimer.Tick += (_, _) => {
            _statusTimer?.Stop();
            _statusBadge.Opacity = 0;
        };
        _statusTimer.Start();
    }

    private void HideStatus() {
        _statusTimer?.Stop();
        _statusBadge.Opacity = 0;
    }
}
