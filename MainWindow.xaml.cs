using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ExplorerWorkspace;

public partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 520;
    private const int DefaultWindowHeight = 340;
    private const int LayoutSpacing = 12;

    private readonly ObservableCollection<ExplorerWindow> _windows = new();
    private readonly DispatcherTimer _syncTimer = new();
    private readonly string _layoutFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExplorerWorkspace",
        "layout.json");

    private GroupState? _group;
    private WinEventDelegate? _eventDelegate;
    private IntPtr _eventHook;
    private bool _isSyncing;
    private DateTime _ignoreGroupEventsUntilUtc;

    public MainWindow()
    {
        InitializeComponent();
        ExplorerList.ItemsSource = _windows;

        _syncTimer.Interval = TimeSpan.FromMilliseconds(80);
        _syncTimer.Tick += (_, _) => SyncGroup();

        _eventDelegate = (_, eventType, hwnd, idObject, _, _, _) =>
        {
            if (_group is null || idObject != Win32.OBJID_WINDOW || !_group.Contains(hwnd))
                return;

            Dispatcher.Invoke(() => HandleGroupEvent(eventType, hwnd));
        };
        _eventHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND,
            Win32.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _eventDelegate,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        RefreshExplorerWindows();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshExplorerWindows();

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var selected = ExplorerList.SelectedItems.Cast<ExplorerWindow>()
            .Where(w => Win32.IsWindow(w.Hwnd))
            .DistinctBy(w => w.Hwnd)
            .ToList();

        if (selected.Count < 2)
        {
            Status("Select at least 2 Explorer windows first.");
            return;
        }

        var main = selected[0];
        if (!Win32.GetWindowRect(main.Hwnd, out var mainRect))
        {
            Status("Could not read the main Explorer window position.");
            return;
        }

        _group = new GroupState(
            main,
            selected,
            mainRect,
            selected.ToDictionary(w => w.Hwnd, w =>
            {
                Win32.GetWindowRect(w.Hwnd, out var rect);
                return rect;
            }),
            SelectedLayout());

        ApplyLayout(_group, mainRect);
        RestoreGroup(_group, main.Hwnd, activate: true);
        _syncTimer.Start();
        Status($"Attached {selected.Count} Explorer windows as one workspace. Move the first selected window to move the group.");
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        DetachGroup("Detached. Explorer windows are independent again.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_group is null)
        {
            Status("Attach a group before saving.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_layoutFile)!);
        var saved = new SavedLayout(
            _group.Layout,
            _group.Members.Select(w =>
            {
                Win32.GetWindowRect(w.Hwnd, out var rect);
                return new SavedWindow(w.Title, w.Folder, rect);
            }).ToList());
        File.WriteAllText(_layoutFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        Status($"Saved layout to {_layoutFile}");
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_layoutFile))
        {
            Status("No saved layout found.");
            return;
        }

        RefreshExplorerWindows();
        var saved = JsonSerializer.Deserialize<SavedLayout>(File.ReadAllText(_layoutFile));
        if (saved is null || saved.Windows.Count == 0)
        {
            Status("Saved layout file is empty or invalid.");
            return;
        }

        var matched = new List<ExplorerWindow>();
        foreach (var savedWindow in saved.Windows)
        {
            var match = _windows.FirstOrDefault(w =>
                string.Equals(w.Folder, savedWindow.Folder, StringComparison.OrdinalIgnoreCase));
            if (match is null || matched.Any(w => w.Hwnd == match.Hwnd))
                continue;

            matched.Add(match);
            Win32.SetWindowPos(
                match.Hwnd,
                IntPtr.Zero,
                savedWindow.Rect.Left,
                savedWindow.Rect.Top,
                savedWindow.Rect.Width,
                savedWindow.Rect.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        if (matched.Count >= 2)
        {
            Win32.GetWindowRect(matched[0].Hwnd, out var mainRect);
            _group = new GroupState(
                matched[0],
                matched,
                mainRect,
                matched.ToDictionary(w => w.Hwnd, w =>
                {
                    Win32.GetWindowRect(w.Hwnd, out var rect);
                    return rect;
                }),
                saved.Layout);
            ApplyLayout(_group, mainRect);
            RestoreGroup(_group, matched[0].Hwnd, activate: true);
            _syncTimer.Start();
        }

        Status($"Restored {matched.Count} matching Explorer windows.");
    }

    private void RefreshExplorerWindows()
    {
        var previous = ExplorerList.SelectedItems.Cast<ExplorerWindow>().Select(w => w.Hwnd).ToHashSet();
        _windows.Clear();
        foreach (var window in ExplorerScanner.FindExplorerWindows())
            _windows.Add(window);

        foreach (var window in _windows.Where(w => previous.Contains(w.Hwnd)))
            ExplorerList.SelectedItems.Add(window);

        Status($"Detected {_windows.Count} Explorer windows.");
    }

    private void SyncGroup()
    {
        if (_group is null)
            return;

        if (DateTime.UtcNow < _ignoreGroupEventsUntilUtc)
            return;

        var dead = _group.Members.Where(w => !Win32.IsWindow(w.Hwnd)).ToList();
        foreach (var d in dead)
        {
            _group.Members.Remove(d);
            _group.LastRects.Remove(d.Hwnd);
            _group.OriginalRects.Remove(d.Hwnd);
        }

        if (_group.Members.Count < 1)
        {
            DetachGroup("All Explorer windows closed.");
            return;
        }

        if (!Win32.IsWindow(_group.Main.Hwnd))
        {
            if (_group.Members.Count < 2)
            {
                DetachGroup("Too few windows remaining.");
                return;
            }

            _group = _group.PromoteNewMain();
            Status($"Main window closed. Promoted '{_group.Main.Title}' as new anchor.");
        }

        if (!Win32.GetWindowRect(_group.Main.Hwnd, out var currentMain))
            return;

        if (_group.MainLockedSize.Width != currentMain.Width || _group.MainLockedSize.Height != currentMain.Height)
        {
            LockMainSize(_group, currentMain);
            return;
        }

        var allMinimized = _group.Members.All(w => Win32.IsIconic(w.Hwnd));
        if (allMinimized)
        {
            if (_group.LastMinimized)
                return;

            var activeHwnd = Win32.GetForegroundWindow();
            if (_group.Contains(activeHwnd))
                RestoreGroup(_group, activeHwnd, activate: true);
            return;
        }

        var foreground = Win32.GetForegroundWindow();
        if (_group.Contains(foreground) && foreground != _group.LastForegroundHwnd && !Win32.IsIconic(foreground))
            ActivateGroup(_group, foreground);

        if (currentMain.Equals(_group.LastMainRect) || Win32.IsIconic(_group.Main.Hwnd))
            return;

        ReflowGroup(_group, currentMain);
    }

    private void HandleGroupEvent(uint eventType, IntPtr hwnd)
    {
        if (_group is null || _isSyncing)
            return;

        if (DateTime.UtcNow < _ignoreGroupEventsUntilUtc)
            return;

        if (!Win32.IsWindow(hwnd))
            return;

        switch (eventType)
        {
            case Win32.EVENT_SYSTEM_MINIMIZESTART:
                MinimizeGroup(_group);
                break;
            case Win32.EVENT_SYSTEM_FOREGROUND:
                if (!_group.Contains(hwnd))
                    break;

                if (_group.Members.All(w => Win32.IsIconic(w.Hwnd)))
                    RestoreGroup(_group, hwnd, activate: true);
                else
                    ActivateGroup(_group, hwnd);
                break;
            case Win32.EVENT_OBJECT_LOCATIONCHANGE when hwnd == _group.Main.Hwnd:
                SyncGroup();
                break;
            case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                UpdateKnownRect(_group, hwnd);
                break;
        }
    }

    private void ApplyLayout(GroupState group, Win32.RECT mainRect)
    {
        var wasSynchronizing = _isSyncing;
        _isSyncing = true;
        try
        {
            ReflowGroup(group, mainRect);
        }
        finally
        {
            _isSyncing = wasSynchronizing;
        }
    }

    private void ReflowGroup(GroupState group, Win32.RECT mainRect)
    {
        var rects = BuildLayout(group, mainRect);
        try
        {
            for (var i = 0; i < group.Members.Count; i++)
            {
                var window = group.Members[i];
                if (!Win32.IsWindow(window.Hwnd))
                    continue;

                var rect = rects[i];
                Win32.SetWindowPos(
                    window.Hwnd,
                    IntPtr.Zero,
                    rect.Left,
                    rect.Top,
                    rect.Width,
                    rect.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                group.LastRects[window.Hwnd] = rect;
            }

            group.LastMainRect = group.LastRects.GetValueOrDefault(group.Main.Hwnd, mainRect);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void LockMainSize(GroupState group, Win32.RECT currentMain)
    {
        if (_isSyncing)
            return;

        var prev = _isSyncing;
        _isSyncing = true;
        try
        {
            group.UpdateLockedSize(currentMain);
            ReflowGroup(group, currentMain);
        }
        finally
        {
            _isSyncing = prev;
        }
    }

    private static void UpdateKnownRect(GroupState group, IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect))
            return;

        group.LastRects[hwnd] = rect;
        if (hwnd == group.Main.Hwnd)
            group.LastMainRect = rect;
    }

    private void MinimizeGroup(GroupState group)
    {
        if (_isSyncing)
            return;

        _isSyncing = true;
        _ignoreGroupEventsUntilUtc = DateTime.UtcNow.AddMilliseconds(300);
        try
        {
            foreach (var window in group.Members.Where(w => Win32.IsWindow(w.Hwnd)))
                Win32.ShowWindow(window.Hwnd, Win32.SW_MINIMIZE);
            group.LastMinimized = true;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void RestoreGroup(GroupState group, IntPtr activatingHwnd, bool activate)
    {
        if (_isSyncing)
            return;

        _isSyncing = true;
        _ignoreGroupEventsUntilUtc = DateTime.UtcNow.AddMilliseconds(300);
        try
        {
            foreach (var window in group.Members.Where(w => Win32.IsWindow(w.Hwnd)))
                Win32.ShowWindow(window.Hwnd, Win32.SW_RESTORE);

            if (activate)
                BringGroupToFront(group, activatingHwnd);

            RefreshKnownRects(group);
            group.LastMinimized = false;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void ActivateGroup(GroupState group, IntPtr activatingHwnd)
    {
        if (_isSyncing)
            return;

        _isSyncing = true;
        _ignoreGroupEventsUntilUtc = DateTime.UtcNow.AddMilliseconds(200);
        try
        {
            BringGroupToFront(group, activatingHwnd);
            RefreshKnownRects(group);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private static void BringGroupToFront(GroupState group, IntPtr activatingHwnd)
    {
        var target = group.Contains(activatingHwnd) ? activatingHwnd : group.Main.Hwnd;
        Win32.ShowWindow(target, Win32.SW_SHOW);
        Win32.SetForegroundWindow(target);
        group.LastForegroundHwnd = target;
        group.LastMinimized = false;
    }

    private static void RefreshKnownRects(GroupState group)
    {
        foreach (var window in group.Members.Where(w => Win32.IsWindow(w.Hwnd)))
            UpdateKnownRect(group, window.Hwnd);
    }

    private static List<Win32.RECT> BuildLayout(GroupState group, Win32.RECT main)
    {
        if (group.Layout == LayoutKind.Horizontal)
        {
            return BuildLinearLayout(group.Members.Count, main, horizontal: true);
        }

        if (group.Layout == LayoutKind.Vertical)
        {
            return BuildLinearLayout(group.Members.Count, main, horizontal: false);
        }

        return BuildFlowLayout(group, main);
    }

    private static List<Win32.RECT> BuildLinearLayout(int count, Win32.RECT main, bool horizontal)
    {
        var width = DefaultWindowWidth;
        var height = DefaultWindowHeight;
        var left = main.Left;
        var top = main.Top;
        var rects = new List<Win32.RECT>();

        for (var i = 0; i < count; i++)
        {
            if (horizontal)
                rects.Add(Win32.RECT.From(left + (width + LayoutSpacing) * i, top, width, height));
            else
                rects.Add(Win32.RECT.From(left, top + (height + LayoutSpacing) * i, width, height));
        }

        return rects;
    }

    private static List<Win32.RECT> BuildFlowLayout(GroupState group, Win32.RECT main)
    {
        var rects = new List<Win32.RECT>();
        var left = main.Left;
        var top = main.Top;
        var workArea = Win32.GetWorkArea(main);
        var targetCols = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(group.Members.Count)));
        var rightBoundary = Math.Max(left + DefaultWindowWidth, workArea.Right);
        var maxWidth = Math.Max(240, rightBoundary - left);
        var cursorX = left;
        var cursorY = top;
        var rowHeight = 0;

        for (var i = 0; i < group.Members.Count; i++)
        {
            var window = group.Members[i];
            if (!group.LastRects.TryGetValue(window.Hwnd, out var sizeRect))
                Win32.GetWindowRect(window.Hwnd, out sizeRect);

            var width = Math.Clamp(sizeRect.Width, 240, maxWidth);
            var height = Math.Clamp(sizeRect.Height, 180, DefaultWindowHeight + 220);

            var itemsInRow = i % targetCols;
            if (i > 0 && (itemsInRow == 0 || (cursorX > left && cursorX + width > rightBoundary)))
            {
                cursorX = left;
                cursorY += rowHeight + LayoutSpacing;
                rowHeight = 0;
            }

            rects.Add(Win32.RECT.From(cursorX, cursorY, width, height));
            cursorX += width + LayoutSpacing;
            rowHeight = Math.Max(rowHeight, height);
        }

        return rects;
    }

    private LayoutKind SelectedLayout()
    {
        return LayoutBox.SelectedIndex switch
        {
            1 => LayoutKind.Horizontal,
            2 => LayoutKind.Vertical,
            _ => LayoutKind.AutoGrid
        };
    }

    private void DetachGroup(string message)
    {
        _syncTimer.Stop();
        _group = null;
        Status(message);
    }

    private void Status(string message) => StatusText.Text = message;

    protected override void OnClosed(EventArgs e)
    {
        if (_eventHook != IntPtr.Zero)
            Win32.UnhookWinEvent(_eventHook);
        base.OnClosed(e);
    }
}

public enum LayoutKind
{
    AutoGrid,
    Horizontal,
    Vertical
}

public sealed record ExplorerWindow(IntPtr Hwnd, string Title, string Folder)
{
    public string HwndHex => $"0x{Hwnd.ToInt64():X}";
}

public sealed class GroupState
{
    public GroupState(
        ExplorerWindow main,
        List<ExplorerWindow> members,
        Win32.RECT lastMainRect,
        Dictionary<IntPtr, Win32.RECT> originalRects,
        LayoutKind layout)
    {
        Main = main;
        Members = members;
        LastMainRect = lastMainRect;
        OriginalRects = originalRects;
        Layout = layout;
        LastRects = new Dictionary<IntPtr, Win32.RECT>(originalRects);
        MainLockedSize = new Win32.RECT(0, 0, lastMainRect.Width, lastMainRect.Height);
    }

    public ExplorerWindow Main { get; }
    public List<ExplorerWindow> Members { get; }
    public Win32.RECT LastMainRect { get; set; }
    public Dictionary<IntPtr, Win32.RECT> OriginalRects { get; }
    public Dictionary<IntPtr, Win32.RECT> LastRects { get; }
    public LayoutKind Layout { get; }
    public Win32.RECT MainLockedSize { get; set; }
    public bool LastMinimized { get; set; }
    public IntPtr LastForegroundHwnd { get; set; }

    public bool Contains(IntPtr hwnd) => Members.Any(w => w.Hwnd == hwnd);

    public void UpdateLockedSize(Win32.RECT newMain)
    {
        MainLockedSize = new Win32.RECT(0, 0, newMain.Width, newMain.Height);
        LastMainRect = newMain;
        LastRects[Main.Hwnd] = newMain;
    }

    public GroupState PromoteNewMain()
    {
        var nextMain = Members.First();
        Win32.GetWindowRect(nextMain.Hwnd, out var newMainRect);
        var promoted = new GroupState(
            nextMain,
            Members.ToList(),
            newMainRect,
            new Dictionary<IntPtr, Win32.RECT>(LastRects),
            Layout);

        promoted.MainLockedSize = MainLockedSize;
        promoted.LastMinimized = LastMinimized;
        promoted.LastForegroundHwnd = LastForegroundHwnd;
        return promoted;
    }
}

public sealed record SavedLayout(LayoutKind Layout, List<SavedWindow> Windows);

public sealed record SavedWindow(string Title, string Folder, Win32.RECT Rect);

public static class ExplorerScanner
{
    public static List<ExplorerWindow> FindExplorerWindows()
    {
        var folderByHwnd = GetShellFolders();
        var result = new List<ExplorerWindow>();

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd))
                return true;

            var className = Win32.GetClassNameText(hwnd);
            if (className is not "CabinetWClass" and not "ExploreWClass")
                return true;

            var title = Win32.GetWindowTextText(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                title = "Explorer";

            if (!folderByHwnd.TryGetValue(hwnd, out var folder) || string.IsNullOrWhiteSpace(folder))
                return true;

            if (!IsFilesystemFolder(folder))
                return true;

            result.Add(new ExplorerWindow(hwnd, title, folder));
            return true;
        }, IntPtr.Zero);

        return result.OrderBy(w => w.Title).ToList();
    }

    private static Dictionary<IntPtr, string> GetShellFolders()
    {
        var result = new Dictionary<IntPtr, string>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return result;

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (var window in shell.Windows())
            {
                try
                {
                    var hwnd = new IntPtr((long)window.HWND);
                    string path = window.Document.Folder.Self.Path;
                    if (IsFilesystemFolder(path))
                        result[hwnd] = path;
                }
                catch
                {
                    // Some Shell windows do not expose a filesystem folder.
                }
            }
        }
        catch
        {
            return result;
        }

        return result;
    }

    private static bool IsFilesystemFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("::", StringComparison.Ordinal) || path.StartsWith("{", StringComparison.Ordinal))
            return false;

        return Path.IsPathRooted(path);
    }
}

public delegate void WinEventDelegate(
    IntPtr hWinEventHook,
    uint eventType,
    IntPtr hwnd,
    int idObject,
    int idChild,
    uint dwEventThread,
    uint dwmsEventTime);

public static partial class Win32
{
    public const int OBJID_WINDOW = 0;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WINEVENT_SKIPOWNPROCESS = 2;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public static RECT GetWorkArea(RECT rect)
    {
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.rcWork;

        return new RECT(
            (int)SystemParameters.WorkArea.Left,
            (int)SystemParameters.WorkArea.Top,
            (int)SystemParameters.WorkArea.Right,
            (int)SystemParameters.WorkArea.Bottom);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public static string GetClassNameText(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static string GetWindowTextText(IntPtr hwnd)
    {
        var builder = new StringBuilder(512);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public readonly record struct RECT(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public static RECT From(int left, int top, int width, int height)
        {
            return new RECT(left, top, left + width, top + height);
        }
    }
}
