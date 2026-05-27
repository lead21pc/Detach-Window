# Explorer Workspace

Local-only Windows 10 utility for temporarily grouping existing File Explorer windows.

This app does not replace Explorer, embed Explorer, create tabs, install shell extensions, or implement a custom file manager. It only enumerates normal Explorer windows and moves/resizes them with Win32 APIs.

## Build

```powershell
dotnet build
```

## Run

```powershell
dotnet run
```

## Use

1. Open two to four normal File Explorer folder windows.
2. Start this app.
3. Select the Explorer windows in the list.
4. Choose `Auto Grid`, `Horizontal`, or `Vertical`.
5. Click `Attach`.
6. Move or resize the first selected Explorer window. The others follow.
7. Click `Detach` to stop synchronization.

`Save` writes a small JSON layout to:

```text
%LOCALAPPDATA%\ExplorerWorkspace\layout.json
```

`Restore` matches currently open Explorer windows by folder path first, then title, and reapplies the saved positions.

## Implementation Notes

- Explorer detection: `EnumWindows`, `GetClassName`, `GetWindowText`, Shell.Application folder lookup.
- Movement/layout: `GetWindowRect`, `SetWindowPos`.
- Change detection: `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` plus a short WPF timer.
- Scope: single-user, local machine, Windows-only MVP.
