using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Memo.Models;
using Memo.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Memo.Views;

public partial class MainWindow {
    private const double DockTransitionSize = 5;
    private const int DockDetectionPixels = 40;
    private const int DockDetachPixels = 48;
    private const int DockLongPressMilliseconds = 10;
    private const double DockMoveTolerance = 5;
    private const double DockIdleScale = 0.9;
    private const double DockActiveScale = 1;
    private const double ExpandedMinWidth = 360;
    private const double ExpandedMinHeight = 480;
    private const double ExpandedTitleBarHeight = 49;

    private enum DockState { Expanded, DockPreview, Docked, Restoring }

    private DockState _dockState = DockState.Expanded;
    private double _dockSize = AppSettings.DefaultMainWindowDockSize;
    private bool _dockEnabled = true;
    private MainWindowDockEdge _dockEdge = MainWindowDockEdge.Left;
    private PixelRect _dockWorkArea;
    private double _dockScreenScaling = 1;
    private double _dockNormalizedPosition = 0.5;

    private PixelPoint _expandedPosition;
    private double _expandedWidth = 420;
    private double _expandedHeight = 680;
    private bool _hasExpandedBounds;

    private Func<AppSettings, Task>? _persistWindowStateAsync;

    private bool _titleDragging;
    private IPointer? _activePointer;
    private PixelPoint _pointerGrabOffset;
    private PixelPoint _latestPointerScreen;
    private bool _finalizeDockWhenAnimationCompletes;

    private DispatcherTimer? _dockLongPressTimer;
    private PixelPoint _dockPressScreen;
    private bool _dockLongPressReady;
    private bool _draggingExpandedFromDock;
    private IPointer? _dockMenuPointer;
    private bool _useTitleBarCenterDragAnchor;
    private bool _hasMeasuredTitleBarDragCenter;
    private double _titleBarDragCenterOffsetX;
    private double _titleBarDragCenterY = ExpandedTitleBarHeight / 2;

    private FrameAnimation? _dockAnimation;
    private FrameAnimation? _dockScaleAnimation;
    private DispatcherTimer? _expandedBoundsSaveTimer;
    private bool _expandedStartupSyncPending;
    private bool _isSynchronizingExpandedStartupBounds;
    private PixelPoint _animationFromPosition;
    private double _animationFromWidth;
    private double _animationFromHeight;
    private double _animationFromExpandedOpacity;
    private double _animationFromDockOpacity;
    private PixelPoint _animationTargetPosition;
    private double _animationTargetWidth;
    private double _animationTargetHeight;
    private double _animationTargetExpandedOpacity;
    private double _animationTargetDockOpacity;
    private Action? _dockAnimationCompleted;
    private PixelRect? _lastSubmittedNativeBounds;
    private double _animationFromSurfaceScaleX = 1;
    private double _animationFromSurfaceScaleY = 1;
    private double _animationTargetSurfaceScaleX = 1;
    private double _animationTargetSurfaceScaleY = 1;

    private IReadOnlyDictionary<MainWindowDockEdge, Geometry> _dockGeometries =
        BuildDockGeometries(AppSettings.DefaultMainWindowDockSize);

    private void InitializeDockingInteraction() {
        var titleBar = this.FindControl<Border>("_titleBarDrag");
        if (titleBar != null) {
            titleBar.PointerMoved += OnTitleBarPointerMoved;
            titleBar.PointerReleased += OnTitleBarPointerReleased;
            titleBar.PointerCaptureLost += OnTitleBarPointerCaptureLost;
        }

        PositionChanged += (_, _) => ScheduleExpandedBoundsSave();
        SizeChanged += (_, _) => ScheduleExpandedBoundsSave();
    }

    private void ApplyDockSizeSetting(int requestedSize) {
        var size = Math.Clamp(
            requestedSize,
            AppSettings.MinimumMainWindowDockSize,
            AppSettings.MaximumMainWindowDockSize);
        _settings.MainWindowDockSize = size;
        if (Math.Abs(_dockSize - size) < 0.001) return;

        _dockSize = size;
        _dockGeometries = BuildDockGeometries(_dockSize);
        UpdateDockShape(_dockEdge);

        if (_dockState is not (DockState.Docked or DockState.DockPreview)) return;

        var screen = Screens.ScreenFromWindow(this)
            ?? Screens.ScreenFromPoint(Position)
            ?? Screens.Primary;
        if (screen != null) {
            _dockWorkArea = screen.WorkingArea;
            _dockScreenScaling = screen.Scaling;
        }
        if (_dockWorkArea.Width <= 0 || _dockWorkArea.Height <= 0) return;

        var target = DockPositionFromNormalized(
            _dockEdge,
            _dockWorkArea,
            _dockScreenScaling,
            _dockNormalizedPosition);
        MinWidth = _dockSize;
        MinHeight = _dockSize;
        _animationTargetPosition = target;
        _animationTargetWidth = DockWidth(_dockEdge);
        _animationTargetHeight = DockHeight(_dockEdge);
        SubmitAnimatedWindowBounds(target, _animationTargetWidth, _animationTargetHeight, synchronizeAvalonia: true);
        ApplyDockClip();
    }

    private void ApplyDockEnabledSetting(bool enabled) {
        _settings.MainWindowDockEnabled = enabled;
        if (!enabled) _settings.MainWindowDocked = false;
        if (_dockEnabled == enabled) return;

        _dockEnabled = enabled;
        if (enabled) return;

        _finalizeDockWhenAnimationCompletes = false;
        if (_dockState is DockState.Docked or DockState.DockPreview or DockState.Restoring)
            RestoreFromDock(null, null);
    }

    public void ConfigureWindowStatePersistence(Func<AppSettings, Task> persistWindowStateAsync) {
        _persistWindowStateAsync = persistWindowStateAsync;
    }

    public void InitializeFromSettingsAndShow(AppSettings settings) {
        ApplySettings(settings);
        LoadExpandedBounds(settings);
        SetPinnedCore(settings.MainWindowTopmost);

        if (settings.MainWindowDockEnabled && settings.MainWindowDocked) {
            PrepareDockedStartup(settings);
        }
        else {
            PrepareExpandedStartup(settings);
            _expandedStartupSyncPending = true;
        }

        ShowWithOpenTransition(force: true);
    }

    private void CompleteOpenAfterShow() {
        if (!_expandedStartupSyncPending || _dockState != DockState.Expanded) {
            _expandedStartupSyncPending = false;
            PlayOpenTransition();
            return;
        }

        _expandedStartupSyncPending = false;
        SynchronizeExpandedStartupBounds();

        // Let Avalonia complete the invalidated layout before the first animation frame.
        Dispatcher.UIThread.Post(() => {
            _expandedBoundsSaveTimer?.Stop();
            _expandedBoundsSaveTimer = null;
            _isSynchronizingExpandedStartupBounds = false;
            PlayOpenTransition();
        }, DispatcherPriority.Background);
    }

    private void SynchronizeExpandedStartupBounds() {
        var screen = (_hasExpandedBounds ? Screens.ScreenFromPoint(_expandedPosition) : null)
            ?? Screens.ScreenFromWindow(this)
            ?? Screens.Primary;
        if (screen == null) return;

        var requestedPosition = _hasExpandedBounds ? _expandedPosition : Position;
        var bounds = ClampExpandedBounds(screen, requestedPosition, _expandedWidth, _expandedHeight);

        _isSynchronizingExpandedStartupBounds = true;
        _expandedBoundsSaveTimer?.Stop();
        _expandedBoundsSaveTimer = null;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SubmitAnimatedWindowBounds(bounds.Position, bounds.Width, bounds.Height, synchronizeAvalonia: true);
        SaveExpandedBounds(bounds.Position, bounds.Width, bounds.Height);

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        _expandedSurface?.InvalidateMeasure();
        _expandedSurface?.InvalidateArrange();
        _expandedSurface?.InvalidateVisual();
        _expandedContent?.InvalidateMeasure();
        _expandedContent?.InvalidateArrange();
        _expandedContent?.InvalidateVisual();
    }

    public void CopyRuntimeWindowStateTo(AppSettings target) {
        if (_dockEnabled && (_dockState == DockState.Docked || _dockState == DockState.DockPreview)) {
            UpdateDockPersistenceFields(target);
        }
        else {
            target.MainWindowDocked = false;
        }

        if (_hasExpandedBounds) {
            target.MainWindowHasExpandedBounds = true;
            target.MainWindowExpandedX = _expandedPosition.X;
            target.MainWindowExpandedY = _expandedPosition.Y;
            target.MainWindowExpandedWidth = _expandedWidth;
            target.MainWindowExpandedHeight = _expandedHeight;
        }

        target.MainWindowTopmost = _isPinned;
    }

    public void ShowExpandedWithTransition(bool focusInput = false) {
        void Finish() {
            ShowInTaskbar = false;
            Activate();
            if (focusInput) Dispatcher.UIThread.Post(FocusInputForNewMemo, DispatcherPriority.Input);
        }

        if (_dockState is DockState.Docked or DockState.DockPreview) {
            if (!IsVisible) {
                WindowState = WindowState.Normal;
                ShowInTaskbar = false;
                _windowTransition?.Reset();
                Show();
            }

            RestoreFromDock(null, Finish);
            return;
        }

        if (_dockState == DockState.Restoring) {
            _restoreCompletion = Finish;
            return;
        }

        ShowWithOpenTransition(force: false);
        if (focusInput) Dispatcher.UIThread.Post(FocusInputForNewMemo, DispatcherPriority.Input);
    }

    private Action? _restoreCompletion;

    private void LoadExpandedBounds(AppSettings settings) {
        if (!settings.MainWindowHasExpandedBounds) return;
        _hasExpandedBounds = true;
        _expandedPosition = new PixelPoint(settings.MainWindowExpandedX, settings.MainWindowExpandedY);
        _expandedWidth = Math.Max(ExpandedMinWidth, settings.MainWindowExpandedWidth);
        _expandedHeight = Math.Max(ExpandedMinHeight, settings.MainWindowExpandedHeight);
    }

    private void PrepareExpandedStartup(AppSettings settings) {
        _dockState = DockState.Expanded;
        SetExpandedVisualState();
        if (!settings.MainWindowHasExpandedBounds) return;

        var screen = Screens.ScreenFromPoint(_expandedPosition)
            ?? FindScreenForSavedArea(settings)
            ?? Screens.Primary;
        if (screen == null) return;
        var bounds = ClampExpandedBounds(screen, _expandedPosition, _expandedWidth, _expandedHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = bounds.Width;
        Height = bounds.Height;
        Position = bounds.Position;
        SaveExpandedBounds(bounds.Position, bounds.Width, bounds.Height);
    }

    private void PrepareDockedStartup(AppSettings settings) {
        var screen = FindScreenForSavedArea(settings) ?? Screens.Primary;
        if (screen == null) {
            PrepareExpandedStartup(settings);
            return;
        }

        _dockEdge = settings.MainWindowDockEdge;
        _dockNormalizedPosition = Math.Clamp(settings.MainWindowDockPosition, 0, 1);
        _dockWorkArea = screen.WorkingArea;
        _dockScreenScaling = screen.Scaling;
        var target = DockPositionFromNormalized(_dockEdge, _dockWorkArea, _dockScreenScaling, _dockNormalizedPosition);

        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = _dockSize;
        MinHeight = _dockSize;
        Width = DockWidth(_dockEdge);
        Height = DockHeight(_dockEdge);
        Position = target;
        _dockState = DockState.Docked;
        SetDockVisualScale(DockIdleScale);
        SetDockedVisualState();
    }

    private Screen? FindScreenForSavedArea(AppSettings settings) {
        if (settings.MainWindowDockWorkAreaWidth <= 0 || settings.MainWindowDockWorkAreaHeight <= 0)
            return Screens.Primary;

        var saved = new PixelRect(
            settings.MainWindowDockWorkAreaX,
            settings.MainWindowDockWorkAreaY,
            settings.MainWindowDockWorkAreaWidth,
            settings.MainWindowDockWorkAreaHeight);

        var exact = Screens.All.FirstOrDefault(s => s.WorkingArea == saved);
        if (exact != null) return exact;

        var center = new PixelPoint(saved.X + saved.Width / 2, saved.Y + saved.Height / 2);
        return Screens.ScreenFromPoint(center) ?? Screens.Primary;
    }

    private void BeginTitleBarDrag(Border titleBar, PointerPressedEventArgs e) {
        if (_dockState != DockState.Expanded || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        StopWindowTransitionForDocking();
        SaveCurrentExpandedBounds();
        _titleDragging = true;
        _activePointer = e.Pointer;
        _latestPointerScreen = PointerScreenPosition(e);
        _pointerGrabOffset = new PixelPoint(
            _latestPointerScreen.X - Position.X,
            _latestPointerScreen.Y - Position.Y);
        _useTitleBarCenterDragAnchor = false;
        e.Pointer.Capture(titleBar);
        e.Handled = true;
    }

    private void OnTitleBarPointerMoved(object? sender, PointerEventArgs e) {
        if (!_titleDragging || e.Pointer != _activePointer) return;
        _latestPointerScreen = PointerScreenPosition(e);

        if (_dockState == DockState.Expanded) {
            UpdateExpandedDragPosition(_latestPointerScreen);

            if (TryFindDockEdge(_latestPointerScreen, out var edge, out var screen))
                BeginDockPreview(edge, screen, _latestPointerScreen);
        }
        else if (_dockState == DockState.DockPreview) {
            if (TryFindDockEdge(_latestPointerScreen, out var edge, out var screen)) {
                UpdateDockPreviewTarget(edge, screen, _latestPointerScreen);
            }
            else if (IsPastDetachThreshold(_latestPointerScreen, _dockEdge, _dockWorkArea)) {
                RestoreFromDock(_latestPointerScreen, null);
            }
        }
        else if (_dockState == DockState.Restoring) {
            if (TryFindDockEdge(_latestPointerScreen, out var edge, out var screen))
                BeginDockPreview(edge, screen, _latestPointerScreen);
            else
                UpdateRestoreDragTarget(_latestPointerScreen);
        }

        e.Handled = true;
    }

    private void OnTitleBarPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (!_titleDragging || e.Pointer != _activePointer) return;
        _latestPointerScreen = PointerScreenPosition(e);
        _titleDragging = false;
        _activePointer = null;
        e.Pointer.Capture(null);

        if (_dockState == DockState.DockPreview) {
            _finalizeDockWhenAnimationCompletes = true;
            if (_dockAnimation == null) FinalizeDock();
        }
        else if (_dockState == DockState.Expanded) {
            SaveCurrentExpandedBounds();
            PersistRuntimeWindowState();
        }
        e.Handled = true;
    }

    private void OnTitleBarPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (!_titleDragging) return;
        _titleDragging = false;
        _activePointer = null;
        if (_dockState == DockState.DockPreview) {
            _finalizeDockWhenAnimationCompletes = true;
            if (_dockAnimation == null) FinalizeDock();
        }
    }

    private void BeginDockPreview(MainWindowDockEdge edge, Screen screen, PixelPoint cursor) {
        if (!_dockEnabled) return;
        if (_dockState == DockState.Expanded) MeasureTitleBarDragCenter();
        if (_dockState == DockState.Restoring) _restoreCompletion = null;
        SaveCurrentExpandedBounds();
        _dockState = DockState.DockPreview;
        AnimateDockVisualScale(DockActiveScale);
        _finalizeDockWhenAnimationCompletes = false;
        MinWidth = _dockSize;
        MinHeight = _dockSize;
        ClearDockClip();
        UpdateDockPreviewTarget(edge, screen, cursor);

        StartDockAnimation(
            _animationTargetPosition,
            DockWidth(_dockEdge),
            DockHeight(_dockEdge),
            0,
            1,
            CompleteDockPreviewAnimation);
    }

    private void UpdateDockPreviewTarget(MainWindowDockEdge edge, Screen screen, PixelPoint cursor) {
        var edgeChanged = edge != _dockEdge;
        var wasAnimating = _dockAnimation != null;
        var wasCompact = _dockState == DockState.DockPreview
            && _dockAnimation == null
            && (_dockLayer?.Opacity ?? 0) >= 0.99;
        _dockEdge = edge;
        _dockWorkArea = screen.WorkingArea;
        _dockScreenScaling = screen.Scaling;
        _animationTargetPosition = DockPositionFromCursor(edge, _dockWorkArea, _dockScreenScaling, cursor);
        _animationTargetWidth = DockWidth(edge);
        _animationTargetHeight = DockHeight(edge);
        _dockNormalizedPosition = CalculateNormalizedDockPosition(edge, _dockWorkArea, _dockScreenScaling, _animationTargetPosition);
        UpdateDockShape(edge);

        if (wasAnimating && edgeChanged) {
            StartDockAnimation(
                _animationTargetPosition,
                _animationTargetWidth,
                _animationTargetHeight,
                0,
                1,
                CompleteDockPreviewAnimation);
            return;
        }

        if (!wasCompact) return;
        if (Math.Abs(CurrentWindowWidth() - DockWidth(edge)) > 1
            || Math.Abs(CurrentWindowHeight() - DockHeight(edge)) > 1) {
            ClearDockClip();
            StartDockAnimation(
                _animationTargetPosition,
                DockWidth(edge),
                DockHeight(edge),
                0,
                1,
                CompleteDockPreviewAnimation);
        }
        else {
            Width = DockWidth(edge);
            Height = DockHeight(edge);
            Position = _animationTargetPosition;
            ApplyDockClip();
        }
    }

    private void CompleteDockPreviewAnimation() {
        _dockAnimation = null;
        if (!_dockEnabled) {
            RestoreFromDock(null, null);
            return;
        }
        if (_finalizeDockWhenAnimationCompletes || !IsDockGestureActive()) FinalizeDock();
        else ApplyCompactVisualWhileCaptured();
    }

    private void FinalizeDock() {
        if (!_dockEnabled) {
            RestoreFromDock(null, null);
            return;
        }
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        Width = DockWidth(_dockEdge);
        Height = DockHeight(_dockEdge);
        Position = _animationTargetPosition;
        _dockState = DockState.Docked;
        _finalizeDockWhenAnimationCompletes = false;
        SetDockedVisualState();
        AnimateDockVisualScale(DockIdleScale);
        PersistRuntimeWindowState();
    }

    private void ApplyCompactVisualWhileCaptured() {
        _dockState = DockState.DockPreview;
        SetDockedVisualState(allowHitTesting: false);
    }

    private void RestoreFromDock(PixelPoint? dragCursor, Action? completed) {
        if (_dockState == DockState.Restoring) {
            if (completed != null) _restoreCompletion = completed;
            return;
        }

        _dockLongPressTimer?.Stop();
        _dockLongPressTimer = null;
        _finalizeDockWhenAnimationCompletes = false;
        _dockState = DockState.Restoring;
        _restoreCompletion = completed;
        ClearDockClip();
        if (_expandedSurface != null) _expandedSurface.IsHitTestVisible = false;
        if (_dockLayer != null) _dockLayer.IsHitTestVisible = false;

        var screen = dragCursor.HasValue
            ? Screens.ScreenFromPoint(dragCursor.Value)
            : Screens.ScreenFromWindow(this);
        screen ??= Screens.Primary;
        if (screen == null) {
            RecoverFromFailedRestore();
            return;
        }

        if (dragCursor.HasValue) {
            if (!TryGetInteractiveExpandedBounds(dragCursor.Value, out var dragTarget)) {
                RecoverFromFailedRestore();
                return;
            }
            StartDockAnimation(dragTarget.Position, dragTarget.Width, dragTarget.Height, 1, 0, CompleteRestore);
        }
        else {
            var target = ClampExpandedBounds(screen, _expandedPosition, _expandedWidth, _expandedHeight);
            StartDockAnimation(target.Position, target.Width, target.Height, 1, 0, CompleteRestore);
        }
    }

    private void CompleteRestore() {
        _dockAnimation = null;
        _dockState = DockState.Expanded;
        MinWidth = ExpandedMinWidth;
        MinHeight = ExpandedMinHeight;
        SetExpandedVisualState();
        SaveCurrentExpandedBounds();
        PersistRuntimeWindowState();
        var completion = _restoreCompletion;
        _restoreCompletion = null;
        completion?.Invoke();
    }

    private void RecoverFromFailedRestore() {
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        _dockAnimationCompleted = null;
        _restoreCompletion = null;
        _dockState = DockState.Docked;
        _finalizeDockWhenAnimationCompletes = false;
        SetDockedVisualState();
        AnimateDockVisualScale(DockIdleScale);
    }

    private void UpdateRestoreDragTarget(PixelPoint cursor) {
        if (!TryGetInteractiveExpandedBounds(cursor, out var target)) return;
        _animationTargetPosition = target.Position;
        _animationTargetWidth = target.Width;
        _animationTargetHeight = target.Height;
    }

    private void StartDockAnimation(
        PixelPoint targetPosition,
        double targetWidth,
        double targetHeight,
        double targetExpandedOpacity,
        double targetDockOpacity,
        Action completed) {
        _dockAnimation?.Cancel();
        if (TryGetNativeWindowBounds(out var actualBounds)) {
            _lastSubmittedNativeBounds = actualBounds;
            _animationFromPosition = actualBounds.Position;
            var scaling = Math.Max(0.01, RenderScaling);
            _animationFromWidth = actualBounds.Width / scaling;
            _animationFromHeight = actualBounds.Height / scaling;
        }
        else {
            _animationFromPosition = Position;
            _animationFromWidth = CurrentWindowWidth();
            _animationFromHeight = CurrentWindowHeight();
        }
        _animationFromExpandedOpacity = _expandedSurface?.Opacity ?? 1;
        _animationFromDockOpacity = _dockLayer?.Opacity ?? 0;
        _animationTargetPosition = targetPosition;
        _animationTargetWidth = targetWidth;
        _animationTargetHeight = targetHeight;
        _animationTargetExpandedOpacity = targetExpandedOpacity;
        _animationTargetDockOpacity = targetDockOpacity;
        _dockAnimationCompleted = completed;
        PrepareExpandedSurfaceForAnimation();

        var duration = NativeBoundsAreClose(
            _animationFromPosition,
            _animationFromWidth,
            _animationFromHeight,
            targetPosition,
            targetWidth,
            targetHeight)
            ? TimeSpan.Zero
            : MotionPreferences.DockDuration;

        FrameAnimation? animation = null;
        animation = new FrameAnimation(this, duration, new CubicEaseOut(), progress => {
            var nextWidth = Lerp(_animationFromWidth, _animationTargetWidth, progress);
            var nextHeight = Lerp(_animationFromHeight, _animationTargetHeight, progress);
            var nextPosition = new PixelPoint(
                (int)Math.Round(Lerp(_animationFromPosition.X, _animationTargetPosition.X, progress)),
                (int)Math.Round(Lerp(_animationFromPosition.Y, _animationTargetPosition.Y, progress)));

            SubmitAnimatedWindowBounds(nextPosition, nextWidth, nextHeight, synchronizeAvalonia: false);
            if (_expandedSurface != null)
                _expandedSurface.Opacity = Lerp(_animationFromExpandedOpacity, _animationTargetExpandedOpacity, progress);
            if (_dockLayer != null)
                _dockLayer.Opacity = Lerp(_animationFromDockOpacity, _animationTargetDockOpacity, progress);
            SetExpandedSurfaceScale(
                Lerp(_animationFromSurfaceScaleX, _animationTargetSurfaceScaleX, progress),
                Lerp(_animationFromSurfaceScaleY, _animationTargetSurfaceScaleY, progress));
        }, () => {
            if (!ReferenceEquals(_dockAnimation, animation)) return;
            SubmitAnimatedWindowBounds(
                _animationTargetPosition,
                _animationTargetWidth,
                _animationTargetHeight,
                synchronizeAvalonia: true);
            _dockAnimation = null;
            var callback = _dockAnimationCompleted;
            _dockAnimationCompleted = null;
            callback?.Invoke();
        });

        _dockAnimation = animation;
        animation.Start();
    }

    private bool NativeBoundsAreClose(
        PixelPoint fromPosition,
        double fromWidth,
        double fromHeight,
        PixelPoint targetPosition,
        double targetWidth,
        double targetHeight) {
        var from = ToPixelBounds(fromPosition, fromWidth, fromHeight);
        var target = ToPixelBounds(targetPosition, targetWidth, targetHeight);
        return Math.Abs(from.X - target.X) < 1
            && Math.Abs(from.Y - target.Y) < 1
            && Math.Abs(from.Width - target.Width) < 1
            && Math.Abs(from.Height - target.Height) < 1;
    }

    private PixelRect ToPixelBounds(PixelPoint position, double width, double height) {
        var scaling = Math.Max(0.01, RenderScaling);
        return new PixelRect(
            position,
            new PixelSize(
                Math.Max(1, (int)Math.Round(width * scaling)),
                Math.Max(1, (int)Math.Round(height * scaling))));
    }

    private void SubmitAnimatedWindowBounds(
        PixelPoint position,
        double width,
        double height,
        bool synchronizeAvalonia) {
        var bounds = ToPixelBounds(position, width, height);
        var submittedNatively = false;
        var platformHandle = TryGetPlatformHandle();
        if (OperatingSystem.IsWindows()
            && platformHandle?.HandleDescriptor == "HWND"
            && platformHandle.Handle != IntPtr.Zero) {
            if (_lastSubmittedNativeBounds != bounds) {
                submittedNatively = SetWindowPos(
                    platformHandle.Handle,
                    IntPtr.Zero,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
                if (submittedNatively) _lastSubmittedNativeBounds = bounds;
            }
            else {
                submittedNatively = true;
            }
        }

        if (!submittedNatively || synchronizeAvalonia) {
            if (Math.Abs(Width - width) > 0.01) Width = width;
            if (Math.Abs(Height - height) > 0.01) Height = height;
            if (Position != position) Position = position;
        }
    }

    private bool TryGetNativeWindowBounds(out PixelRect bounds) {
        bounds = default;
        var platformHandle = TryGetPlatformHandle();
        if (!OperatingSystem.IsWindows()
            || platformHandle?.HandleDescriptor != "HWND"
            || platformHandle.Handle == IntPtr.Zero
            || !GetWindowRect(platformHandle.Handle, out var rect)) return false;

        bounds = new PixelRect(
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));
        return true;
    }

    private void PrepareExpandedSurfaceForAnimation() {
        if (_expandedSurface == null) return;

        var transform = _expandedSurface.RenderTransform as ScaleTransform;
        var oldScaleX = transform?.ScaleX ?? 1;
        var oldScaleY = transform?.ScaleY ?? 1;
        var oldBaseWidth = double.IsNaN(_expandedSurface.Width) ? _animationFromWidth : _expandedSurface.Width;
        var oldBaseHeight = double.IsNaN(_expandedSurface.Height) ? _animationFromHeight : _expandedSurface.Height;
        var effectiveWidth = oldBaseWidth * oldScaleX;
        var effectiveHeight = oldBaseHeight * oldScaleY;
        var baseWidth = Math.Max(oldBaseWidth, _animationTargetWidth);
        var baseHeight = Math.Max(oldBaseHeight, _animationTargetHeight);

        _expandedSurface.Width = baseWidth;
        _expandedSurface.Height = baseHeight;
        _expandedSurface.HorizontalAlignment = _dockEdge switch {
            MainWindowDockEdge.Left => Avalonia.Layout.HorizontalAlignment.Left,
            MainWindowDockEdge.Right => Avalonia.Layout.HorizontalAlignment.Right,
            _ => Avalonia.Layout.HorizontalAlignment.Center,
        };
        _expandedSurface.VerticalAlignment = _dockEdge switch {
            MainWindowDockEdge.Top => Avalonia.Layout.VerticalAlignment.Top,
            MainWindowDockEdge.Bottom => Avalonia.Layout.VerticalAlignment.Bottom,
            _ => Avalonia.Layout.VerticalAlignment.Center,
        };
        _expandedSurface.RenderTransformOrigin = _dockEdge switch {
            MainWindowDockEdge.Left => new RelativePoint(0, 0.5, RelativeUnit.Relative),
            MainWindowDockEdge.Right => new RelativePoint(1, 0.5, RelativeUnit.Relative),
            MainWindowDockEdge.Top => new RelativePoint(0.5, 0, RelativeUnit.Relative),
            MainWindowDockEdge.Bottom => new RelativePoint(0.5, 1, RelativeUnit.Relative),
            _ => new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        };

        _animationFromSurfaceScaleX = effectiveWidth / baseWidth;
        _animationFromSurfaceScaleY = effectiveHeight / baseHeight;
        _animationTargetSurfaceScaleX = _animationTargetExpandedOpacity >= 0.5 ? 1 : _animationTargetWidth / baseWidth;
        _animationTargetSurfaceScaleY = _animationTargetExpandedOpacity >= 0.5 ? 1 : _animationTargetHeight / baseHeight;
        SetExpandedSurfaceScale(_animationFromSurfaceScaleX, _animationFromSurfaceScaleY);
        _expandedSurface.IsHitTestVisible = false;
        if (_dockLayer != null) _dockLayer.IsHitTestVisible = false;
        ClearDockClip();
    }

    private void SetExpandedSurfaceScale(double x, double y) {
        if (_expandedSurface?.RenderTransform is not ScaleTransform transform) return;
        if (Math.Abs(transform.ScaleX - x) > 0.0001) transform.ScaleX = x;
        if (Math.Abs(transform.ScaleY - y) > 0.0001) transform.ScaleY = y;
    }

    private void OnDockPointerPressed(object? sender, PointerPressedEventArgs e) {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed) {
            if (_dockState != DockState.Docked) return;

            e.Handled = true;
            if (_activePointer != null) return;

            _dockMenuPointer = e.Pointer;
            return;
        }

        if (_dockState != DockState.Docked || !properties.IsLeftButtonPressed) return;
        if (sender is not Border dockSurface) return;

        _dockMenuPointer = null;
        _activePointer = e.Pointer;
        _dockPressScreen = PointerScreenPosition(e);
        _latestPointerScreen = _dockPressScreen;
        _dockLongPressReady = false;
        _draggingExpandedFromDock = false;
        e.Pointer.Capture(dockSurface);

        _dockLongPressTimer?.Stop();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(DockLongPressMilliseconds), DispatcherPriority.Input, (_, _) => { });
        timer.Tick += (_, _) => {
            timer.Stop();
            if (_activePointer != e.Pointer) return;
            _dockLongPressReady = true;
            AnimateDockVisualScale(DockActiveScale);
        };
        _dockLongPressTimer = timer;
        timer.Start();
        e.Handled = true;
    }

    private void OnDockPointerMoved(object? sender, PointerEventArgs e) {
        if (e.Pointer != _activePointer) return;
        _latestPointerScreen = PointerScreenPosition(e);

        if (!_dockLongPressReady) {
            if (Distance(_dockPressScreen, _latestPointerScreen) > DockMoveTolerance) {
                _dockLongPressTimer?.Stop();
                _dockLongPressTimer = null;
            }
            return;
        }

        if (_dockState == DockState.Restoring) {
            if (TryFindDockEdge(_latestPointerScreen, out var edge, out var screen))
                BeginDockPreview(edge, screen, _latestPointerScreen);
            else
                UpdateRestoreDragTarget(_latestPointerScreen);
            e.Handled = true;
            return;
        }

        if (_dockState == DockState.Expanded && _draggingExpandedFromDock) {
            UpdateExpandedDragPosition(_latestPointerScreen);
            if (TryFindDockEdge(_latestPointerScreen, out var edge, out var screen))
                BeginDockPreview(edge, screen, _latestPointerScreen);
            e.Handled = true;
            return;
        }

        var pointerScreen = Screens.ScreenFromPoint(_latestPointerScreen) ?? Screens.Primary;
        if (pointerScreen == null) return;
        var area = pointerScreen.WorkingArea;
        if (IsPastDetachThreshold(_latestPointerScreen, _dockEdge, area)) {
            _draggingExpandedFromDock = true;
            _useTitleBarCenterDragAnchor = true;
            RestoreFromDock(_latestPointerScreen, null);
            e.Handled = true;
            return;
        }

        _dockWorkArea = area;
        _dockScreenScaling = pointerScreen.Scaling;
        var target = DockPositionFromCursor(_dockEdge, area, pointerScreen.Scaling, _latestPointerScreen);
        Position = target;
        _animationTargetPosition = target;
        _dockNormalizedPosition = CalculateNormalizedDockPosition(_dockEdge, area, pointerScreen.Scaling, target);
        e.Handled = true;
    }

    private void OnDockPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (e.InitialPressMouseButton == MouseButton.Right) {
            var shouldShowMenu = _dockState == DockState.Docked
                && e.Pointer == _dockMenuPointer
                && _activePointer == null;
            var shouldHandle = shouldShowMenu || _dockState == DockState.Docked || _activePointer != null;
            _dockMenuPointer = null;

            if (shouldShowMenu) _showSharedMenu?.Invoke();
            if (shouldHandle) e.Handled = true;
            return;
        }

        if (e.Pointer != _activePointer) return;
        _dockLongPressTimer?.Stop();
        _dockLongPressTimer = null;
        _dockLongPressReady = false;
        _draggingExpandedFromDock = false;
        _activePointer = null;
        AnimateDockVisualScale(DockIdleScale);
        if (_dockState == DockState.DockPreview) {
            _finalizeDockWhenAnimationCompletes = true;
            if (_dockAnimation == null) FinalizeDock();
        }
        e.Pointer.Capture(null);
        if (_dockState == DockState.Docked) PersistRuntimeWindowState();
        else if (_dockState == DockState.DockPreview) {
            _finalizeDockWhenAnimationCompletes = true;
            if (_dockAnimation == null) FinalizeDock();
        }
        else if (_dockState == DockState.Expanded) {
            SaveCurrentExpandedBounds();
            PersistRuntimeWindowState();
        }
        e.Handled = true;
    }

    private void OnDockPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        _dockLongPressTimer?.Stop();
        _dockLongPressTimer = null;
        _dockLongPressReady = false;
        _draggingExpandedFromDock = false;
        _activePointer = null;
        AnimateDockVisualScale(DockIdleScale);
    }

    private bool TryFindDockEdge(PixelPoint point, out MainWindowDockEdge edge, out Screen screen) {
        screen = Screens.ScreenFromPoint(point) ?? Screens.Primary!;
        edge = MainWindowDockEdge.Left;
        if (!_dockEnabled || screen == null) return false;
        var area = screen.WorkingArea;
        var candidates = new[] {
            (Edge: MainWindowDockEdge.Left, Distance: Math.Abs(point.X - area.X)),
            (Edge: MainWindowDockEdge.Right, Distance: Math.Abs(area.Right - point.X)),
            (Edge: MainWindowDockEdge.Top, Distance: Math.Abs(point.Y - area.Y)),
            (Edge: MainWindowDockEdge.Bottom, Distance: Math.Abs(area.Bottom - point.Y)),
        };
        var nearest = candidates.OrderBy(c => c.Distance).First();
        if (nearest.Distance > DockDetectionPixels) return false;
        edge = nearest.Edge;
        return true;
    }

    private static bool IsPastDetachThreshold(PixelPoint point, MainWindowDockEdge edge, PixelRect area) {
        var inwardDistance = edge switch {
            MainWindowDockEdge.Left => point.X - area.X,
            MainWindowDockEdge.Right => area.Right - point.X,
            MainWindowDockEdge.Top => point.Y - area.Y,
            MainWindowDockEdge.Bottom => area.Bottom - point.Y,
            _ => 0,
        };
        return inwardDistance > DockDetachPixels;
    }

    private PixelPoint DockPositionFromCursor(MainWindowDockEdge edge, PixelRect area, double scaling, PixelPoint cursor) {
        var width = DockPixelWidth(edge, scaling);
        var height = DockPixelHeight(edge, scaling);
        return edge switch {
            MainWindowDockEdge.Left => new PixelPoint(area.X, Math.Clamp(cursor.Y - height / 2, area.Y, area.Bottom - height)),
            MainWindowDockEdge.Right => new PixelPoint(area.Right - width, Math.Clamp(cursor.Y - height / 2, area.Y, area.Bottom - height)),
            MainWindowDockEdge.Top => new PixelPoint(Math.Clamp(cursor.X - width / 2, area.X, area.Right - width), area.Y),
            MainWindowDockEdge.Bottom => new PixelPoint(Math.Clamp(cursor.X - width / 2, area.X, area.Right - width), area.Bottom - height),
            _ => new PixelPoint(area.X, area.Y),
        };
    }

    private PixelPoint DockPositionFromNormalized(MainWindowDockEdge edge, PixelRect area, double scaling, double normalized) {
        var width = DockPixelWidth(edge, scaling);
        var height = DockPixelHeight(edge, scaling);
        normalized = Math.Clamp(normalized, 0, 1);
        var x = area.X + (int)Math.Round(Math.Max(0, area.Width - width) * normalized);
        var y = area.Y + (int)Math.Round(Math.Max(0, area.Height - height) * normalized);
        return edge switch {
            MainWindowDockEdge.Left => new PixelPoint(area.X, y),
            MainWindowDockEdge.Right => new PixelPoint(area.Right - width, y),
            MainWindowDockEdge.Top => new PixelPoint(x, area.Y),
            MainWindowDockEdge.Bottom => new PixelPoint(x, area.Bottom - height),
            _ => new PixelPoint(area.X, area.Y),
        };
    }

    private double CalculateNormalizedDockPosition(MainWindowDockEdge edge, PixelRect area, double scaling, PixelPoint position) {
        var vertical = edge is MainWindowDockEdge.Left or MainWindowDockEdge.Right;
        var available = vertical
            ? area.Height - DockPixelHeight(edge, scaling)
            : area.Width - DockPixelWidth(edge, scaling);
        if (available <= 0) return 0;
        var offset = vertical ? position.Y - area.Y : position.X - area.X;
        return Math.Clamp(offset / (double)available, 0, 1);
    }

    private (PixelPoint Position, double Width, double Height) ClampExpandedBounds(
        Screen screen,
        PixelPoint requestedPosition,
        double requestedWidth,
        double requestedHeight) {
        var area = screen.WorkingArea;
        var size = LimitExpandedSize(screen, requestedWidth, requestedHeight);
        var physicalWidth = (int)Math.Round(size.Width * screen.Scaling);
        var physicalHeight = (int)Math.Round(size.Height * screen.Scaling);
        var maxX = Math.Max(area.X, area.Right - physicalWidth);
        var maxY = Math.Max(area.Y, area.Bottom - physicalHeight);
        return (
            new PixelPoint(Math.Clamp(requestedPosition.X, area.X, maxX), Math.Clamp(requestedPosition.Y, area.Y, maxY)),
            size.Width,
            size.Height);
    }

    private static (double Width, double Height) LimitExpandedSize(
        Screen screen,
        double requestedWidth,
        double requestedHeight) {
        var maxWidth = Math.Max(1, screen.WorkingArea.Width / screen.Scaling);
        var maxHeight = Math.Max(1, screen.WorkingArea.Height / screen.Scaling);
        var minWidth = Math.Min(ExpandedMinWidth, maxWidth);
        var minHeight = Math.Min(ExpandedMinHeight, maxHeight);
        return (
            Math.Clamp(requestedWidth, minWidth, maxWidth),
            Math.Clamp(requestedHeight, minHeight, maxHeight));
    }

    private bool TryGetInteractiveExpandedBounds(
        PixelPoint cursor,
        out (PixelPoint Position, double Width, double Height) target) {
        var screen = Screens.ScreenFromPoint(cursor) ?? Screens.Primary;
        if (screen == null) {
            target = default;
            return false;
        }

        var size = LimitExpandedSize(screen, _expandedWidth, _expandedHeight);
        var anchor = ExpandedDragAnchorInPixels(screen.Scaling, size.Width);
        target = (
            new PixelPoint(cursor.X - anchor.X, cursor.Y - anchor.Y),
            size.Width,
            size.Height);
        return true;
    }

    private void UpdateExpandedDragPosition(PixelPoint cursor) {
        if (!TryGetInteractiveExpandedBounds(cursor, out var target)) return;
        Position = target.Position;
    }

    private PixelPoint ExpandedDragAnchorInPixels(double scaling, double expandedWidth) {
        if (!_useTitleBarCenterDragAnchor) return _pointerGrabOffset;

        var centerOffsetX = _hasMeasuredTitleBarDragCenter ? _titleBarDragCenterOffsetX : 0;
        var centerY = _hasMeasuredTitleBarDragCenter
            ? _titleBarDragCenterY
            : ExpandedTitleBarHeight / 2;
        return new PixelPoint(
            (int)Math.Round((expandedWidth / 2 + centerOffsetX) * scaling),
            (int)Math.Round(centerY * scaling));
    }

    private void MeasureTitleBarDragCenter() {
        var titleBar = this.FindControl<Border>(nameof(_titleBarDrag));
        if (titleBar == null || titleBar.Bounds.Width <= 0 || titleBar.Bounds.Height <= 0) return;

        var center = titleBar.TranslatePoint(
            new Point(titleBar.Bounds.Width / 2, titleBar.Bounds.Height / 2),
            this);
        if (!center.HasValue) return;

        var windowWidth = Bounds.Width > 0 ? Bounds.Width : CurrentWindowWidth();
        if (!double.IsFinite(windowWidth) || windowWidth <= 0) return;
        _titleBarDragCenterOffsetX = center.Value.X - windowWidth / 2;
        _titleBarDragCenterY = center.Value.Y;
        _hasMeasuredTitleBarDragCenter = true;
    }

    private void SaveCurrentExpandedBounds() {
        if (_dockState != DockState.Expanded) return;
        SaveExpandedBounds(Position, CurrentWindowWidth(), CurrentWindowHeight());
    }

    private void SaveExpandedBounds(PixelPoint position, double width, double height) {
        _expandedPosition = position;
        _expandedWidth = Math.Max(ExpandedMinWidth, width);
        _expandedHeight = Math.Max(ExpandedMinHeight, height);
        _hasExpandedBounds = true;
    }

    private void UpdateDockPersistenceFields(AppSettings settings) {
        settings.MainWindowDocked = true;
        settings.MainWindowDockEdge = _dockEdge;
        settings.MainWindowDockPosition = Math.Clamp(_dockNormalizedPosition, 0, 1);
        settings.MainWindowDockWorkAreaX = _dockWorkArea.X;
        settings.MainWindowDockWorkAreaY = _dockWorkArea.Y;
        settings.MainWindowDockWorkAreaWidth = _dockWorkArea.Width;
        settings.MainWindowDockWorkAreaHeight = _dockWorkArea.Height;
    }

    private void PersistRuntimeWindowState() {
        CopyRuntimeWindowStateTo(_settings);
        if (_persistWindowStateAsync == null) return;
        _ = _persistWindowStateAsync(_settings.Clone());
    }

    private void ScheduleExpandedBoundsSave() {
        if (_dockState != DockState.Expanded || !IsVisible || _isSynchronizingExpandedStartupBounds) return;
        _expandedBoundsSaveTimer?.Stop();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { });
        timer.Tick += (_, _) => {
            timer.Stop();
            if (!ReferenceEquals(_expandedBoundsSaveTimer, timer)) return;
            _expandedBoundsSaveTimer = null;
            if (_dockState != DockState.Expanded || _titleDragging || _windowTransition?.IsTransitioning == true) return;
            SaveCurrentExpandedBounds();
            PersistRuntimeWindowState();
        };
        _expandedBoundsSaveTimer = timer;
        timer.Start();
    }

    private void SetDockedVisualState(bool allowHitTesting = true) {
        if (_expandedSurface != null) {
            _expandedSurface.Opacity = 0;
            _expandedSurface.IsHitTestVisible = false;
        }
        if (_dockLayer != null) {
            _dockLayer.Opacity = 1;
            _dockLayer.IsHitTestVisible = allowHitTesting;
        }
        UpdateDockShape(_dockEdge);
        ApplyDockClip();
    }

    private void SetExpandedVisualState() {
        ClearDockClip();
        if (_expandedSurface != null) {
            _expandedSurface.Width = double.NaN;
            _expandedSurface.Height = double.NaN;
            _expandedSurface.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _expandedSurface.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            _expandedSurface.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            SetExpandedSurfaceScale(1, 1);
            _expandedSurface.Opacity = 1;
            _expandedSurface.IsHitTestVisible = true;
        }
        if (_dockLayer != null) {
            _dockLayer.Opacity = 0;
            _dockLayer.IsHitTestVisible = false;
        }
    }

    private void UpdateDockShape(MainWindowDockEdge edge) {
        if (_dockShape != null) _dockShape.Data = _dockGeometries[edge];
        if (_dockIconSurface != null) {
            _dockIconSurface.Width = _dockSize;
            _dockIconSurface.Height = _dockSize;
        }
        if (_dockContent != null) {
            _dockContent.Width = _dockSize;
            _dockContent.Height = _dockSize;
        }
        if (_dockVisual != null) {
            _dockVisual.RenderTransformOrigin = edge switch {
                MainWindowDockEdge.Left => new RelativePoint(0, 0.5, RelativeUnit.Relative),
                MainWindowDockEdge.Right => new RelativePoint(1, 0.5, RelativeUnit.Relative),
                MainWindowDockEdge.Top => new RelativePoint(0.5, 0, RelativeUnit.Relative),
                MainWindowDockEdge.Bottom => new RelativePoint(0.5, 1, RelativeUnit.Relative),
                _ => new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            };
        }
    }

    private void AnimateDockVisualScale(double targetScale) {
        if (_dockVisual?.RenderTransform is not ScaleTransform transform) return;

        _dockScaleAnimation?.Cancel();
        var fromScale = transform.ScaleX;
        if (Math.Abs(fromScale - targetScale) <= 0.0001) {
            _dockScaleAnimation = null;
            SetDockVisualScale(targetScale);
            return;
        }

        FrameAnimation? animation = null;
        animation = new FrameAnimation(
            this,
            MotionPreferences.FastDuration,
            new CubicEaseOut(),
            progress => SetDockVisualScale(Lerp(fromScale, targetScale, progress)),
            () => {
                if (ReferenceEquals(_dockScaleAnimation, animation)) _dockScaleAnimation = null;
            });
        _dockScaleAnimation = animation;
        animation.Start();
    }

    private void SetDockVisualScale(double scale) {
        if (_dockVisual?.RenderTransform is not ScaleTransform transform) return;
        if (Math.Abs(transform.ScaleX - scale) > 0.0001) transform.ScaleX = scale;
        if (Math.Abs(transform.ScaleY - scale) > 0.0001) transform.ScaleY = scale;
    }

    private void ApplyDockClip() {
        if (_dockLayer != null) _dockLayer.Clip = _dockGeometries[_dockEdge];
    }

    private void ClearDockClip() {
        if (_dockLayer != null) _dockLayer.Clip = null;
    }

    private static IReadOnlyDictionary<MainWindowDockEdge, Geometry> BuildDockGeometries(double size) =>
        Enum.GetValues<MainWindowDockEdge>()
            .ToDictionary(edge => edge, edge => Geometry.Parse(DockGeometryData(edge, size)));

    private static string DockGeometryData(MainWindowDockEdge edge, double size) {
        var transition = DockTransitionSize;
        var radius = Math.Min(10, size / 2);
        var full = size + (transition * 2);
        var farTransition = size + transition;
        var nearCurve = transition + radius;
        var farCurve = farTransition - radius;
        var innerFar = size - transition;

        return edge switch {
            MainWindowDockEdge.Left => FormattableString.Invariant(
                $"M 0,0 C 0,3 2,{transition} {transition},{transition} L {size - radius},{transition} Q {size},{transition} {size},{nearCurve} L {size},{farCurve} Q {size},{farTransition} {size - radius},{farTransition} L {transition},{farTransition} C 2,{farTransition} 0,{farTransition + 2} 0,{full} Z"),
            MainWindowDockEdge.Right => FormattableString.Invariant(
                $"M {size},0 C {size},3 {size - 2},{transition} {innerFar},{transition} L {radius},{transition} Q 0,{transition} 0,{nearCurve} L 0,{farCurve} Q 0,{farTransition} {radius},{farTransition} L {innerFar},{farTransition} C {size - 2},{farTransition} {size},{farTransition + 2} {size},{full} Z"),
            MainWindowDockEdge.Top => FormattableString.Invariant(
                $"M 0,0 C 3,0 {transition},2 {transition},{transition} L {transition},{size - radius} Q {transition},{size} {nearCurve},{size} L {farCurve},{size} Q {farTransition},{size} {farTransition},{size - radius} L {farTransition},{transition} C {farTransition},2 {farTransition + 2},0 {full},0 Z"),
            MainWindowDockEdge.Bottom => FormattableString.Invariant(
                $"M 0,{size} C 3,{size} {transition},{size - 2} {transition},{innerFar} L {transition},{radius} Q {transition},0 {nearCurve},0 L {farCurve},0 Q {farTransition},0 {farTransition},{radius} L {farTransition},{innerFar} C {farTransition},{size - 2} {farTransition + 2},{size} {full},{size} Z"),
            _ => FormattableString.Invariant($"M 0,0 L {size},0 L {size},{size} L 0,{size} Z"),
        };
    }

    private double DockWidth(MainWindowDockEdge edge) =>
        edge is MainWindowDockEdge.Top or MainWindowDockEdge.Bottom
            ? _dockSize + (DockTransitionSize * 2)
            : _dockSize;

    private double DockHeight(MainWindowDockEdge edge) =>
        edge is MainWindowDockEdge.Left or MainWindowDockEdge.Right
            ? _dockSize + (DockTransitionSize * 2)
            : _dockSize;

    private int DockPixelWidth(MainWindowDockEdge edge, double scaling) =>
        Math.Max(1, (int)Math.Round(DockWidth(edge) * scaling));

    private int DockPixelHeight(MainWindowDockEdge edge, double scaling) =>
        Math.Max(1, (int)Math.Round(DockHeight(edge) * scaling));

    private double CurrentWindowWidth() => double.IsFinite(Width) && Width > 0 ? Width : Bounds.Width;
    private double CurrentWindowHeight() => double.IsFinite(Height) && Height > 0 ? Height : Bounds.Height;
    private PixelPoint PointerScreenPosition(PointerEventArgs e) {
        if (GetCursorPos(out var cursor)) return new PixelPoint(cursor.X, cursor.Y);
        var point = e.GetPosition(this);
        return new PixelPoint(
            Position.X + (int)Math.Round(point.X * RenderScaling),
            Position.Y + (int)Math.Round(point.Y * RenderScaling));
    }
    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
    private static double Distance(PixelPoint a, PixelPoint b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private bool IsDockGestureActive() => _titleDragging || _dockLongPressReady;

    private void StopWindowTransitionForDocking() {
        _windowTransition?.Reset();
    }

    private void DisposeDockingInteraction() {
        _dockAnimation?.Cancel();
        _dockAnimation = null;
        _dockScaleAnimation?.Cancel();
        _dockScaleAnimation = null;
        _dockLongPressTimer?.Stop();
        _dockLongPressTimer = null;
        _expandedBoundsSaveTimer?.Stop();
        _expandedBoundsSaveTimer = null;
        _restoreCompletion = null;
        _dockAnimationCompleted = null;
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint {
        public int X;
        public int Y;
    }
}
