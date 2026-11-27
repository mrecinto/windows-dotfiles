using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static partial class CycleWindows
{
    private static Tuple<IntPtr, uint> lastFullscreenWindow;

    private delegate bool EnumerateWindow(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumerateWindow callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmWindowValue(IntPtr window, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmWindowRect(IntPtr window, int attribute, out Rect rect, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, NativeMouseInput[] input, int size);

    private static Dictionary<string, object> FocusedGamingMonitor(ClientWebSocket socket, CancellationToken token)
    {
        return ScratchpadMonitors(socket, token).FirstOrDefault(monitor =>
        {
            if (!Equals(monitor["hasFocus"], true)) return false;
            var gameWorkspace = WorkspaceOnMonitor(monitor, "G");
            return gameWorkspace != null && Equals(gameWorkspace["isDisplayed"], true);
        });
    }

    private static void RefocusGamingWorkspace()
    {
        try
        {
            WithScratchpadConnection((socket, token) =>
            {
                var monitor = FocusedGamingMonitor(socket, token);
                if (monitor != null) FocusGameOnMonitor(monitor, socket, token);
            });
        }
        catch (Exception error) { LogScratchpadError(error); }
    }

    private static void FocusGameOnMonitor(Dictionary<string, object> monitor,
        ClientWebSocket socket = null, CancellationToken token = default(CancellationToken))
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var nativeMonitor = new IntPtr(Convert.ToInt64(monitor["handle"]));
        var bounds = GamingDisplayBounds(monitor);

        var target = FindManagedFullscreenOnG(monitor, bounds);
        if (target == IntPtr.Zero) target = FindFullscreenWindow(nativeMonitor, bounds);
        if (target == IntPtr.Zero)
        {
            var remembered = Volatile.Read(ref lastFullscreenWindow);
            uint processId;
            if (remembered == null || GetWindowThreadProcessId(remembered.Item1, out processId) == 0 ||
                processId != remembered.Item2 || MonitorFromWindow(remembered.Item1, 0) != nativeMonitor ||
                !IsIconic(remembered.Item1)) return;
            target = remembered.Item1;
            ShowWindowAsync(target, 9); // SW_RESTORE.
        }
        else
        {
            if (socket != null) ReleaseFullscreenWindowFromGamingWorkspace(socket, token, monitor, target);
            uint processId;
            if (GetWindowThreadProcessId(target, out processId) != 0)
                Volatile.Write(ref lastFullscreenWindow, Tuple.Create(target, processId));
        }
        if (target == GetForegroundWindow()) return;

        Point cursor;
        if (GetPhysicalCursorPos(out cursor) && (cursor.X < bounds.Left || cursor.X >= bounds.Right ||
            cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom))
            SetPhysicalCursorPos(bounds.Left + (bounds.Right - bounds.Left) / 2,
                bounds.Top + (bounds.Bottom - bounds.Top) / 2);

        if (SetForegroundWindow(target)) return;
        // Request foreground access with a zero-motion input, without clicking or typing.
        SendInput(1, new NativeMouseInput[1], Marshal.SizeOf(typeof(NativeMouseInput)));
        SetForegroundWindow(target);
    }

    private static Rect GamingDisplayBounds(Dictionary<string, object> monitor)
    {
        var x = Convert.ToInt32(monitor["x"]);
        var y = Convert.ToInt32(monitor["y"]);
        return new Rect
        {
            Left = x,
            Top = y,
            Right = x + Convert.ToInt32(monitor["width"]),
            Bottom = y + Convert.ToInt32(monitor["height"])
        };
    }

    private static IntPtr FindLiveGamingUnderlay(ClientWebSocket socket, CancellationToken token,
        Dictionary<string, object>[] monitors, Dictionary<string, object> monitor)
    {
        var display = new IntPtr(Convert.ToInt64(monitor["handle"]));
        var bounds = GamingDisplayBounds(monitor);
        var game = FindManagedFullscreenOnG(monitor, bounds);
        if (game == IntPtr.Zero) game = FindFullscreenWindow(display, bounds);
        if (game == IntPtr.Zero) return IntPtr.Zero;

        return !IsManagedWindow(monitors, game) ||
            ReleaseFullscreenWindowFromGamingWorkspace(socket, token, monitor, game) ? game : IntPtr.Zero;
    }

    private static IntPtr FindManagedFullscreenOnG(Dictionary<string, object> monitor, Rect bounds)
    {
        var gaming = WorkspaceOnMonitor(monitor, "G");
        if (gaming == null || !Equals(gaming["isDisplayed"], true)) return IntPtr.Zero;
        var windows = new List<Dictionary<string, object>>();
        CollectWindows(gaming, windows, false);
        return windows.Select(item => new IntPtr(Convert.ToInt64(item["handle"])))
            .FirstOrDefault(window => IsFullscreenAppWindow(window, bounds));
    }

    private static bool ReleaseFullscreenWindowFromGamingWorkspace(ClientWebSocket socket,
        CancellationToken token, Dictionary<string, object> monitor, IntPtr window)
    {
        var gaming = WorkspaceOnMonitor(monitor, "G");
        if (gaming == null || !Equals(gaming["isDisplayed"], true)) return false;

        var windows = new List<Dictionary<string, object>>();
        CollectWindows(gaming, windows, false);
        var target = windows.FirstOrDefault(item => Convert.ToInt64(item["handle"]) == window.ToInt64());
        if (target == null) return false;

        // Keep fullscreen content on G visible when GlazeWM cloaks the workspace.
        ScratchpadWindowCommand(socket, token, target, "ignore");
        return true;
    }

    private static bool IsManagedWindow(Dictionary<string, object>[] monitors, IntPtr window)
    {
        // GlazeWM will cloak its own windows when their workspace becomes hidden.
        var managed = new List<Dictionary<string, object>>();
        foreach (var workspace in monitors.SelectMany(ScratchpadChildren))
            CollectWindows(workspace, managed, true);
        return managed.Any(item => Convert.ToInt64(item["handle"]) == window.ToInt64());
    }

    private static void FocusGamingWorkspaceOnClick(IntPtr window)
    {
        var display = MonitorFromWindow(window, 0);
        if (display == IntPtr.Zero || IsIconic(window) || !IsWindowVisible(window)) return;

        var screen = Screen.FromHandle(window).Bounds;
        var bounds = new Rect { Left = screen.Left, Top = screen.Top, Right = screen.Right,
            Bottom = screen.Bottom };
        if (!IsFullscreenAppWindow(window, bounds)) return;

        try
        {
            WithScratchpadConnection((socket, token) =>
            {
                var monitors = ScratchpadMonitors(socket, token);
                var monitor = Array.Find(monitors, item => Convert.ToInt64(item["handle"]) == display.ToInt64());
                if (monitor == null || !IsOverlayMonitor(monitor)) return;

                var backdrop = FindWindow(null, BackdropTitle);
                if (backdrop != IntPtr.Zero && IsWindowVisible(backdrop) &&
                    MonitorFromWindow(backdrop, 0) == display) return;

                var visible = ScratchpadChildren(monitor).FirstOrDefault(item => Equals(item["isDisplayed"], true));
                if (visible == null) return;
                var name = (string)visible["name"];
                if (name == "G" || IsSpecialWorkspace(name) || IsManagedWindow(monitors, window)) return;

                Request(socket, token, "command focus --workspace G");
            });
        }
        catch (Exception error) { LogScratchpadError(error); }
    }

    private static Rectangle[] VisibleWorkspaceFrames(Dictionary<string, object> workspace,
        IntPtr game, Rectangle display)
    {
        var windows = new List<Dictionary<string, object>>();
        CollectWindows(workspace, windows, false);
        var handles = new HashSet<IntPtr>(windows.Select(window => new IntPtr(Convert.ToInt64(window["handle"]))));
        var frames = new List<Rectangle>();
        EnumWindows((window, state) =>
        {
            if (window == game) return false;
            if (!handles.Contains(window) || !IsWindowVisible(window) || IsIconic(window)) return true;
            int cloaked;
            if (GetDwmWindowValue(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            Rect rect;
            if (GetDwmWindowRect(window, 9, out rect, Marshal.SizeOf(typeof(Rect))) != 0 &&
                !GetWindowRect(window, out rect)) return true;
            var frame = Rectangle.Intersect(display, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            if (frame.Width > 0 && frame.Height > 0)
            {
                frame.Offset(-display.X, -display.Y);
                frames.Add(frame);
            }
            return true;
        }, IntPtr.Zero);
        return frames.ToArray();
    }

    private static IntPtr FindZebarOnMonitor(IntPtr monitor)
    {
        var result = IntPtr.Zero;
        var title = new StringBuilder(128);
        EnumWindows((window, state) =>
        {
            if (MonitorFromWindow(window, 2) != monitor ||
                GetWindowText(window, title, title.Capacity) == 0 ||
                title.ToString() != "Zebar - wallpaper-bar / bar") return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindFullscreenWindow(IntPtr monitor, Rect bounds)
    {
        IntPtr visible = IntPtr.Zero;
        EnumWindows((window, state) =>
        {
            if (MonitorFromWindow(window, 0) != monitor || !IsFullscreenAppWindow(window, bounds)) return true;
            visible = window;
            return false;
        }, IntPtr.Zero);

        return visible;
    }

    private static bool IsFullscreenAppWindow(IntPtr window, Rect bounds)
    {
        const int extendedStyle = -20;
        const int toolOrNoActivate = 0x08000080;
        const int dwmCloaked = 14;
        int cloak;
        if (!IsWindowVisible(window) || IsIconic(window) ||
            (GetWindowStyle(window, extendedStyle) & toolOrNoActivate) != 0 ||
            (GetDwmWindowValue(window, dwmCloaked, out cloak, sizeof(int)) == 0 && cloak != 0))
            return false;

        const int extendedFrameBounds = 9;
        Rect frame;
        if ((GetDwmWindowRect(window, extendedFrameBounds, out frame, Marshal.SizeOf(typeof(Rect))) != 0 &&
            !GetWindowRect(window, out frame)) || !MatchesDisplay(frame, bounds)) return false;

        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        var name = className.ToString();
        if (name == "Progman" || name == "WorkerW") return false;

        // A window can retain caption styles while extending its client area to the entire display.
        const int styleIndex = -16;
        const int caption = 0x00C00000;
        if ((GetWindowStyle(window, styleIndex) & caption) != caption) return true;

        Rect client;
        if (!GetClientRect(window, out client)) return false;
        var topLeft = new Point { X = client.Left, Y = client.Top };
        var bottomRight = new Point { X = client.Right, Y = client.Bottom };
        return ClientToScreen(window, ref topLeft) && ClientToScreen(window, ref bottomRight) &&
            MatchesDisplay(new Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X,
                Bottom = bottomRight.Y }, bounds);
    }

    private static bool MatchesDisplay(Rect frame, Rect bounds)
    {
        const int tolerance = 2;
        return Math.Abs(frame.Left - bounds.Left) <= tolerance && Math.Abs(frame.Top - bounds.Top) <= tolerance &&
            Math.Abs(frame.Right - bounds.Right) <= tolerance && Math.Abs(frame.Bottom - bounds.Bottom) <= tolerance;
    }
}
