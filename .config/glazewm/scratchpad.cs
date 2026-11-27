using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class CycleWindows
{
    private const string ScratchpadWorkspace = "A";
    private const string SpecialWorkspaces = "WASDIO";
    private const string OverlayMonitorHardwareId = "MSI6CD7";
    private const string ScratchpadSignal = @"Local\GlazeWM-Scratchpad-Close";
    private const string ScratchpadCycleSignal = @"Local\GlazeWM-Scratchpad-Cycle";
    private const string HotkeyStopSignal = @"Local\GlazeWM-Scratchpad-Stop";
    private const string BackdropTitle = "GlazeWM Scratchpad Backdrop";
    private const string HotkeysWindowTitle = "GlazeWM Scratchpad Hotkeys";
    private const int HotkeyMessage = 0x0312;
    private const int RecentWorkspaceMessage = 0x8052;
    private const int SwitchSpecialWorkspaceMessage = 0x8051;
    private const int CloseSpecialWorkspaceMessage = 0x8053;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int windowCount);

    [DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr batch, IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EndDeferWindowPos(IntPtr batch);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowStyle(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    private delegate IntPtr KeyboardHook(int code, IntPtr message, IntPtr key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, KeyboardHook callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr key);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    private static bool IsSpecialWorkspace(string name)
    {
        return name != null && name.Length == 1 && SpecialWorkspaces.Contains(name);
    }

    private static bool IsOverlayMonitor(Dictionary<string, object> monitor)
    {
        return Equals(monitor["hardwareId"], OverlayMonitorHardwareId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private static int RunScratchpadHotkeys()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        bool created;
        using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, HotkeyStopSignal, out created))
        {
            if (!created) return 0;
            try
            {
                using (var lifetime = new CancellationTokenSource())
                using (var window = new ScratchpadHotkeys())
                {
                    var wait = ThreadPool.RegisterWaitForSingleObject(stop,
                        (state, timedOut) => window.BeginInvoke(new Action(Application.ExitThread)),
                        null, Timeout.Infinite, true);
                    var availability = Task.Run(() => window.WatchRecentWorkspaceAvailability(lifetime.Token));
                    try { Application.Run(); }
                    finally
                    {
                        wait.Unregister(null);
                        lifetime.Cancel();
                        availability.GetAwaiter().GetResult();
                    }
                }
                return 0;
            }
            catch (Exception error) { return LogScratchpadError(error); }
        }
    }

    private static int StopScratchpadHotkeys()
    {
        EventWaitHandle stop;
        if (EventWaitHandle.TryOpenExisting(HotkeyStopSignal, out stop))
            using (stop) stop.Set();
        EventWaitHandle close;
        if (EventWaitHandle.TryOpenExisting(ScratchpadSignal, out close))
            using (close) close.Set();
        return 0;
    }

    private static bool SignalScratchpadCycle()
    {
        return SignalScratchpadEvent(ScratchpadCycleSignal);
    }

    private static bool RequestBackdropClose()
    {
        var backdrop = FindWindow(null, BackdropTitle);
        return backdrop != IntPtr.Zero && PostMessage(backdrop, CloseSpecialWorkspaceMessage,
            IntPtr.Zero, IntPtr.Zero);
    }

    private static bool CloseBackdropOnForegroundMonitor()
    {
        var backdrop = FindWindow(null, BackdropTitle);
        if (backdrop == IntPtr.Zero) return false;
        var foregroundMonitor = MonitorFromWindow(GetForegroundWindow(), 0);
        return foregroundMonitor != IntPtr.Zero && foregroundMonitor == MonitorFromWindow(backdrop, 0) &&
            PostMessage(backdrop, CloseSpecialWorkspaceMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool SignalScratchpadEvent(string name)
    {
        EventWaitHandle signal;
        if (!EventWaitHandle.TryOpenExisting(name, out signal)) return false;
        using (signal) return signal.Set();
    }

    private static int OpenRecentSpecialWorkspace(string name)
    {
        var host = FindWindow(null, HotkeysWindowTitle);
        if (host != IntPtr.Zero && PostMessage(host, HotkeyMessage, new IntPtr(name[0]), IntPtr.Zero))
            return 0;
        return RunScratchpad(name);
    }

    private static bool NotifyOpenBackdrop(string name)
    {
        var backdrop = FindWindow(null, BackdropTitle);
        return backdrop != IntPtr.Zero && PostMessage(backdrop, SwitchSpecialWorkspaceMessage,
            new IntPtr(name[0]), IntPtr.Zero);
    }

    private sealed class ScratchpadHotkeys : Form
    {
        // Keep these letters available for the matching Alt bindings in config.yaml.
        private const string GlazeWmAltLetters = "EFHJKLQV";
        private readonly List<int> registered = new List<int>();
        private readonly object recentWorkspaceLock = new object();
        private readonly KeyboardHook recentWorkspaceHook;
        private readonly IntPtr hook;
        private volatile bool recentWorkspaceEnabled;
        private bool capturedTab;

        public ScratchpadHotkeys()
        {
            Text = HotkeysWindowTitle;
            const uint alt = 1, noRepeat = 0x4000;
            var unavailable = new List<char>();
            for (var key = 'A'; key <= 'Z'; key++)
            {
                if (GlazeWmAltLetters.Contains(key)) continue;
                if (RegisterHotKey(Handle, key, noRepeat | alt, key))
                    registered.Add(key);
                else
                    unavailable.Add(key);
            }
            if (unavailable.Count > 0)
                LogScratchpadError(new InvalidOperationException(
                    "Could not reserve these Alt shortcuts: " + new string(unavailable.ToArray())));

            recentWorkspaceHook = OnKeyboardInput;
            hook = SetWindowsHookEx(13, recentWorkspaceHook, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                LogScratchpadError(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        private IntPtr OnKeyboardInput(int code, IntPtr message, IntPtr key)
        {
            const int tab = 9;
            const int altDown = 0x20, keyUp = 0x80;
            if (code >= 0 && Marshal.ReadInt32(key) == tab)
            {
                var flags = Marshal.ReadInt32(key, 8);
                if (capturedTab)
                {
                    if ((flags & keyUp) != 0) capturedTab = false;
                    return new IntPtr(1);
                }
                if (recentWorkspaceEnabled && (flags & (altDown | keyUp)) == altDown &&
                    GetAsyncKeyState(0x10) >= 0 && GetAsyncKeyState(0x11) >= 0 &&
                    GetAsyncKeyState(0x5B) >= 0 && GetAsyncKeyState(0x5C) >= 0 &&
                    PostMessage(Handle, RecentWorkspaceMessage, IntPtr.Zero, IntPtr.Zero))
                {
                    capturedTab = true;
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, key);
        }

        public async Task WatchRecentWorkspaceAvailability(CancellationToken token)
        {
            if (hook == IntPtr.Zero) return;
            var buffer = new ArraySegment<byte>(new byte[4096]);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var events = new ClientWebSocket())
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(3));
                            await events.ConnectAsync(new Uri("ws://127.0.0.1:6123"), timeout.Token)
                                .ConfigureAwait(false);
                            Request(events, timeout.Token, "sub -e pause_changed application_exiting");
                        }

                        while (!token.IsCancellationRequested)
                        {
                            WithScratchpadConnection((socket, queryToken) =>
                                recentWorkspaceEnabled = Equals(Request(socket, queryToken, "query paused")["data"], false));
                            WebSocketReceiveResult message;
                            do
                            {
                                message = await events.ReceiveAsync(buffer, token).ConfigureAwait(false);
                            }
                            while (!message.EndOfMessage);
                            if (message.MessageType == WebSocketMessageType.Close) break;
                        }
                    }
                }
                catch (WebSocketException) { }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException) { }
                finally { recentWorkspaceEnabled = false; }

                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage)
            {
                var name = ((char)message.WParam.ToInt32()).ToString();
                if (IsSpecialWorkspace(name) && !NotifyOpenBackdrop(name))
                {
                    var worker = new Thread(() => RunScratchpad(name)) { IsBackground = true };
                    worker.SetApartmentState(ApartmentState.STA);
                    worker.Start();
                }
                return;
            }
            if (message.Msg == RecentWorkspaceMessage)
            {
                if (!recentWorkspaceEnabled || CloseBackdropOnForegroundMonitor()) return;
                ThreadPool.QueueUserWorkItem(state =>
                {
                    lock (recentWorkspaceLock)
                    {
                        if (recentWorkspaceEnabled) SwitchRecentWorkspace();
                    }
                });
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            foreach (var id in registered) UnregisterHotKey(Handle, id);
            registered.Clear();
            base.OnHandleDestroyed(e);
        }
    }

    private static int RunScratchpad(string name = ScratchpadWorkspace)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor DPI awareness v2.
        try
        {
            using (var closeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ScratchpadSignal))
            using (var instance = new Mutex(false, ScratchpadSignal + "-owner"))
            {
                bool acquired;
                try { acquired = instance.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!acquired)
                {
                    if (NotifyOpenBackdrop(name)) return 0;
                    if (DateTime.UtcNow >= deadline) return 1;
                    // A prior key may still be creating the backdrop, or just finishing a plain switch.
                    try { acquired = instance.WaitOne(20); }
                    catch (AbandonedMutexException) { acquired = true; }
                }

                try
                {
                    closeSignal.Reset();
                    while (name != null)
                        name = ShowScratchpad(closeSignal, name);
                }
                finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception error) { return LogScratchpadError(error); }
    }

    private static int LogScratchpadError(Exception error)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlazeWM-shortcuts");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "scratchpad.log"),
            DateTime.Now.ToString("s") + " " + error.Message + Environment.NewLine);
        return 1;
    }

    private static int StashSpecialWorkspace(string name)
    {
        try
        {
            WithScratchpadConnection((socket, token) =>
            {
                var focused = ScratchpadQuery(socket, token, "focused", "focused");
                if (!Equals(focused["type"], "window")) return;

                ScratchpadWindowCommand(socket, token, focused, "move --workspace " + name);
                ScratchpadWindowCommand(socket, token, focused, "set-floating --centered --shown-on-top");
            });
            return 0;
        }
        catch (Exception error) { return LogScratchpadError(error); }
        finally { ReassertHeldAlt(); }
    }

    private static string ShowScratchpad(EventWaitHandle closeSignal, string name)
    {
        string origin = null;
        string originDisplay = null;
        var windows = new List<Dictionary<string, object>>();
        ScratchpadBackdrop backdrop = null;
        var switched = false;
        var closedByShortcut = 0;
        var revision = 0;
        string requestedAfterClose = null;
        Action restoreWorkspace = () =>
        {
            if (!switched) return;
            RestoreScratchpadOrigin(originDisplay, origin, name);
            switched = false;
        };

        using (var events = new ClientWebSocket())
        using (var stopEvents = new CancellationTokenSource())
        {
            try
            {
                WithScratchpadConnection((socket, token) =>
                {
                    var monitors = ScratchpadMonitors(socket, token);
                    var monitor = monitors.FirstOrDefault(item => Equals(item["hasFocus"], true));
                    if (monitor == null) return;
                    if (!IsOverlayMonitor(monitor))
                    {
                        BringWorkspace(socket, token, name, monitors);
                        return;
                    }
                    var displayed = ScratchpadChildren(monitor).First(item => Equals(item["isDisplayed"], true));
                    origin = (string)displayed["name"];
                    originDisplay = (string)monitor["deviceName"];
                    if (origin == name)
                    {
                        FocusRecentOnMonitor(socket, token);
                        return;
                    }

                    windows = PrepareSpecialWorkspace(socket, token, monitors, monitor, name);

                    var bounds = new Rectangle(Convert.ToInt32(monitor["x"]), Convert.ToInt32(monitor["y"]),
                        Convert.ToInt32(monitor["width"]), Convert.ToInt32(monitor["height"]));
                    backdrop = new ScratchpadBackdrop(bounds);
                    backdrop.TrackScratchpadWindows(name, windows);
                    backdrop.Show();

                    Request(socket, token, "command focus --workspace " + name);
                    switched = true;
                    backdrop.RefreshStack();
                });

                ReassertHeldAlt();
                if (backdrop == null) return null;
                backdrop.DismissRequested += () =>
                {
                    Interlocked.Exchange(ref closedByShortcut, 1);
                    backdrop.Close();
                };
                backdrop.WorkspaceRequested += requested =>
                {
                    try
                    {
                        var close = false;
                        WithScratchpadConnection((socket, token) =>
                        {
                            var monitors = ScratchpadMonitors(socket, token);
                            var monitor = monitors.FirstOrDefault(item => Equals(item["hasFocus"], true));
                            if (monitor == null || !Equals(monitor["deviceName"], originDisplay))
                            {
                                requestedAfterClose = requested;
                                close = true;
                            }
                            else if (requested == name)
                            {
                                close = true;
                            }
                            else
                            {
                                var nextWindows = PrepareSpecialWorkspace(socket, token, monitors, monitor, requested);
                                Request(socket, token, "command focus --workspace " + requested);
                                Volatile.Write(ref name, requested);
                                Interlocked.Increment(ref revision);
                                windows = nextWindows;
                                backdrop.TrackScratchpadWindows(name, windows);
                                backdrop.RefreshStack();
                            }
                        });
                        if (close)
                        {
                            Interlocked.Exchange(ref closedByShortcut, 1);
                            backdrop.Close();
                        }
                    }
                    catch (Exception error) { LogScratchpadError(error); }
                    finally { ReassertHeldAlt(); }
                };
                backdrop.FormClosing += (sender, args) =>
                {
                    try { restoreWorkspace(); }
                    catch (Exception error) { LogScratchpadError(error); }
                };

                using (var cycleSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ScratchpadCycleSignal))
                {
                    RunScratchpadOverlay(events, stopEvents, closeSignal, cycleSignal, backdrop,
                        originDisplay, () => Volatile.Read(ref name), () => Volatile.Read(ref revision),
                        () => Interlocked.Exchange(ref closedByShortcut, 1));
                }
            }
            finally
            {
                try { restoreWorkspace(); }
                finally { if (backdrop != null) backdrop.Dispose(); }
                if (Volatile.Read(ref closedByShortcut) != 0)
                    ReassertHeldAlt();
            }
        }
        return requestedAfterClose;
    }

    private static List<Dictionary<string, object>> PrepareSpecialWorkspace(ClientWebSocket socket,
        CancellationToken token, Dictionary<string, object>[] monitors, Dictionary<string, object> monitor,
        string name)
    {
        var windows = new List<Dictionary<string, object>>();
        var source = monitors.FirstOrDefault(item => WorkspaceOnMonitor(item, name) != null);
        if (source == null) return windows;

        var workspace = WorkspaceOnMonitor(source, name);
        CollectWindows(workspace, windows, false);
        if (source != monitor)
        {
            var direction = DirectionToMonitor(monitors, source, monitor);
            if (direction == null)
                throw new InvalidOperationException("The special workspace is on a non-adjacent monitor.");
            ScratchpadWindowCommand(socket, token, workspace, "move-workspace --direction " + direction);
        }

        foreach (var window in windows)
        {
            if (name == ScratchpadWorkspace || IsTaskManager(window))
                EnsureSpecialWindowIsTopmost(socket, token, window);
        }
        return windows;
    }

    private static bool IsTaskManager(Dictionary<string, object> window)
    {
        return string.Equals((string)window["processName"], "Taskmgr", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSpecialWindowIsTopmost(ClientWebSocket socket, CancellationToken token,
        Dictionary<string, object> window)
    {
        var state = (Dictionary<string, object>)window["state"];
        object topmost;
        if (!Equals(state["type"], "floating") ||
            !state.TryGetValue("shownOnTop", out topmost) || !Equals(topmost, true))
            ScratchpadWindowCommand(socket, token, window, "set-floating --centered --shown-on-top");
    }

    private static void RunScratchpadOverlay(ClientWebSocket events, CancellationTokenSource stopEvents,
        EventWaitHandle closeSignal, EventWaitHandle cycleSignal, ScratchpadBackdrop backdrop,
        string display, Func<string> activeWorkspace, Func<int> revision,
        Action markClosedByShortcut)
    {
        var stop = ThreadPool.RegisterWaitForSingleObject(closeSignal,
            (state, timedOut) =>
            {
                markClosedByShortcut();
                CloseScratchpadBackdrop(backdrop);
            }, null, Timeout.Infinite, true);
        var cycle = ThreadPool.RegisterWaitForSingleObject(cycleSignal, (state, timedOut) =>
        {
            try { backdrop.BeginInvoke(new Action(() => CycleScratchpadWindows(activeWorkspace()))); }
            catch (InvalidOperationException) { } // The overlay already closed.
        }, null, Timeout.Infinite, false);
        try
        {
            var listener = Task.Run(() => WatchScratchpad(events, stopEvents.Token, backdrop,
                display, activeWorkspace, revision));
            Application.Run(backdrop);
            stopEvents.Cancel();
            listener.GetAwaiter().GetResult();
        }
        finally
        {
            cycle.Unregister(null);
            stop.Unregister(null);
        }
    }

    private static void CycleScratchpadWindows(string name)
    {
        try
        {
            WithScratchpadConnection((socket, token) =>
            {
                var data = (Dictionary<string, object>)Request(socket, token, "query workspaces")["data"];
                var workspace = ((object[])data["workspaces"]).Cast<Dictionary<string, object>>()
                    .FirstOrDefault(item => Equals(item["name"], name) && Equals(item["isDisplayed"], true));
                if (workspace == null) return;
                var windows = new List<Dictionary<string, object>>();
                CollectWindows(workspace, windows, false);
                if (windows.Count == 0) return;
                var focused = windows.FindIndex(window => Equals(window["hasFocus"], true));
                var next = windows[(focused + 1) % windows.Count];
                Request(socket, token, "command focus --container-id " + next["id"]);
            });
        }
        catch (Exception error) { LogScratchpadError(error); }
        finally { ReassertHeldAlt(); }
    }

    private static void RestoreScratchpadOrigin(string display, string origin, string name)
    {
        WithScratchpadConnection((socket, token) =>
        {
            var monitors = ScratchpadMonitors(socket, token);
            var monitor = monitors.FirstOrDefault(item => Equals(item["deviceName"], display));
            var scratchpad = monitor == null ? null : WorkspaceOnMonitor(monitor, name);
            if (scratchpad == null || !Equals(scratchpad["isDisplayed"], true)) return;

            var otherMonitor = monitors.FirstOrDefault(item => item != monitor && Equals(item["hasFocus"], true));
            var otherWorkspace = otherMonitor == null ? null : ScratchpadChildren(otherMonitor)
                .FirstOrDefault(workspace => Equals(workspace["isDisplayed"], true));
            Request(socket, token, "command focus --workspace " + origin);
            if (otherWorkspace != null)
                Request(socket, token, "command focus --workspace " + (string)otherWorkspace["name"]);
        });
    }

    private static async Task WatchScratchpad(ClientWebSocket events, CancellationToken token,
        ScratchpadBackdrop backdrop, string display, Func<string> activeWorkspace, Func<int> revision)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await events.ConnectAsync(new Uri("ws://127.0.0.1:6123"), timeout.Token).ConfigureAwait(false);
                Request(events, timeout.Token, "sub -e focus_changed focused_container_moved window_managed window_unmanaged workspace_updated workspace_deactivated monitor_updated monitor_removed application_exiting");
            }

            while (!token.IsCancellationRequested)
            {
                var observedRevision = revision();
                var name = activeWorkspace();
                Dictionary<string, object> workspace = null;
                var windows = new List<Dictionary<string, object>>();
                WithScratchpadConnection((socket, queryToken) =>
                {
                    var monitor = ScratchpadMonitors(socket, queryToken)
                        .FirstOrDefault(item => Equals(item["deviceName"], display));
                    if (monitor != null) workspace = WorkspaceOnMonitor(monitor, name);
                    if (workspace == null || !Equals(workspace["isDisplayed"], true)) return;
                    CollectWindows(workspace, windows, false);
                    foreach (var window in windows.Where(IsTaskManager))
                        EnsureSpecialWindowIsTopmost(socket, queryToken, window);
                });
                backdrop.BeginInvoke(new Action(() =>
                {
                    // A newer switch may have completed while this query was in flight.
                    if (backdrop.IsDisposed || revision() != observedRevision) return;
                    if (workspace == null || !Equals(workspace["isDisplayed"], true))
                    {
                        backdrop.Close();
                        return;
                    }
                    backdrop.TrackScratchpadWindows(name, windows);
                    backdrop.RefreshStack();
                }));

                WebSocketReceiveResult message;
                do
                {
                    message = await events.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (message.MessageType == WebSocketMessageType.Close) return;
                }
                while (!message.EndOfMessage);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException)
        {
            if (!token.IsCancellationRequested && !backdrop.IsDisposed) throw;
        }
        finally { CloseScratchpadBackdrop(backdrop); }
    }

    private static void CloseScratchpadBackdrop(Form backdrop)
    {
        try { backdrop.BeginInvoke(new Action(backdrop.Close)); }
        catch (InvalidOperationException) { } // The form has already closed.
    }

    private static void WithScratchpadConnection(Action<ClientWebSocket, CancellationToken> action)
    {
        using (var socket = new ClientWebSocket())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            var token = timeout.Token;
            socket.ConnectAsync(new Uri("ws://127.0.0.1:6123"), token).GetAwaiter().GetResult();
            action(socket, token);
            socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token).GetAwaiter().GetResult();
        }
    }

    private static Dictionary<string, object> ScratchpadQuery(ClientWebSocket socket, CancellationToken token,
        string query, string key)
    {
        var response = (Dictionary<string, object>)Request(socket, token, "query " + query)["data"];
        return (Dictionary<string, object>)response[key];
    }

    private static Dictionary<string, object>[] ScratchpadMonitors(ClientWebSocket socket, CancellationToken token)
    {
        var data = (Dictionary<string, object>)Request(socket, token, "query monitors")["data"];
        return ((object[])data["monitors"]).Cast<Dictionary<string, object>>().ToArray();
    }

    private static IEnumerable<Dictionary<string, object>> ScratchpadChildren(Dictionary<string, object> parent)
    {
        return ((object[])parent["children"]).Cast<Dictionary<string, object>>();
    }

    private static Dictionary<string, object> WorkspaceOnMonitor(Dictionary<string, object> monitor, string name)
    {
        return ScratchpadChildren(monitor).FirstOrDefault(item => Equals(item["name"], name));
    }

    private static void ScratchpadWindowCommand(ClientWebSocket socket, CancellationToken token,
        Dictionary<string, object> subject, string command)
    {
        Request(socket, token, "command --id " + Guid.Parse((string)subject["id"]) + " " + command);
    }

    private sealed class ScratchpadBackdrop : Form
    {
        private readonly Bitmap snapshot;
        private readonly System.Windows.Forms.Timer restack = new System.Windows.Forms.Timer { Interval = 40 };
        private string workspaceName;
        private IntPtr[] scratchWindows = new IntPtr[0];
        private IntPtr taskManager;
        private IntPtr[] temporarilyRaised = new IntPtr[0];
        public event Action<string> WorkspaceRequested;
        public event Action DismissRequested;

        public ScratchpadBackdrop(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            Text = BackdropTitle;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Bounds = bounds;
            BackColor = Color.Black;
            snapshot = new Bitmap(bounds.Width, bounds.Height);
            try
            {
                using (var graphics = Graphics.FromImage(snapshot))
                {
                    graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    using (var dim = new SolidBrush(Color.FromArgb(140, Color.Black)))
                        graphics.FillRectangle(dim, 0, 0, bounds.Width, bounds.Height);
                }
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
            DoubleBuffered = true;
            restack.Tick += (sender, args) =>
            {
                restack.Stop();
                RaiseScratchpad();
            };
        }

        public void TrackScratchpadWindows(string name, List<Dictionary<string, object>> windows)
        {
            var handles = windows.Select(window => new IntPtr(Convert.ToInt64(window["handle"]))).ToArray();
            if (workspaceName == name && scratchWindows.SequenceEqual(handles)) return;

            var task = windows.FirstOrDefault(IsTaskManager);
            taskManager = task == null ? IntPtr.Zero : new IntPtr(Convert.ToInt64(task["handle"]));
            var wasEmpty = scratchWindows.Length == 0;
            foreach (var window in temporarilyRaised.Where(window => !handles.Contains(window) && IsWindow(window)))
                SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
            temporarilyRaised = handles.Where(window => window != taskManager &&
                (temporarilyRaised.Contains(window) || (GetWindowStyle(window, -20) & 0x00000008) == 0)).ToArray();
            workspaceName = name;
            scratchWindows = handles;
            if (wasEmpty || scratchWindows.Length == 0) Invalidate();
        }

        private void ReleaseTemporaryStack()
        {
            foreach (var window in temporarilyRaised.Where(IsWindow))
                SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
            temporarilyRaised = new IntPtr[0];
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == CloseSpecialWorkspaceMessage)
            {
                var dismiss = DismissRequested;
                if (dismiss != null) dismiss();
                return;
            }
            if (message.Msg == SwitchSpecialWorkspaceMessage)
            {
                var name = ((char)message.WParam.ToInt32()).ToString();
                var request = WorkspaceRequested;
                if (request != null && IsSpecialWorkspace(name)) request(name);
                return;
            }
            base.WndProc(ref message);
        }

        public void RefreshStack()
        {
            if (IsDisposed) return;
            RaiseScratchpad();
            // GlazeWM can finish restacking after its focus event is published.
            restack.Stop();
            restack.Start();
        }

        private void RaiseScratchpad()
        {
            if (IsDisposed || !IsHandleCreated) return;

            var topmost = new IntPtr(-1);
            const uint reorderOnly = 0x0013; // No move, size, or activation.
            var focused = GetForegroundWindow();
            // GlazeWM can make elevated Task Manager topmost. Position our windows after it
            // without asking the standard-user helper to move Task Manager itself.
            var anchor = taskManager != IntPtr.Zero && IsWindow(taskManager) &&
                (GetWindowStyle(taskManager, -20) & 0x00000008) != 0 ? taskManager : topmost;
            var layers = scratchWindows.Where(window => window != taskManager && IsWindow(window))
                .OrderByDescending(window => window == focused)
                .Concat(new[] { Handle }).ToArray();
            var batch = BeginDeferWindowPos(layers.Length);
            var insertAfter = anchor;
            foreach (var window in layers)
            {
                if (batch == IntPtr.Zero) break;
                batch = DeferWindowPos(batch, window, insertAfter, 0, 0, 0, 0, reorderOnly);
                insertAfter = window;
            }
            if (batch != IntPtr.Zero && EndDeferWindowPos(batch)) return;

            foreach (var window in layers.Reverse())
                SetWindowPos(window, anchor, 0, 0, 0, 0, reorderOnly);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                restack.Dispose();
                ReleaseTemporaryStack();
                if (snapshot != null) snapshot.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawImageUnscaled(snapshot, 0, 0);
            if (scratchWindows.Length != 0) return;

            TextRenderer.DrawText(e.Graphics,
                workspaceName + " is empty\nAlt+" + workspaceName + " to close",
                Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE. The backdrop still intercepts mouse input.
                return parameters;
            }
        }

    }
}
