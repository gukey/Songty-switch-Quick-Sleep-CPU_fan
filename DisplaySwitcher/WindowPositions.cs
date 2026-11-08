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
    internal static IntPtr SwitcherWindow { private get; set; }

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
        var originals = snapshot?.Windows.ToDictionary(window => window.Handle) ?? new Dictionary<long, Position>();
        var positions = originals.Values.Where(window => !layout.Extended &&
            GetWindowThreadProcessId(new IntPtr(window.Handle), out uint pid) == window.ThreadId &&
            pid == window.ProcessId && ProcessStart(pid) == window.ProcessStart)
            .ToDictionary(window => window.Handle, window => window with { PreferPrimary = true });
        foreach (var current in Capture(layout))
        {
            if (!layout.Extended && originals.TryGetValue(current.Handle, out var original) &&
                original.ProcessId == current.ProcessId && original.ProcessStart == current.ProcessStart &&
                original.ThreadId == current.ThreadId && original.ClassName == current.ClassName)
            {
                if (current.Placement.Show == ShowMinimized && original.Placement.Show != ShowMinimized)
                    continue;
                positions[current.Handle] = original with {
                    Placement = original.Placement with { Show = current.Placement.Show }, PreferPrimary = true };
            }
            else
                positions[current.Handle] = current with { PreferPrimary = true };
        }
        return positions.Values.ToArray();
    }

    internal static bool MoveOfficeContent(Layout layout, Position[] current)
    {
        try
        {
            var result = RestoreWindows(layout, current, trackNewWindows: true);
            Data.Log($"应用窗口核验：{result.Restored}/{result.Expected}。");
            return result.Restored == result.Expected;
        }
        catch (Exception error)
        {
            Data.Log($"应用窗口跟随主屏失败：{error.Message}");
            return false;
        }
    }

    internal static int Restore(Layout layout, IEnumerable<Position> windows) =>
        RestoreWindows(layout, windows, trackNewWindows: false).Restored;

    private static (int Restored, int Expected) RestoreWindows(Layout layout, IEnumerable<Position> windows, bool trackNewWindows)
    {
        var pending = new Dictionary<long, Pending>();
        var expected = new HashSet<long>();
        var primary = layout.Displays.SingleOrDefault(display => display.Primary);
        void Queue(Position entry)
        {
            var target = entry.PreferPrimary ? primary : WindowsDisplays.Resolve(entry.Display.Identity, layout.Displays);
            if (target is null || !entry.PreferPrimary &&
                (target.Width != entry.Display.Width || target.Height != entry.Display.Height ||
                 target.Rotation != entry.Display.Rotation))
                return;
            var window = new IntPtr(entry.Handle);
            uint thread = GetWindowThreadProcessId(window, out uint pid);
            if (thread != entry.ThreadId || pid != entry.ProcessId ||
                !IsDesktopApp(window, out string className) || className != entry.ClassName ||
                ProcessStart(pid) != entry.ProcessStart || !TryPlacement(window, out var current))
                return;
            expected.Add(entry.Handle);
            var screen = MonitorFromPoint(new Point
            {
                X = target.X + (int)target.Width / 2,
                Y = target.Y + (int)target.Height / 2
            }, 0);
            if (screen == IntPtr.Zero)
                return;
            uint show = entry.Placement.Show;
            var request = new Pending(entry, target, window, screen, show);
            pending[entry.Handle] = request;
            if (current.Show == show && OnTarget(request))
                return;
            if (current.Show == ShowNormal && entry.Placement.Show == ShowNormal)
            {
                MoveToOriginalScreen(request);
                return;
            }

            var placement = entry.Placement;
            placement.Flags = (placement.Flags & 2) | AsyncPlacement;
            placement.Show = show is ShowNormal or ShowMaximized ? ShowNoActivate : show;
            int x = target.X - entry.Display.X, y = target.Y - entry.Display.Y;
            placement.Normal.Offset(x, y);
            if (x != 0 || y != 0)
                placement.Maximized = new Point { X = -1, Y = -1 };
            bool placed = SetWindowPlacement(window, ref placement) &&
                (show != ShowMaximized || ShowWindowAsync(window, ShowMaximized));
            if (!placed)
                Data.Log($"窗口位置恢复失败：{className} / {Marshal.GetLastWin32Error()}");
        }
        foreach (var entry in windows.Reverse())
            Queue(entry);
        if (pending.Count == 0 && !trackNewWindows)
            return (0, expected.Count);

        // Windows and some apps continue applying DPI/display changes after the paths are active.
        // Check for a short, fixed interval so normal window dragging works again once F4 completes.
        var retried = new HashSet<long>();
        int lastCorrection = -1;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            Thread.Sleep(250);
            if (trackNewWindows && attempt % 4 == 3)
            {
                // An app can recreate or reveal its main window after the topology settles.
                foreach (var entry in Capture(layout))
                {
                    if (pending.TryGetValue(entry.Handle, out var known) &&
                        known.Original.ProcessId == entry.ProcessId && known.Original.ProcessStart == entry.ProcessStart &&
                        known.Original.ThreadId == entry.ThreadId)
                        continue;
                    Queue(entry with { PreferPrimary = true });
                    lastCorrection = attempt;
                }
            }
            foreach (var window in pending.Values.Where(window => !OnTarget(window)))
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
        int restored = pending.Values.Count(OnTarget);
        foreach (var window in pending.Values.Where(window => !OnTarget(window)))
            Data.Log($"窗口未回到原屏幕：{window.Original.ClassName} → {window.Target.Name}");
        return (restored, expected.Count);
    }

    private static bool OnTarget(Pending window)
    {
        if (!IsIconic(window.Window))
        {
            if (!IsWindowVisible(window.Window) || MonitorFromWindow(window.Window, 0) != window.Screen ||
                !GetWindowRect(window.Window, out var bounds))
                return false;
            return MonitorFromPoint(new Point { X = (bounds.Left + bounds.Right) / 2,
                Y = (bounds.Top + bounds.Bottom) / 2 }, 0) == window.Screen;
        }
        if (window.Show != ShowMinimized || !TryPlacement(window.Window, out var placement))
            return false;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(window.Screen, ref info))
            return false;
        // WINDOWPLACEMENT uses workspace coordinates; check the restore rectangle,
        // not the iconic position or merely the minimized state.
        var center = new Point {
            X = (placement.Normal.Left + placement.Normal.Right) / 2 + info.Work.Left - info.Bounds.Left,
            Y = (placement.Normal.Top + placement.Normal.Bottom) / 2 + info.Work.Top - info.Bounds.Top };
        return MonitorFromPoint(center, 0) == window.Screen;
    }

    internal static void CenterOnPrimary(IntPtr window, double logicalWidth, double logicalHeight)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromPoint(new Point(), 1), ref info))
            return;
        for (int pass = 0; pass < 2; pass++)
        {
            uint dpi = GetDpiForWindow(window);
            if (dpi == 0)
                return;
            int width = (int)Math.Round(logicalWidth * dpi / 96);
            int height = (int)Math.Round(logicalHeight * dpi / 96);
            int x = info.Work.Left + Math.Max(0, (info.Work.Right - info.Work.Left - width) / 2);
            int y = info.Work.Top + Math.Max(0, (info.Work.Bottom - info.Work.Top - height) / 2);
            SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x4 | 0x10);
            if (dpi == GetDpiForWindow(window))
                break;
        }
    }

    private static void MoveToOriginalScreen(Pending window)
    {
        uint thread = GetWindowThreadProcessId(window.Window, out uint pid);
        if (thread != window.Original.ThreadId || pid != window.Original.ProcessId ||
            !TryPlacement(window.Window, out var current))
            return;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(window.Screen, ref info))
            return;
        int workWidth = info.Work.Right - info.Work.Left, workHeight = info.Work.Bottom - info.Work.Top;
        if (workWidth <= 0 || workHeight <= 0)
            return;

        var bounds = window.Original.Placement.Show == ShowMinimized
            ? window.Original.Placement.Normal : window.Original.ScreenRect;
        bounds.Offset(window.Target.X - window.Original.Display.X, window.Target.Y - window.Original.Display.Y);
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        bool maximize = window.Show == ShowMaximized;
        if (maximize || width <= 0 || height <= 0 || window.Original.Placement.Show is not (ShowNormal or ShowMinimized))
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
        if (window.Show == ShowMinimized && IsIconic(window.Window))
        {
            current.Normal = new Rect { Left = x - (info.Work.Left - info.Bounds.Left),
                Top = y - (info.Work.Top - info.Bounds.Top),
                Right = x - (info.Work.Left - info.Bounds.Left) + width,
                Bottom = y - (info.Work.Top - info.Bounds.Top) + height };
            current.Flags = (current.Flags & 2) | AsyncPlacement;
            SetWindowPlacement(window.Window, ref current);
            return;
        }
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
        if (window == SwitcherWindow || !IsWindowVisible(window) || GetWindowTextLengthW(window) == 0 ||
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
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
