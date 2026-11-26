// Build with .NET Framework csc.exe /target:exe watch-virtual-desktop.cs.
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class WatchVirtualDesktop
{
    private const string DesktopKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const int NotifyLastSet = 4;

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

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    private static void SetBarsVisible(bool visible)
    {
        var processes = Process.GetProcessesByName("zebar");
        try
        {
            var processIds = processes.Select(process => process.Id).ToArray();
            EnumWindows((window, parameter) =>
            {
                int processId;
                GetWindowThreadProcessId(window, out processId);
                if (!processIds.Contains(processId) || IsWindowVisible(window) == visible) return true;

                var title = new StringBuilder(128);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString() == "Zebar - wallpaper-bar / bar")
                    ShowWindowAsync(window, visible ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static int Main()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(DesktopKey))
        using (var changed = new AutoResetEvent(false))
        using (var closed = new ManualResetEvent(false))
        {
            if (key == null) return 1;
            var reader = new Thread(() =>
            {
                Console.In.ReadToEnd();
                closed.Set();
            }) { IsBackground = true };
            reader.Start();

            bool? previous = null;
            while (true)
            {
                if (RegNotifyChangeKeyValue(key.Handle, false, NotifyLastSet,
                    changed.SafeWaitHandle, true) != 0) return 1;

                var desktops = key.GetValue("VirtualDesktopIDs") as byte[];
                var current = key.GetValue("CurrentVirtualDesktop") as byte[];
                bool? visible = desktops == null || desktops.Length <= 16 ? true :
                    current != null && current.Length == 16 ? desktops.Take(16).SequenceEqual(current) : (bool?)null;
                if (visible.HasValue && visible != previous)
                {
                    SetBarsVisible(visible.Value);
                    previous = visible;
                }

                if (WaitHandle.WaitAny(new WaitHandle[] { changed, closed }) == 1) return 0;
            }
        }
    }
}
