// Build with .NET Framework csc.exe /target:winexe hide-bar.cs.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class HideBar
{
    private const string BarTitle = "Zebar - wallpaper-bar / bar";
    private const uint AddAppBar = 0;
    private const uint RemoveAppBar = 1;
    private const uint QueryAppBarPosition = 2;
    private const uint SetAppBarPosition = 3;
    private const int SlideTime = 220;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, WorkArea;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage, Edge;
        public Rect Bounds;
        public IntPtr Parameter;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect bounds);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    private static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Point point;
        if (args.Length == 0 && GetCursorPos(out point))
        {
            var bar = FindBar(point);
            if (bar == IntPtr.Zero) return;
            EventWaitHandle toggle;
            if (!EventWaitHandle.TryOpenExisting(EventName(bar), out toggle)) return;
            using (toggle) toggle.Set();
        }
        else if (args.Length == 3 && args[0] == "--watch" &&
            int.TryParse(args[1], out point.X) && int.TryParse(args[2], out point.Y))
        {
            var bar = FindBar(point);
            if (bar != IntPtr.Zero) Watch(bar);
        }
    }

    private static string EventName(IntPtr bar)
    {
        return "Local\\ZebarAutoHide-" + bar.ToInt64();
    }

    private static void Watch(IntPtr bar)
    {
        uint processId;
        GetWindowThreadProcessId(bar, out processId);
        bool created;
        using (var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(bar), out created))
        {
            if (!created) return;
            while (IsOwnedBy(bar, processId))
            {
                if (toggle.WaitOne(250) && IsOwnedBy(bar, processId))
                    AutoHide(bar, processId, toggle);
            }
        }
    }

    private static void AutoHide(IntPtr bar, uint processId, EventWaitHandle toggle)
    {
        Rect bounds;
        var monitor = MonitorFromWindow(bar, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetWindowRect(bar, out bounds) || !GetMonitorInfo(monitor, ref info)) return;

        var data = new AppBarData
        {
            Size = Marshal.SizeOf(typeof(AppBarData)),
            Window = bar,
            Edge = 1, // Top edge.
            Bounds = bounds
        };
        if (SHAppBarMessage(RemoveAppBar, ref data) == UIntPtr.Zero) return;

        var revealed = false;
        var clipped = false;
        var armed = false;
        var clock = Stopwatch.StartNew();
        long changed = 0;
        long pointerSince = -1;

        try
        {
            Notify("conceal");
            while (!toggle.WaitOne(15) && IsOwnedBy(bar, processId))
            {
                if (!GetMonitorInfo(monitor, ref info) || MonitorFromWindow(bar, 0) != monitor)
                    break;

                var now = clock.ElapsedMilliseconds;
                if (!revealed && !clipped && now - changed >= SlideTime)
                {
                    var empty = CreateRectRgn(0, 0, 0, 0);
                    if (empty == IntPtr.Zero) break;
                    if (SetWindowRgn(bar, empty, true) == 0)
                    {
                        DeleteObject(empty);
                        break;
                    }
                    clipped = true;
                }

                Point cursor;
                if (!GetCursorPos(out cursor)) continue;
                var onMonitor = cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                    cursor.Y >= info.Bounds.Top;
                var atEdge = onMonitor && cursor.Y < info.Bounds.Top + 2;
                var overBar = onMonitor && cursor.Y < bounds.Bottom + 4;
                if (!atEdge) armed = true;
                var shouldChange = revealed ? !overBar : armed && atEdge;
                if (!shouldChange)
                {
                    pointerSince = -1;
                    continue;
                }

                if (pointerSince < 0) pointerSince = now;
                if (now - pointerSince < (revealed ? 250 : 90)) continue;
                pointerSince = -1;
                changed = now;
                revealed = !revealed;
                if (revealed && clipped)
                {
                    SetWindowRgn(bar, IntPtr.Zero, true);
                    clipped = false;
                }
                Notify(revealed ? "reveal" : "conceal");
            }
        }
        finally
        {
            if (IsOwnedBy(bar, processId))
            {
                if (clipped) SetWindowRgn(bar, IntPtr.Zero, true);
                // Restore the same top reservation that Zebar created for this HWND.
                data.Bounds = bounds;
                if (SHAppBarMessage(AddAppBar, ref data) != UIntPtr.Zero)
                {
                    SHAppBarMessage(QueryAppBarPosition, ref data);
                    data.Bounds.Bottom = data.Bounds.Top + bounds.Bottom - bounds.Top;
                    SHAppBarMessage(SetAppBarPosition, ref data);
                }
                Notify("pin");
            }
        }
    }

    private static void Notify(string state)
    {
        Console.WriteLine(state);
        Console.Out.Flush();
    }

    private static bool IsOwnedBy(IntPtr window, uint processId)
    {
        uint owner;
        return GetWindowThreadProcessId(window, out owner) != 0 && owner == processId;
    }

    private static IntPtr FindBar(Point point)
    {
        var monitor = MonitorFromPoint(point, 0);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var bar = IntPtr.Zero;
        var title = new StringBuilder(128);
        EnumWindows((window, data) =>
        {
            if (!IsWindowVisible(window) || MonitorFromWindow(window, 0) != monitor ||
                GetWindowText(window, title, title.Capacity) == 0 || title.ToString() != BarTitle)
                return true;

            uint processId;
            GetWindowThreadProcessId(window, out processId);
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    if (!string.Equals(process.ProcessName, "zebar", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            bar = window;
            return false;
        }, IntPtr.Zero);
        return bar;
    }
}
