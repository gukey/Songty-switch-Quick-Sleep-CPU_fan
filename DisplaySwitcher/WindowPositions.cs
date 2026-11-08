using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DisplaySwitcher;

internal static class WindowPositions
{
    private const string SnapshotFile = "window-positions.json";
    private const uint AsyncPlacement = 4, ShowNormal = 1, ShowMinimized = 2, ShowMaximized = 3, ShowNoActivate = 4;

    internal sealed record Position(long Handle, uint ProcessId, uint ThreadId, long ProcessStart,
        string ClassName, Monitor Display, Placement Placement, Rect ScreenRect = default, bool PreferPrimary = false);
    internal sealed record Snapshot(int Session, Position[] Windows);
    private sealed record Pending(Position Original, Monitor Target, IntPtr Window, IntPtr Screen, uint Show);

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

            IntPtr monitor = MonitorFromWindow(window, 2);
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
            {
                GetWindowRect(window, out Rect bounds);
                positions.Add(new(window.ToInt64(), pid, thread, started.Value, className, origin, placement, bounds));
            }
            return true;
        }, IntPtr.Zero);
        return positions.ToArray();
    }

    internal static void RestoreRemembered(Layout layout)
    {
        try
        {
            var snapshot = Data.Read<Snapshot>(SnapshotFile);
            using var process = Process.GetCurrentProcess();
            if (snapshot?.Session == process.SessionId)
            {
                int count = Restore(layout, snapshot.Windows);
                Data.Log($"已核验 {count} 个窗口的位置。");
            }
            if (snapshot is not null)
                Discard();
        }
        catch (Exception error) { Data.Log($"还原窗口位置失败：{error.Message}"); }
    }

    internal static Position[] CaptureOfficeContent(Layout layout)
    {
        using var process = Process.GetCurrentProcess();
        Snapshot? snapshot = null;
        try { snapshot = Data.Read<Snapshot>(SnapshotFile); }
        catch (Exception error) { Data.Log($"读取原窗口位置失败：{error.Message}"); }
        if (snapshot?.Session != process.SessionId)
            snapshot = null;
        if (layout.Extended && snapshot is null)
            return [];

        var originals = snapshot?.Windows.ToDictionary(window => window.Handle) ?? new Dictionary<long, Position>();
        var positions = originals.Values.Where(window => window.Placement.Show != ShowMinimized)
            .ToDictionary(window => window.Handle, window => window with { PreferPrimary = window.Display.Primary });
        foreach (var current in Capture(layout))
        {
            if (originals.TryGetValue(current.Handle, out var original) &&
                original.ProcessId == current.ProcessId && original.ProcessStart == current.ProcessStart &&
                original.ThreadId == current.ThreadId && original.ClassName == current.ClassName)
            {
                if (current.Placement.Show == ShowMinimized && original.Placement.Show != ShowMinimized)
                    continue;
                if (original.Display.Primary)
                    positions[current.Handle] = current with { PreferPrimary = true };
                else
                {
                    var placement = original.Placement;
                    placement.Show = current.Placement.Show;
                    positions[current.Handle] = original with { Placement = placement };
                }
            }
            else if (!layout.Extended && current.Placement.Show != ShowMinimized)
                positions[current.Handle] = current with { PreferPrimary = true };
        }
        return positions.Values.ToArray();
    }

    internal static bool MoveOfficeContent(Layout layout, Position[] current)
    {
        try
        {
            int count = 0;
            if (current.Length > 0)
            {
                count = Restore(layout, current);
                Data.Log($"办公窗口核验：{count}/{current.Length}。");
            }
            Discard();
            return count == current.Length;
        }
        catch (Exception error)
        {
            Data.Log($"应用窗口跟随主屏失败：{error.Message}");
            return false;
        }
    }

    internal static int Restore(Layout layout, IEnumerable<Position> windows)
    {
        var pending = new List<Pending>();
        var primary = layout.Displays.SingleOrDefault(display => display.Primary);
        foreach (var entry in windows.Reverse())
        {
            var target = entry.PreferPrimary ? primary : WindowsDisplays.Resolve(entry.Display.Identity, layout.Displays);
            if (target is null || !entry.PreferPrimary &&
                (target.Width != entry.Display.Width || target.Height != entry.Display.Height ||
                 target.Rotation != entry.Display.Rotation))
                continue;
            var window = new IntPtr(entry.Handle);
            uint thread = GetWindowThreadProcessId(window, out uint pid);
            if (thread != entry.ThreadId || pid != entry.ProcessId ||
                !IsDesktopApp(window, out string className) || className != entry.ClassName ||
                ProcessStart(pid) != entry.ProcessStart || !TryPlacement(window, out var current))
                continue;
            var screen = MonitorFromPoint(new Point
            {
                X = target.X + (int)target.Width / 2,
                Y = target.Y + (int)target.Height / 2
            }, 0);
            if (screen == IntPtr.Zero)
                continue;
            uint show = entry.Placement.Show;
            var request = new Pending(entry, target, window, screen, show);
            if (current.Show == ShowNormal && entry.Placement.Show == ShowNormal)
            {
                MoveToOriginalScreen(request);
                pending.Add(request);
                continue;
            }

            var placement = entry.Placement;
            placement.Flags = AsyncPlacement;
            placement.Show = show is ShowNormal or ShowMaximized ? ShowNoActivate : show;
            int x = target.X - entry.Display.X, y = target.Y - entry.Display.Y;
            placement.Normal.Offset(x, y);
            if (x != 0 || y != 0)
                placement.Maximized = new Point { X = -1, Y = -1 };
            bool placed = SetWindowPlacement(window, ref placement) &&
                (show != ShowMaximized || ShowWindowAsync(window, ShowMaximized));
            if (!placed)
                Data.Log($"窗口位置恢复失败：{className} / {Marshal.GetLastWin32Error()}");
            if (placed || show != ShowMinimized)
                pending.Add(request);
        }
        if (pending.Count == 0)
            return 0;

        // Windows and some apps continue applying DPI/display changes after the paths are active.
        // Check for a short, fixed interval so normal window dragging works again once F4 completes.
        var retried = new HashSet<long>();
        int lastCorrection = -1;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            Thread.Sleep(250);
            foreach (var window in pending.Where(window => !OnTarget(window)))
            {
                lastCorrection = attempt;
                if (retried.Add(window.Original.Handle))
                    Data.Log($"显示切换后补移窗口：{window.Original.ClassName} PID {window.Original.ProcessId} → {window.Target.Name}");
                MoveToOriginalScreen(window);
            }
            if (attempt >= 47 && attempt - lastCorrection >= 5)
                break;
        }
        if (retried.Count > 0)
            Thread.Sleep(300);
        int restored = pending.Count(OnTarget);
        foreach (var window in pending.Where(window => !OnTarget(window)))
            Data.Log($"窗口未回到原屏幕：{window.Original.ClassName} → {window.Target.Name}");
        return restored;
    }

    private static bool OnTarget(Pending window) =>
        IsIconic(window.Window) ? window.Show == ShowMinimized :
            MonitorFromWindow(window.Window, 2) == window.Screen;

    private static void MoveToOriginalScreen(Pending window)
    {
        uint thread = GetWindowThreadProcessId(window.Window, out uint pid);
        if (thread != window.Original.ThreadId || pid != window.Original.ProcessId ||
            !TryPlacement(window.Window, out var current) || window.Show == ShowMinimized)
            return;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(window.Screen, ref info))
            return;
        int workWidth = info.Work.Right - info.Work.Left, workHeight = info.Work.Bottom - info.Work.Top;
        if (workWidth <= 0 || workHeight <= 0)
            return;

        var bounds = window.Original.ScreenRect;
        bounds.Offset(window.Target.X - window.Original.Display.X, window.Target.Y - window.Original.Display.Y);
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        bool maximize = window.Show == ShowMaximized;
        if (maximize || width <= 0 || height <= 0 || window.Original.Placement.Show != ShowNormal)
        {
            // Place the restored window inside the known target before asking Windows to maximize it again.
            bounds.Left = info.Work.Left + workWidth / 4;
            bounds.Top = info.Work.Top + workHeight / 4;
            width = workWidth / 2;
            height = workHeight / 2;
        }
        width = Math.Clamp(width, 1, workWidth);
        height = Math.Clamp(height, 1, workHeight);
        int x = Math.Clamp(bounds.Left, info.Work.Left, info.Work.Right - width);
        int y = Math.Clamp(bounds.Top, info.Work.Top, info.Work.Bottom - height);
        if (current.Show == ShowNormal && GetWindowRect(window.Window, out var actual) &&
            actual.Left == x && actual.Top == y && actual.Right - x == width && actual.Bottom - y == height)
            return;
        if (current.Show is ShowMaximized or ShowMinimized)
        {
            current.Flags = AsyncPlacement;
            current.Show = ShowNoActivate;
            SetWindowPlacement(window.Window, ref current);
        }
        const uint asyncWithoutActivation = 0x4000 | 0x200 | 0x10 | 0x4;
        // Our WPF windows own their logical size. Replaying a physical size captured
        // on another DPI can shrink the fixed layout after the monitor transition.
        uint flags = asyncWithoutActivation;
        if (pid == (uint)Environment.ProcessId)
            flags |= 0x1; // SWP_NOSIZE; WPF handles the destination monitor's DPI.
        if (SetWindowPos(window.Window, IntPtr.Zero, x, y, width, height, flags) && maximize)
            ShowWindowAsync(window.Window, ShowMaximized);
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
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int maximum);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
