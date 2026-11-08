using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DisplaySwitcher;

public sealed record DisplayIdentity(string Path, string Product, string? Serial, string? EdidHash);
public sealed record Monitor(DisplayIdentity Identity, string Name, string? Output, bool Active,
    uint Width, uint Height, int X, int Y, uint Rotation, double Hertz)
{
    public bool Primary => Active && X == 0 && Y == 0;
    public string Label => $"{(Active ? Output ?? "活动" : "未启用")}  ·  {Name}";
    public string Details => $"设备码 {Identity.Product}\n序列号 {Identity.Serial ?? "未提供"}\n{Identity.Path}";
}

public sealed record Layout(WindowsDisplays.Path[] Paths, WindowsDisplays.Mode[] Modes, Monitor[] Displays)
{
    public bool Extended => Displays.Length > 1 &&
        Displays.Select(display => (display.X, display.Y)).Distinct().Count() == Displays.Length;
}

public sealed class WindowsDisplays
{
    private const uint Awareness = 0x10 | 0x40;
    private const uint SetAwareness = 0x8000 | 0x20000;
    private const uint Active = 1, Virtual = 8;
    private const uint InvalidIndex = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Source { public Luid Adapter; public uint Id, ModeIndex, Status; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Target
    {
        public Luid Adapter;
        public uint Id, ModeIndex;
        public int Output;
        public uint Rotation, Scaling, RateNumerator, RateDenominator, Scan;
        public int Available;
        public uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Path { public Source Source; public Target Target; public uint Flags; }

    // DISPLAYCONFIG_MODE_INFO contains a 48-byte union. Retain its complete
    // binary representation, including target timings and desktop image mode.
    [StructLayout(LayoutKind.Sequential)]
    public struct Mode
    {
        public uint Type, Id;
        public Luid Adapter;
        public uint A, B, C, D, E, F, G, H, I, J, K, L;
        public readonly int X => unchecked((int)D);
        public readonly int Y => unchecked((int)E);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags;
        public int Output;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Device;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] Path[] pathArray,
        ref uint modes, [Out] Mode[] modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSource(ref SourceName data);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTarget(ref TargetName data);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, Path[] paths, uint modeCount, Mode[] modes, uint flags);

    private static (Path[] Paths, Mode[] Modes) Query(uint kind)
    {
        uint flags = kind | Awareness;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount));
            var paths = new Path[pathCount];
            var modes = new Mode[modeCount];
            int code = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (code == 122)
                continue;
            Check(code);
            return (paths[..(int)pathCount], modes[..(int)modeCount]);
        }
        throw new InvalidOperationException("显示器连接正在变化，请稍后重试。");
    }

    private static void Check(int code)
    {
        if (code != 0)
            throw new Win32Exception(code);
    }

    public Layout Capture()
    {
        var (paths, modes) = Query(2);
        return new Layout(paths, modes, paths.Select(path => Describe(path, modes)).ToArray());
    }

    public Monitor[] Connected()
    {
        var (paths, modes) = Query(1);
        return paths.Where(path => path.Target.Available != 0 || (path.Flags & Active) != 0)
            .GroupBy(path => (path.Target.Adapter, path.Target.Id))
            .Select(group => Describe(group.OrderByDescending(path => (path.Flags & Active) != 0).First(), modes))
            .Where(display => !string.IsNullOrWhiteSpace(display.Identity.Path)).ToArray();
    }

    private static Header HeaderFor<T>(uint type, Luid adapter, uint id) =>
        new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), Adapter = adapter, Id = id };

    private static Monitor Describe(Path path, Mode[] modes)
    {
        var target = new TargetName { Header = HeaderFor<TargetName>(2, path.Target.Adapter, path.Target.Id) };
        if (GetTarget(ref target) != 0)
            target.Device = "";
        var identity = Identify(target.Device ?? "");
        bool active = (path.Flags & Active) != 0;
        var source = new SourceName { Header = HeaderFor<SourceName>(1, path.Source.Adapter, path.Source.Id) };
        string? output = active && GetSource(ref source) == 0 ? source.Name?.Replace(@"\\.\", "") : null;
        uint index = SourceIndex(path);
        Mode mode = active && index < modes.Length ? modes[index] : default;
        double hz = path.Target.RateDenominator == 0 ? 0 :
            (double)path.Target.RateNumerator / path.Target.RateDenominator;
        string name = (target.Name ?? "").Replace("*30", "").Trim();
        return new Monitor(identity, name.Length == 0 ? identity.Product : name, output, active,
            mode.A, mode.B, mode.X, mode.Y, path.Target.Rotation, hz);
    }

    private static DisplayIdentity Identify(string path)
    {
        var parts = path.Split('#');
        string product = parts.Length >= 3 ? parts[1] : "";
        if (parts.Length < 3)
            return new(path, product, null, null);
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{product}\{parts[2]}\Device Parameters");
        if (key?.GetValue("EDID") is not byte[] { Length: >= 128 } edid)
            return new(path, product, null, null);
        string? serial = null;
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] == 0 && edid[offset + 1] == 0 &&
                edid[offset + 2] == 0 && edid[offset + 3] == 255)
            {
                serial = Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\r', '\n', ' ');
                if (serial.Length == 0 || serial.All(character => character == '0'))
                    serial = null;
                break;
            }
        }
        uint numeric = BitConverter.ToUInt32(edid, 12);
        if (serial is null && numeric is not (0 or 0x01010101 or uint.MaxValue))
            serial = numeric.ToString("X8");
        return new(path, product, serial, Convert.ToHexString(SHA256.HashData(edid)));
    }

    public static Monitor? Resolve(DisplayIdentity identity, IEnumerable<Monitor> displays)
    {
        var compatible = displays.Where(display =>
            Same(display.Identity.Product, identity.Product) &&
            (identity.Serial is null || display.Identity.Serial is null ||
             Same(display.Identity.Serial, identity.Serial))).ToArray();
        var serialMatches = identity.Serial is null ? [] :
            compatible.Where(display => Same(display.Identity.Serial, identity.Serial)).ToArray();
        if (serialMatches.Length == 1)
            return serialMatches[0];
        var exact = compatible.Where(display => Same(display.Identity.Path, identity.Path)).ToArray();
        if (exact.Length == 1)
            return exact[0];
        var edidMatches = identity.EdidHash is null ? [] :
            compatible.Where(display => Same(display.Identity.EdidHash, identity.EdidHash)).ToArray();
        return edidMatches.Length == 1 ? edidMatches[0] : null;
    }

    private static bool Same(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static uint SourceIndex(Path path) => (path.Flags & Virtual) != 0 ?
        (path.Source.ModeIndex >> 16) == 0xffff ? InvalidIndex : path.Source.ModeIndex >> 16 :
        path.Source.ModeIndex;

    public Layout Single(Layout current, Monitor display)
    {
        int index = Array.FindIndex(current.Displays, item => Same(item.Identity.Path, display.Identity.Path));
        if (index < 0)
            throw new InvalidOperationException("电视还没有有效的桌面信号。");
        return Select(current, [index]);
    }

    public Layout AvailableOffice(Layout office)
    {
        var connected = Connected();
        int[] available = Enumerable.Range(0, office.Displays.Length)
            .Where(index => Resolve(office.Displays[index].Identity, connected) is not null).ToArray();
        if (available.Length == 0)
            throw new InvalidOperationException("未检测到任何已保存的办公显示器。");
        return Rebind(Select(office, available));
    }

    private static Layout Select(Layout current, int[] indices)
    {
        Monitor primary = indices.Select(index => current.Displays[index]).FirstOrDefault(display => display.Primary)
            ?? current.Displays[indices[0]];
        var modes = new List<Mode>();
        var paths = new List<Path>();
        var displays = new List<Monitor>();
        foreach (int index in indices)
        {
            var path = current.Paths[index];
            var display = current.Displays[index];
            int x = display.X - primary.X, y = display.Y - primary.Y;
            uint sourceIndex = SourceIndex(path);
            if (sourceIndex >= current.Modes.Length)
                throw new InvalidOperationException("选定的桌面模式不可用。");
            var source = current.Modes[sourceIndex];
            source.D = unchecked((uint)x);
            source.E = unchecked((uint)y);
            uint nextSource = (uint)modes.Count;
            modes.Add(source);
            if ((path.Flags & Virtual) != 0)
            {
                path.Source.ModeIndex = (nextSource << 16) | 0xffff;
                uint targetIndex = CopyMode(modes, current.Modes, path.Target.ModeIndex >> 16, 0xffff);
                uint desktopIndex = CopyMode(modes, current.Modes, path.Target.ModeIndex & 0xffff, 0xffff);
                path.Target.ModeIndex = (targetIndex << 16) | desktopIndex;
            }
            else
            {
                path.Source.ModeIndex = nextSource;
                path.Target.ModeIndex = CopyMode(modes, current.Modes, path.Target.ModeIndex, InvalidIndex);
            }
            paths.Add(path);
            displays.Add(display with { X = x, Y = y });
        }
        return new(paths.ToArray(), modes.ToArray(), displays.ToArray());
    }

    private static uint CopyMode(List<Mode> destination, Mode[] source, uint index, uint invalid)
    {
        if (index >= source.Length)
            return invalid;
        uint result = (uint)destination.Count;
        destination.Add(source[index]);
        return result;
    }

    public Layout Rebind(Layout layout)
    {
        var (availablePaths, modes) = Query(1);
        var available = availablePaths.Where(path => path.Target.Available != 0).Select(path =>
            (Path: path, Monitor: Describe(path, modes))).ToArray();
        var connected = available.Select(entry => entry.Monitor)
            .Where(display => display.Identity.Path.Length > 0)
            .DistinctBy(display => display.Identity.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var destinations = layout.Displays.Select(saved => Resolve(saved.Identity, connected) ??
            throw new InvalidOperationException($"找不到已保存的显示器「{saved.Name}」。")).ToArray();
        if (destinations.Select(display => display.Identity.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            destinations.Length)
            throw new InvalidOperationException("两个已保存的显示器匹配到了同一个设备。");

        var remapped = layout.Paths.ToArray();
        var remappedModes = layout.Modes.ToArray();
        var assigned = new Dictionary<(Luid, uint), (Luid, uint)>();
        for (int index = 0; index < remapped.Length; index++)
        {
            var path = remapped[index];
            var oldSource = (path.Source.Adapter, path.Source.Id);
            var choices = available.Where(entry => Same(entry.Monitor.Identity.Path, destinations[index].Identity.Path))
                .Select(entry => entry.Path).ToArray();
            Path[] valid = assigned.TryGetValue(oldSource, out var mapped)
                ? choices.Where(choice => (choice.Source.Adapter, choice.Source.Id).Equals(mapped)).ToArray()
                : choices.Where(choice => !assigned.ContainsValue((choice.Source.Adapter, choice.Source.Id))).ToArray();
            if (valid.Length == 0)
                throw new InvalidOperationException("当前显卡无法分配已保存的显示路径。");
            var picked = valid.OrderByDescending(choice => (choice.Source.Adapter, choice.Source.Id).Equals(oldSource))
                .ThenByDescending(choice => (choice.Flags & Active) != 0).First();
            if ((path.Flags & Virtual) != (picked.Flags & Virtual))
                throw new InvalidOperationException("显示驱动的虚拟模式能力已变化，请重新保存布局。");
            assigned[oldSource] = (picked.Source.Adapter, picked.Source.Id);
            path.Source.Adapter = picked.Source.Adapter;
            path.Source.Id = picked.Source.Id;
            path.Target.Adapter = picked.Target.Adapter;
            path.Target.Id = picked.Target.Id;
            path.Target.Available = picked.Target.Available;
            path.Target.Output = picked.Target.Output;
            RemapMode(remappedModes, SourceIndex(path), path.Source.Adapter, path.Source.Id);
            if ((path.Flags & Virtual) != 0)
            {
                RemapMode(remappedModes, path.Target.ModeIndex >> 16, path.Target.Adapter, path.Target.Id);
                uint desktopIndex = path.Target.ModeIndex & 0xffff;
                if (desktopIndex < remappedModes.Length)
                {
                    // Desktop-image records can carry a source ID. Preserve the original association.
                    var imageMode = remappedModes[desktopIndex];
                    bool fromSource = (imageMode.Adapter, imageMode.Id).Equals(oldSource);
                    RemapMode(remappedModes, desktopIndex, fromSource ? path.Source.Adapter : path.Target.Adapter,
                        fromSource ? path.Source.Id : path.Target.Id);
                }
            }
            else
            {
                RemapMode(remappedModes, path.Target.ModeIndex, path.Target.Adapter, path.Target.Id);
            }
            remapped[index] = path;
        }
        return new(remapped, remappedModes, layout.Displays.Select((saved, index) =>
            saved with { Identity = destinations[index].Identity }).ToArray());
    }

    private static void RemapMode(Mode[] modes, uint index, Luid adapter, uint id)
    {
        if (index >= modes.Length)
            return;
        modes[index].Adapter = adapter;
        modes[index].Id = id;
    }

    public void Validate(Layout layout) => Set(layout, 0x40);
    public void Apply(Layout layout) => Set(layout, 0x80 | 0x200);

    private static void Set(Layout layout, uint action)
    {
        if (layout.Paths.Length == 0 || layout.Paths.Length != layout.Displays.Length)
            throw new InvalidOperationException("布局没有可验证的活动显示器。");
        Check(SetDisplayConfig((uint)layout.Paths.Length, layout.Paths, (uint)layout.Modes.Length,
            layout.Modes, SetAwareness | 0x20 | action));
    }

    public static bool Matches(Layout actual, Layout expected)
    {
        if (actual.Displays.Length != expected.Displays.Length)
            return false;
        foreach (var saved in expected.Displays)
        {
            var display = Resolve(saved.Identity, actual.Displays);
            if (display is null || display.X != saved.X || display.Y != saved.Y ||
                display.Width != saved.Width || display.Height != saved.Height ||
                display.Rotation != saved.Rotation || Math.Abs(display.Hertz - saved.Hertz) > 0.15)
                return false;
        }
        return true;
    }
}
