# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This repository contains a Windows desktop memo app built with .NET 8 and Avalonia UI 11.1. The app targets `net8.0-windows` and uses Windows-specific APIs for the tray icon (`System.Windows.Forms.NotifyIcon`) and global hotkeys (`RegisterHotKey` through `user32.dll`), so development and runtime verification should be done on Windows with the .NET 8 SDK installed.

The main project is `Memo-avalonia/Memo.csproj`. There is no solution file and no test project currently checked in.

## Common Commands

Run commands from the repository root unless noted otherwise.

```bash
# Restore packages
dotnet restore Memo-avalonia/Memo.csproj

# Run the desktop app
dotnet run --project Memo-avalonia/Memo.csproj

# Build Debug
dotnet build Memo-avalonia/Memo.csproj

# Publish a framework-dependent Windows x64 Release build
dotnet publish Memo-avalonia/Memo.csproj -c Release -r win-x64 --self-contained false

# Optional SDK formatting check; no dedicated repo lint target exists
dotnet format Memo-avalonia/Memo.csproj --verify-no-changes
```

Current test status: no test project exists, so `dotnet test` has nothing to run. If a test project is added later, prefer targeting that test project directly, for example:

```bash
dotnet test path/to/TestProject.csproj --filter FullyQualifiedName~TestName
```

## Runtime Data

The app persists local JSON data under `%AppData%/Memo/`:

- `memos.json` stores memo items.
- `settings.json` stores close-button behavior and hotkey settings.

Deleting these files resets the app to empty memos and default settings on next launch.

## Architecture

Startup flows through `Program.cs` into `App.axaml.cs`. `App` owns desktop lifetime wiring: it creates `MainWindow`, initializes `WindowsTrayIcon`, applies `GlobalHotkeyService`, loads settings asynchronously after initial display, and provides restore/hide helpers used by tray and hotkey actions.

`Views/MainWindow.axaml` defines the custom borderless rounded window and memo list UI. `Views/MainWindow.axaml.cs` owns most direct UI behavior: Enter vs Shift+Enter input handling, double-click edit, animated delete, custom titlebar move/minimize/close/pin actions, and show/hide transitions. The main window `DataContext` is `MainViewModel`.

`ViewModels/MainViewModel.cs` is the memo state boundary. It holds the observable memo collection, edit state, add/update/delete/reorder operations, and delegates persistence to `JsonMemoStorage`. Mutations fire-and-forget `SaveAsync`, so storage methods are protected with semaphores.

`Services/JsonMemoStorage.cs` and `Services/JsonSettingsStorage.cs` serialize data with `System.Text.Json` using camelCase and indented output. Both swallow persistence errors after writing to debug output, so callers do not currently surface storage failures to UI.

`Platform/Windows/GlobalHotkeyService.cs` is Windows-only. It maps `HotkeySetting` models to WinForms `Keys`, registers hotkeys against a hidden `NativeWindow`, and dispatches callbacks back to `MainWindow` methods.

`Views/SettingsWindow.axaml.cs` edits a clone of `AppSettings`, captures hotkeys from `KeyDown`, saves through a callback supplied by `App`, and then `App` reapplies settings to the main window and hotkey service.

`Views/TrayMenuWindow.axaml.cs` is a small custom tray menu shown near the working area corner. `Platform/Windows/WindowsTrayIcon.cs` owns the actual WinForms notify icon and calls into the Avalonia tray menu or main-window restore behavior.

`Components/Dialogs/CloseActionDialog.*` handles the first-run close-button behavior choice, while `Components/Dialogs/ConfirmDialog.*` handles reusable confirmation prompts.

`Behaviors/DragReorderManager.cs` deliberately isolates long-press drag reorder behavior from `MainWindow`. During drag it keeps the collection unchanged, hides the original item, renders a floating popup and placeholder, animates neighboring items with manual `DispatcherTimer` interpolation, and only calls `MainViewModel.MoveItem` when the drag ends. Preserve that invariant when changing reorder behavior.

`UI/WindowTransitionController.cs` provides reusable opacity/scale transitions for secondary windows. The main window currently has its own similar transition code because it coordinates with tray hide/show behavior.

## Development Notes

The project uses Avalonia compiled bindings by default (`AvaloniaUseCompiledBindingsByDefault=true`). When adding bindings in XAML, ensure the relevant `x:DataType` is correct or bindings may fail at build time.

The UI is intentionally custom-window and Windows-tray oriented. Cross-platform support would require replacing the Windows Forms tray icon, global hotkey implementation, and likely the project target framework.
