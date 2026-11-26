// Build with .NET Framework csc.exe /target:winexe /reference:System.Web.Extensions.dll cycle-windows.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class CycleWindows
{
    private const string RecentOnMonitor = "--recent-on-monitor";
    private const string MoveToOtherMonitor = "--move-to-other-monitor";
    private const string FocusPrefix = "--focus-";

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int length);

    [DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static int Main(string[] args)
    {
        var bringWorkspace = args.Length == 2 && args[0] == "--bring" &&
            args[1].Length == 1 && "WASDIO".Contains(args[1]);
        var focusDirection = args.Length == 1 && args[0].StartsWith(FocusPrefix, StringComparison.Ordinal)
            ? args[0].Substring(FocusPrefix.Length) : null;
        if ((args.Length > 1 && !bringWorkspace) || (args.Length == 1 &&
            args[0] != RecentOnMonitor && args[0] != MoveToOtherMonitor &&
            !(focusDirection != null && new[] { "left", "down", "up", "right" }.Contains(focusDirection)) &&
            !System.Text.RegularExpressions.Regex.IsMatch(args[0], @"\A(?:[1-9]|10|[WASDIO])\z")))
            return 2;

        var destination = args.Length == 1 ? args[0] : null;
        try
        {
            using (var socket = new ClientWebSocket())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var token = timeout.Token;
                socket.ConnectAsync(new Uri("ws://127.0.0.1:6123"), token).GetAwaiter().GetResult();
                if (bringWorkspace)
                    BringWorkspace(socket, token, args[1]);
                else if (focusDirection != null)
                    FocusDirection(socket, token, focusDirection);
                else if (destination == RecentOnMonitor)
                    FocusRecentOnMonitor(socket, token);
                else if (destination == MoveToOtherMonitor)
                    MoveWorkspaceToOtherMonitor(socket, token);
                else
                    HandleWorkspaceWindows(socket, token, destination);
                socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token).GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void FocusDirection(ClientWebSocket socket, CancellationToken token, string direction)
    {
        var previous = GetForegroundWindow();
        Request(socket, token, "command focus --direction " + direction);

        // GlazeWM changes focus asynchronously after accepting the IPC command.
        var deadline = DateTime.UtcNow.AddMilliseconds(250);
        IntPtr focused = IntPtr.Zero;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            focused = GetForegroundWindow();
            if (focused != IntPtr.Zero && focused != previous) break;
        }

        Rect bounds;
        if (focused != IntPtr.Zero && focused != previous && GetWindowRect(focused, out bounds))
            SetCursorPos(bounds.Left + (bounds.Right - bounds.Left) / 2,
                bounds.Top + (bounds.Bottom - bounds.Top) / 2);
    }

    private static void MoveWorkspaceToOtherMonitor(ClientWebSocket socket, CancellationToken token)
    {
        var data = (Dictionary<string, object>)Request(socket, token, "query monitors")["data"];
        var monitors = ((object[])data["monitors"]).Cast<Dictionary<string, object>>().ToArray();
        var sourceIndex = Array.FindIndex(monitors, monitor => Equals(monitor["hasFocus"], true));
        if (sourceIndex < 0) return;

        var source = monitors[sourceIndex];
        for (var offset = 1; offset < monitors.Length; offset++)
        {
            var target = monitors[(sourceIndex + offset) % monitors.Length];
            var direction = DirectionToMonitor(monitors, source, target);
            if (direction == null) continue;

            Request(socket, token, "command move-workspace --direction " + direction);
            return;
        }
    }

    private static void BringWorkspace(ClientWebSocket socket, CancellationToken token, string name)
    {
        var data = (Dictionary<string, object>)Request(socket, token, "query monitors")["data"];
        var monitors = ((object[])data["monitors"]).Cast<Dictionary<string, object>>().ToArray();
        var focused = Array.Find(monitors, monitor => Equals(monitor["hasFocus"], true));
        var target = EmptyDesktopMonitor(monitors) ?? focused;
        if (target == null) return;

        var current = ((object[])target["children"]).Cast<Dictionary<string, object>>()
            .First(workspace => Equals(workspace["isDisplayed"], true));
        var currentName = (string)current["name"];
        if (target != focused)
            Request(socket, token, "command focus --workspace " + currentName);
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlazeWM-shortcuts");
        var statePath = Path.Combine(stateDirectory, Path.GetFileName((string)target["deviceName"]) + "-numbered.txt");

        if (currentName == name)
        {
            var origin = File.Exists(statePath) ? File.ReadAllText(statePath) : null;
            if (!IsNumberedWorkspace(origin) || monitors.Any(monitor => monitor != target &&
                ((object[])monitor["children"]).Cast<Dictionary<string, object>>()
                    .Any(workspace => Equals(workspace["name"], origin))))
            {
                var workspaceNames = ((object[])target["children"]).Cast<Dictionary<string, object>>()
                    .ToDictionary(workspace => (string)workspace["id"], workspace => (string)workspace["name"]);
                origin = ((object[])target["childFocusOrder"]).Cast<string>()
                    .Where(workspaceNames.ContainsKey).Select(id => workspaceNames[id])
                    .FirstOrDefault(IsNumberedWorkspace);
            }
            if (origin != null)
                Request(socket, token, "command focus --workspace " + origin);
            return;
        }

        foreach (var source in monitors)
        {
            var workspace = ((object[])source["children"]).Cast<Dictionary<string, object>>()
                .FirstOrDefault(child => Equals(child["name"], name));
            if (workspace == null) continue;
            if (source == target) break;

            var direction = DirectionToMonitor(monitors, source, target);
            if (direction == null) return;
            Request(socket, token, "command --id " + Guid.Parse((string)workspace["id"]) +
                " move-workspace --direction " + direction);
            break;
        }

        Request(socket, token, "command focus --workspace " + name);
        if (IsNumberedWorkspace(currentName))
        {
            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(statePath, currentName);
        }
    }

    private static Dictionary<string, object> EmptyDesktopMonitor(Dictionary<string, object>[] monitors)
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            var className = new StringBuilder(256);
            GetClassName(foreground, className, className.Capacity);
            var name = className.ToString();
            if (name != "Progman" && name != "WorkerW") return null;
        }

        Point cursor;
        if (!GetPhysicalCursorPos(out cursor)) return null;
        foreach (var monitor in monitors)
        {
            var x = Convert.ToInt32(monitor["x"]);
            var y = Convert.ToInt32(monitor["y"]);
            if (cursor.X < x || cursor.X >= x + Convert.ToInt32(monitor["width"]) ||
                cursor.Y < y || cursor.Y >= y + Convert.ToInt32(monitor["height"])) continue;

            var displayed = ((object[])monitor["children"]).Cast<Dictionary<string, object>>()
                .FirstOrDefault(workspace => Equals(workspace["isDisplayed"], true));
            if (displayed != null && ((object[])displayed["children"]).Length == 0) return monitor;
            break;
        }
        return null;
    }

    private static bool IsNumberedWorkspace(string name)
    {
        int number;
        return int.TryParse(name, out number) && number >= 1 && number <= 10 && name == number.ToString();
    }

    private static string DirectionToMonitor(Dictionary<string, object>[] monitors,
        Dictionary<string, object> source, Dictionary<string, object> target)
    {
        // Match GlazeWM's directional selection so we never move to another adjacent monitor.
        foreach (var direction in new[] { "left", "right", "up", "down" })
        {
            var horizontal = direction == "left" || direction == "right";
            var axis = horizontal ? "x" : "y";
            var crossAxis = horizontal ? "y" : "x";
            var crossSize = horizontal ? "height" : "width";
            var sign = direction == "right" || direction == "down" ? 1 : -1;
            var originAxis = Convert.ToInt32(source[axis]);
            var originCross = Convert.ToInt32(source[crossAxis]);
            var originEnd = originCross + Convert.ToInt32(source[crossSize]);

            var adjacent = monitors
                .Where(monitor => sign * (Convert.ToInt32(monitor[axis]) - originAxis) > 0 &&
                    Convert.ToInt32(monitor[crossAxis]) < originEnd &&
                    Convert.ToInt32(monitor[crossAxis]) + Convert.ToInt32(monitor[crossSize]) > originCross)
                .OrderBy(monitor => sign * Convert.ToInt32(monitor[axis]))
                .FirstOrDefault();
            if (adjacent == target) return direction;
        }
        return null;
    }

    private static void HandleWorkspaceWindows(ClientWebSocket socket, CancellationToken token, string destination)
    {
        var data = (Dictionary<string, object>)Request(socket, token, "query workspaces")["data"];
        foreach (Dictionary<string, object> workspace in (object[])data["workspaces"])
        {
            if (!Equals(workspace["hasFocus"], true)) continue;

            var windows = new List<Dictionary<string, object>>();
            CollectWindows(workspace, windows, destination != null);
            if (destination != null)
                MoveWindows(socket, token, workspace, windows, destination);
            else if (windows.Count > 1)
            {
                var focused = windows.FindIndex(window => Equals(window["hasFocus"], true));
                var next = windows[(focused + 1) % windows.Count];
                Request(socket, token, "command focus --container-id " + Guid.Parse((string)next["id"]));
            }
            break;
        }
    }

    private static void FocusRecentOnMonitor(ClientWebSocket socket, CancellationToken token)
    {
        var data = (Dictionary<string, object>)Request(socket, token, "query monitors")["data"];
        foreach (Dictionary<string, object> monitor in (object[])data["monitors"])
        {
            if (!Equals(monitor["hasFocus"], true)) continue;

            // The first entry is the workspace already displayed on this monitor.
            var order = (object[])monitor["childFocusOrder"];
            if (order.Length < 2) return;

            foreach (Dictionary<string, object> workspace in (object[])monitor["children"])
            {
                if (!Equals(workspace["id"], order[1])) continue;

                // Workspace focus redraws both workspaces, updating their native visibility.
                Request(socket, token, "command focus --workspace " + (string)workspace["name"]);
                break;
            }
            return;
        }
    }

    private static void MoveWindows(ClientWebSocket socket, CancellationToken token,
        Dictionary<string, object> workspace, List<Dictionary<string, object>> windows, string destination)
    {
        if (windows.Count == 0 || Equals(workspace["name"], destination)) return;

        // Move the focused window last so focus stays in the source during the transfer.
        var focused = windows.FindIndex(window => Equals(window["hasFocus"], true));
        if (focused >= 0)
        {
            var window = windows[focused];
            windows.RemoveAt(focused);
            windows.Add(window);
        }

        foreach (var window in windows)
            Request(socket, token, "command --id " + Guid.Parse((string)window["id"]) + " move --workspace " + destination);
        Request(socket, token, "command focus --workspace " + destination);
    }

    private static void CollectWindows(Dictionary<string, object> container, List<Dictionary<string, object>> windows,
        bool includeMinimized)
    {
        if (Equals(container["type"], "window"))
        {
            var state = (Dictionary<string, object>)container["state"];
            if (includeMinimized || !Equals(state["type"], "minimized")) windows.Add(container);
            return;
        }

        object children;
        if (!container.TryGetValue("children", out children)) return;
        foreach (Dictionary<string, object> child in (object[])children)
            CollectWindows(child, windows, includeMinimized);
    }

    private static Dictionary<string, object> Request(ClientWebSocket socket, CancellationToken token, string command)
    {
        var request = new ArraySegment<byte>(Encoding.UTF8.GetBytes(command));
        socket.SendAsync(request, WebSocketMessageType.Text, true, token).GetAwaiter().GetResult();

        var buffer = new ArraySegment<byte>(new byte[8192]);
        using (var message = new MemoryStream())
        {
            WebSocketReceiveResult response;
            do
            {
                response = socket.ReceiveAsync(buffer, token).GetAwaiter().GetResult();
                if (response.MessageType != WebSocketMessageType.Text) throw new IOException("GlazeWM closed the connection.");
                message.Write(buffer.Array, buffer.Offset, response.Count);
            }
            while (!response.EndOfMessage);

            var json = Encoding.UTF8.GetString(message.ToArray());
            var result = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (!Equals(result["success"], true)) throw new InvalidOperationException((string)result["error"]);
            return result;
        }
    }
}
