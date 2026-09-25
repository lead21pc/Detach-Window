# Explorer Workspace

A lightweight Windows utility for temporarily grouping several **File Explorer** windows into one movable workspace.

Select the Explorer windows you want, choose a layout, attach them, and move or resize the lead window. The rest stay arranged with it—without replacing Explorer, installing shell extensions, or introducing a custom file manager.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![Framework](https://img.shields.io/badge/.NET-8.0-512BD4)
![UI](https://img.shields.io/badge/UI-WPF-5C2D91)

## Why this exists

Working with several folders at once usually means repeatedly arranging Explorer windows by hand. Explorer Workspace keeps a temporary group together while leaving every window as a normal native Explorer window.

It is intentionally small in scope:

- no Explorer replacement;
- no embedded browser views;
- no custom tabs;
- no shell extension;
- no background service;
- no cloud component.

The app simply discovers existing Explorer windows and coordinates their position, size, minimize/restore state, and saved layout.

## Features

- **Attach multiple Explorer windows** into one temporary workspace.
- **Move the lead window** and keep the group arranged around it.
- **Resize the lead window** and propagate the layout while respecting the screen work area.
- Choose between:
  - **Auto Grid** — adaptive multi-row layout;
  - **Horizontal** — windows arranged side by side;
  - **Vertical** — windows stacked top to bottom.
- **Group minimize and restore** behavior.
- **Save** the current workspace layout locally.
- **Restore** open Explorer windows by folder path, with window title as a fallback.
- Automatically drops Explorer windows that have been closed.

## Requirements

- Windows 10 or newer
- .NET 8 SDK to build from source
- Standard File Explorer folder windows

This project is Windows-only because it uses Win32 APIs and WPF.

## Build

Clone the repository and build it with the .NET SDK:

```powershell
git clone https://github.com/lead21pc/Detach-Window.git
cd Detach-Window
dotnet build
```

## Run

```powershell
dotnet run
```

## Usage

1. Open at least two normal File Explorer folder windows.
2. Start Explorer Workspace.
3. Select the Explorer windows you want to group.
4. Choose **Auto Grid**, **Horizontal**, or **Vertical**.
5. Click **Attach**.
6. Move or resize the first selected window to reposition the workspace.
7. Use **Save** if you want to keep the current layout.
8. Click **Detach** when you want the Explorer windows to behave independently again.

## Saved layouts

Layouts are stored locally at:

```text
%LOCALAPPDATA%\ExplorerWorkspace\layout.json
```

When restoring a layout, the app:

1. refreshes the list of currently open Explorer windows;
2. matches saved windows by folder path;
3. falls back to the saved window title when the folder path no longer matches;
4. restores matching windows to their saved positions.

The layout file contains window metadata and geometry only. It is not a backup of files or Explorer state.

## How it works

Explorer Workspace keeps Explorer itself in control. It does not host, embed, or replace Explorer windows.

| Purpose | Implementation |
| --- | --- |
| Discover top-level windows | `EnumWindows` |
| Identify Explorer windows | `GetClassName`, `GetWindowText` |
| Resolve Explorer folder paths | `Shell.Application` |
| Read window geometry | `GetWindowRect` |
| Move and resize windows | `SetWindowPos` |
| Observe window changes | `SetWinEventHook` |
| Keep the group synchronized | WPF `DispatcherTimer` |

The application remains local to the current Windows session and does not send Explorer data anywhere.

## Current scope

Explorer Workspace is a focused desktop utility rather than a full window manager.

Known scope boundaries:

- only normal filesystem-backed File Explorer windows are detected;
- special shell locations that do not resolve to filesystem paths are ignored;
- saved layouts only reconnect to Explorer windows that are already open;
- layout synchronization depends on normal Win32 window behavior;
- there is currently no installer or packaged release workflow in the repository.

## Project status

This is a small experimental utility built around a practical workflow problem. The codebase is intentionally compact so the window-management behavior remains easy to inspect and modify.

Bug reports and focused improvements are welcome, especially around multi-monitor behavior, Explorer lifecycle edge cases, and layout handling.
