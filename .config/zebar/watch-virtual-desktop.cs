// Build with .NET Framework csc.exe /target:exe watch-virtual-desktop.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class WatchVirtualDesktop
{
    private const string DesktopKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const string BarTitle = "Zebar - wallpaper-bar / bar";
    private const int NotifyLastSet = 4;
    private const uint SlideFlags = 0x215; // No activation, resize, or z-order change.
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int length);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect bounds);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key,
        bool watchSubtree, int filter, SafeWaitHandle notification, bool asynchronous);

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int length);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    private static bool IsVisibleAppWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || window == GetShellWindow() ||
            !IsWindowVisible(window) || IsIconic(window))
            return false;

        const int extendedStyle = -20;
        const int toolOrNoActivate = 0x08000080;
        if ((GetWindowLong(window, extendedStyle) & toolOrNoActivate) != 0) return false;

        const int cloakedAttribute = 14;
        int cloaked;
        if (DwmGetWindowAttribute(window, cloakedAttribute, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        switch (className.ToString())
        {
            case "WorkerW":
            case "Progman":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
                return false;
            case "Tauri Window":
                var title = new StringBuilder(128);
                GetWindowText(window, title, title.Capacity);
                return title.ToString() != BarTitle;
            default:
                return true;
        }
    }

    private static bool TryGetVisibleBounds(IntPtr window, out Rect bounds)
    {
        const int extendedFrameBounds = 9;
        return (DwmGetWindowAttribute(window, extendedFrameBounds, out bounds, Marshal.SizeOf(typeof(Rect))) == 0
                || GetWindowRect(window, out bounds)) &&
            bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private static bool FillsMonitor(Rect bounds, IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        // Compare the visible frame against the physical monitor, including the dock and taskbar area.
        const int tolerance = 2;
        return Math.Abs(bounds.Left - info.Bounds.Left) <= tolerance &&
            Math.Abs(bounds.Top - info.Bounds.Top) <= tolerance &&
            Math.Abs(bounds.Right - info.Bounds.Right) <= tolerance &&
            Math.Abs(bounds.Bottom - info.Bounds.Bottom) <= tolerance;
    }

    private static IntPtr FullscreenMonitor(IntPtr foreground)
    {
        Rect bounds;
        if (IsVisibleAppWindow(foreground) && TryGetVisibleBounds(foreground, out bounds))
        {
            var monitor = MonitorFromWindow(foreground, 0);
            return FillsMonitor(bounds, monitor) ? monitor : IntPtr.Zero;
        }

        IntPtr fullscreenMonitor;
        FindExposedFullscreen(IntPtr.Zero, out fullscreenMonitor);
        return fullscreenMonitor;
    }

    private static IntPtr FindExposedFullscreen(IntPtr targetMonitor, out IntPtr fullscreenMonitor)
    {
        // Minimizing a launcher can expose an app without passing foreground focus to it.
        // Only consider the first visible app on each monitor, in stacking order.
        fullscreenMonitor = IntPtr.Zero;
        Rect bounds;
        var coveredMonitors = new HashSet<IntPtr>();
        var visited = new HashSet<IntPtr>();
        var desktop = GetShellWindow();
        for (var window = GetTopWindow(IntPtr.Zero);
            window != IntPtr.Zero && window != desktop && visited.Add(window);
            window = GetWindow(window, 2)) // GW_HWNDNEXT
        {
            if (!IsVisibleAppWindow(window) || !TryGetVisibleBounds(window, out bounds)) continue;
            var monitor = MonitorFromWindow(window, 0);
            if (targetMonitor != IntPtr.Zero && monitor != targetMonitor) continue;
            if (monitor == IntPtr.Zero || coveredMonitors.Contains(monitor)) continue;
            if (FillsMonitor(bounds, monitor))
            {
                fullscreenMonitor = monitor;
                return window;
            }

            // Untitled helper windows should not obscure fullscreen content in this search.
            var title = new StringBuilder(2);
            if (GetWindowText(window, title, title.Capacity) != 0) coveredMonitors.Add(monitor);
        }
        return IntPtr.Zero;
    }

    private static void ReadFocusRequests()
    {
        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            var fields = line.Split(' ');
            Point point;
            if (fields.Length != 3 || fields[0] != "focus" ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out point.X) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out point.Y))
                continue;

            var monitor = MonitorFromPoint(point, 0);
            if (monitor == IntPtr.Zero) continue;
            IntPtr fullscreenMonitor;
            var window = FindExposedFullscreen(monitor, out fullscreenMonitor);
            if (window != IntPtr.Zero && window != GetForegroundWindow())
                SetForegroundWindow(window);
        }
    }

    private static void SlideBar(IntPtr window, bool visible)
    {
        Rect bounds;
        if (!GetWindowRect(window, out bounds)) return;
        int height = bounds.Bottom - bounds.Top;
        int width = bounds.Right - bounds.Left;

        // Clip the part that slides beyond the monitor, including on stacked displays.
        const int frames = 9;
        for (int frame = 0; frame <= frames; frame++)
        {
            int offset = height * (visible ? frames - frame : frame) / frames;
            var region = CreateRectRgn(0, offset, width, height);
            if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0)
                DeleteObject(region);
            SetWindowPos(window, IntPtr.Zero, bounds.Left, bounds.Top - offset, 0, 0, SlideFlags);
            if (visible && frame == 0) ShowWindow(window, 4); // SW_SHOWNOACTIVATE
            if (frame < frames) Thread.Sleep(15);
        }

        if (!visible) ShowWindow(window, 0); // SW_HIDE
        SetWindowPos(window, IntPtr.Zero, bounds.Left, bounds.Top, 0, 0, SlideFlags);
        SetWindowRgn(window, IntPtr.Zero, true);
    }

    private static void SetBarsVisible(bool desktopVisible, IntPtr fullscreenMonitor, bool animate)
    {
        var processes = Process.GetProcessesByName("zebar");
        try
        {
            var processIds = processes.Select(process => process.Id).ToArray();
            EnumWindows((window, parameter) =>
            {
                int processId;
                GetWindowThreadProcessId(window, out processId);
                if (!processIds.Contains(processId)) return true;
                bool fullscreenOnMonitor = fullscreenMonitor != IntPtr.Zero &&
                    MonitorFromWindow(window, 2) == fullscreenMonitor;
                bool visible = desktopVisible && !fullscreenOnMonitor;
                if (IsWindowVisible(window) == visible) return true;

                var title = new StringBuilder(128);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString() == BarTitle)
                {
                    if (animate) SlideBar(window, visible);
                    else ShowWindow(window, visible ? 4 : 0);
                }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor DPI aware.
        if (args.Length != 0) return 2;

        using (var barLock = new Mutex(false, @"Local\Zebar-wallpaper-bar-visibility"))
        using (var key = Registry.CurrentUser.OpenSubKey(DesktopKey))
        using (var changed = new AutoResetEvent(false))
        using (var closed = new ManualResetEvent(false))
        {
            if (key == null) return 1;
            var reader = new Thread(() =>
            {
                try { ReadFocusRequests(); }
                finally { closed.Set(); }
            }) { IsBackground = true };
            reader.Start();

            bool? previousDesktop = null;
            var previousMonitor = IntPtr.Zero;
            var handles = new WaitHandle[] { changed, closed };
            bool register = true;
            while (true)
            {
                if (register && RegNotifyChangeKeyValue(key.Handle, false, NotifyLastSet,
                    changed.SafeWaitHandle, true) != 0) return 1;

                var foreground = GetForegroundWindow();
                var fullscreenMonitor = FullscreenMonitor(foreground);
                if (register || fullscreenMonitor != previousMonitor)
                {
                    var desktops = key.GetValue("VirtualDesktopIDs") as byte[];
                    var current = key.GetValue("CurrentVirtualDesktop") as byte[];
                    bool desktopVisible = desktops == null || desktops.Length <= 16 ||
                        (current != null && current.Length == 16
                            ? desktops.Take(16).SequenceEqual(current)
                            : previousDesktop.GetValueOrDefault(true));

                    try { barLock.WaitOne(); }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        // Another widget may have completed the same transition while we waited.
                        fullscreenMonitor = FullscreenMonitor(GetForegroundWindow());
                        SetBarsVisible(desktopVisible, fullscreenMonitor,
                            previousDesktop == desktopVisible && fullscreenMonitor != previousMonitor);
                    }
                    finally { barLock.ReleaseMutex(); }
                    previousDesktop = desktopVisible;
                    previousMonitor = fullscreenMonitor;
                }

                int signal = WaitHandle.WaitAny(handles, 70);
                if (signal == 1) return 0;
                register = signal == 0;
            }
        }
    }
}
