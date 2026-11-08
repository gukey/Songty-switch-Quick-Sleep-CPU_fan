using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplaySwitcher;

internal static class WindowPositions
{
    private const string SnapshotFile = "window-positions.json";
    private const uint AsyncPlacement = 4, ShowNormal = 1, ShowMaximized = 3, ShowNoActivate = 4;

    internal sealed record Position(long Handle, uint ProcessId, uint ThreadId, long ProcessStart,
        string ClassName, Monitor Display, Placement Placement);
    internal sealed record Snapshot(int Session, Position[] Windows);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;

        internal void Offset(int x, int y)
        {
            Left += x;
            Right += x;
            Top += y;
            Bottom += y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Placement
    {
        public uint Length, Flags, Show;
        public Point Minimized, Maximized;
        public Rect Normal;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    internal static void Remember(Layout layout)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var windows = Capture(layout);
            Data.Save(SnapshotFile, new Snapshot(process.SessionId, windows));
            Data.Log($"已记录 {windows.Length} 个窗口的位置。");
        }
        catch (Exception error) { Data.Log($"记录窗口位置失败：{error.Message}"); }
    }

    internal static Position[] Capture(Layout layout)
    {
        var positions = new List<Position>();
        var startTimes = new Dictionary<uint, long?>();
        EnumWindows((window, _) =>
        {
            if (!IsDesktopApp(window, out string className))
                return true;

            IntPtr monitor = MonitorFromWindow(window, 0);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
                return true;
            string output = info.Device.Replace(@"\\.\", "");
            var origins = layout.Displays.Where(display => string.Equals(display.Output, output,
                StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (origins is not [var origin])
                return true;

            uint thread = GetWindowThreadProcessId(window, out uint pid);
            if (!startTimes.TryGetValue(pid, out long? started))
            {
                started = ProcessStart(pid);
                startTimes.Add(pid, started);
            }
            if (thread != 0 && started is not null && TryPlacement(window, out var placement))
                positions.Add(new(window.ToInt64(), pid, thread, started.Value, className, origin, placement));
            return true;
        }, IntPtr.Zero);
        return positions.ToArray();
    }

    internal static void RestoreRemembered(Layout layout)
    {
        try
        {
            var snapshot = Data.Read<Snapshot>(SnapshotFile);
            if (snapshot is null)
                return;
            using var process = Process.GetCurrentProcess();
            if (snapshot.Session == process.SessionId)
            {
                int count = Restore(layout, snapshot.Windows);
                Data.Log($"已请求还原 {count} 个窗口的位置。");
            }
            Discard();
        }
        catch (Exception error) { Data.Log($"还原窗口位置失败：{error.Message}"); }
    }

    internal static int Restore(Layout layout, IEnumerable<Position> windows)
    {
        int restored = 0;
        foreach (var entry in windows.Reverse())
        {
            var target = WindowsDisplays.Resolve(entry.Display.Identity, layout.Displays);
            if (target is null || target.Width != entry.Display.Width || target.Height != entry.Display.Height ||
                target.Rotation != entry.Display.Rotation)
                continue;
            var window = new IntPtr(entry.Handle);
            uint thread = GetWindowThreadProcessId(window, out uint pid);
            if (thread != entry.ThreadId || pid != entry.ProcessId ||
                !IsDesktopApp(window, out string className) || className != entry.ClassName ||
                ProcessStart(pid) != entry.ProcessStart || !TryPlacement(window, out var current))
                continue;

            var placement = entry.Placement;
            placement.Flags = AsyncPlacement;
            placement.Show = current.Show is ShowNormal or ShowMaximized ? ShowNoActivate : current.Show;
            int x = target.X - entry.Display.X, y = target.Y - entry.Display.Y;
            placement.Normal.Offset(x, y);
            if (x != 0 || y != 0)
                placement.Maximized = new Point { X = -1, Y = -1 };
            if (SetWindowPlacement(window, ref placement) &&
                (current.Show != ShowMaximized || ShowWindowAsync(window, ShowMaximized)))
                restored++;
            else
                Data.Log($"窗口位置恢复失败：{className} / {Marshal.GetLastWin32Error()}");
        }
        return restored;
    }

    internal static void Discard()
    {
        try { File.Delete(Path.Combine(Data.Root, SnapshotFile)); }
        catch (Exception error) { Data.Log($"清理窗口快照失败：{error.Message}"); }
    }

    private static bool IsDesktopApp(IntPtr window, out string className)
    {
        className = "";
        if (!IsWindowVisible(window) || GetWindowTextLengthW(window) == 0 ||
            (GetWindowLongW(window, -20) & (0x80 | 0x08000000)) != 0 ||
            GetWindow(window, 4) != IntPtr.Zero ||
            (DwmGetWindowAttribute(window, 14, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0))
            return false;
        var buffer = new StringBuilder(256);
        if (GetClassNameW(window, buffer, buffer.Capacity) == 0)
            return false;
        className = buffer.ToString();
        return className is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private static bool TryPlacement(IntPtr window, out Placement placement)
    {
        placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        return GetWindowPlacement(window, ref placement);
    }

    private static long? ProcessStart(uint id)
    {
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int maximum);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(IntPtr window, uint command);
}
