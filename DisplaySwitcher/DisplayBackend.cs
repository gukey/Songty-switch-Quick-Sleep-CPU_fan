using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DisplaySwitcher;

public sealed record AppSettings(DisplayIdentity? Tv = null, int Key = 0x72, uint Modifiers = 0);
public sealed record SwitchResult(bool Success, string Message);
public sealed record Profile(Layout Layout, string Config);

public static class Data
{
    public static readonly string Root = Environment.GetEnvironmentVariable("DISPLAY_SWITCHER_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplaySwitcher");
    private static readonly object LogLock = new();
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public static T? Read<T>(string name) where T : class
    {
        string path = Path.Combine(Root, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
    }

    public static void Save<T>(string name, T data) => Write(name, JsonSerializer.Serialize(data, Json));

    public static void Write(string name, string contents)
    {
        Directory.CreateDirectory(Root);
        string destination = Path.Combine(Root, name);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, destination, true);
        }
        finally { File.Delete(temp); }
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                string folder = Path.Combine(Root, "logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"switch-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public interface IDisplayBackend
{
    Task<Monitor[]> GetDisplaysAsync();
    Task<SwitchResult> SwitchToTvAsync(DisplayIdentity tv, Func<Task<bool>> confirm);
    Task<SwitchResult> RestoreOfficeAsync();
    Task<SwitchResult> SaveOfficeLayoutAsync();
}

public sealed class MultiMonitorToolBackend(WindowsDisplays native) : IDisplayBackend
{
    internal const string TvUnavailable = "未检测到电视，请开启电视并连接 HDMI 后再试。";
    private static readonly string ToolPath = Path.Combine(AppContext.BaseDirectory, "MultiMonitorTool", "MultiMonitorTool.exe");
    public static bool ToolAvailable => File.Exists(ToolPath);
    private readonly SemaphoreSlim gate = new(1);
    public bool Busy => gate.CurrentCount == 0;
    public bool HasOffice => File.Exists(Path.Combine(Data.Root, "office.json"));
    public Task<Monitor[]> GetDisplaysAsync() => Task.Run(native.Connected);
    public Task<Layout> CaptureAsync() => Task.Run(native.Capture);

    public async Task<SwitchResult> SaveOfficeLayoutAsync() => await Exclusive(async () =>
    {
        Layout current = native.Capture();
        if (!current.Extended)
            throw new InvalidOperationException("请先在 Windows 中配置独立的多屏扩展布局，再保存办公配置。");
        native.Validate(current);
        StoreOffice(current, await CaptureConfig());
        return new(true, "办公布局已保存。");
    });

    public async Task<SwitchResult> SwitchToTvAsync(DisplayIdentity tv, Func<Task<bool>> confirm) =>
        await Exclusive(async () =>
    {
        var connected = native.Connected();
        Monitor target = WindowsDisplays.Resolve(tv, connected) ??
            throw new InvalidOperationException(TvUnavailable);
        Layout before = native.Capture();
        native.Validate(before);
        string checkpoint = await CaptureConfig();
        Profile office = before.Extended ? StoreOffice(before, checkpoint) : ReadOffice();
        native.Validate(native.Rebind(office.Layout));
        Profile? gaming = Data.Read<Profile>("gaming.json");
        bool confirmedBefore = gaming?.Layout.Displays is [var savedTv] &&
            WindowsDisplays.Resolve(savedTv.Identity, [target]) is not null;
        if (before.Extended)
            WindowPositions.Remember(before);
        bool started = false;
        try
        {
            if (!target.Active)
            {
                if (connected.Count(m => m.Identity.Product == target.Identity.Product) != 1)
                    throw new InvalidOperationException("多台同型号设备，无法安全启用该电视。请先在 Windows 中启用它。");
                started = true;
                await RunTool("/enable", target.Identity.Product);
                await WaitFor(layout =>
                    WindowsDisplays.Resolve(tv, layout.Displays) is not null &&
                    before.Displays.All(item => WindowsDisplays.Resolve(item.Identity, layout.Displays) is not null));
            }
            Layout active = native.Capture();
            target = WindowsDisplays.Resolve(tv, active.Displays) ??
                throw new InvalidOperationException("未检测到电视桌面输出。");
            Layout desired = native.Single(active, target);
            native.Validate(desired);
            string activeConfig = started ? await CaptureConfig() : checkpoint;
            string? candidate = confirmedBefore && gaming is not null && SameBindings(gaming.Layout, desired) &&
                WindowsDisplays.Matches(gaming.Layout, desired) && SameKnownOutputs(gaming.Config, active)
                ? gaming.Config
                : active.Extended ? TvConfig(activeConfig, active, target) : null;
            if (active.Displays.Length > 1)
            {
                started = true;
                await ApplyNativeFirst(desired, candidate, temporary: true);
            }
            if (!confirmedBefore && !await confirm())
            {
                await RollBack(before, checkpoint, office);
                return new(false, "测试结束，已恢复切换前的布局。");
            }
            Layout actual = native.Capture();
            if (!WindowsDisplays.Matches(actual, desired))
                throw new InvalidOperationException("电视显示状态在确认期间发生变化。");
            if (WindowsDisplays.Resolve(tv, native.Connected()) is null)
                throw new InvalidOperationException(TvUnavailable);
            string config = await CaptureConfig();
            Data.Save("gaming.json", new Profile(actual, config));
            Data.Write("gaming.cfg", config);
            bool moved = WindowPositions.MoveOfficeContent(actual, WindowPositions.CaptureOfficeContent(actual));
            return new(moved, moved ? "电视模式已启用，所有应用窗口已迁到电视。" : "电视模式已启用，部分窗口未迁移，请重试或按 F4 恢复。");
        }
        catch
        {
            if (started)
                await RollBack(before, checkpoint, office);
            else if (before.Extended)
                WindowPositions.Discard();
            throw;
        }
    });

    public async Task<SwitchResult> RestoreOfficeAsync() => await Exclusive(async () =>
    {
        Layout before = native.Capture();
        var currentWindows = WindowPositions.CaptureOfficeContent(before);
        if (before.Extended)
        {
            // The user may have just changed this arrangement in Windows Settings.
            // In extended mode F4 records the current topology without applying an older one.
            StoreOffice(before, await CaptureConfig());
            bool moved = await GatherOfficeWindows(before, currentWindows);
            return new(moved, moved ? "办公窗口已归位，保留 Windows 当前的扩展排列。" : "部分窗口尚未归位，请再次按 F4 重试。");
        }
        Profile office = ReadOffice();
        Layout desired = native.Rebind(office.Layout);
        native.Validate(desired);
        if (WindowsDisplays.Matches(before, desired))
        {
            bool moved = await GatherOfficeWindows(before, currentWindows);
            return new(moved, moved ? "当前已是办公布局。" : "办公布局已就绪，部分窗口尚未归位，请再次按 F4 重试。");
        }
        native.Validate(before);
        string? checkpoint = null;
        try { checkpoint = await CaptureConfig(); }
        catch (Exception error) { Data.Log($"办公恢复使用原生检查点：{error.Message}"); }
        try
        {
            await ApplyNativeFirst(desired, SameBindings(office.Layout, desired) ? office.Config : null);
            bool moved = await GatherOfficeWindows(desired, currentWindows);
            return new(moved, moved ? "已恢复办公扩展和应用窗口。" : "扩展模式已恢复，部分窗口尚未归位，请再次按 F4 重试。");
        }
        catch
        {
            await RollBack(before, checkpoint, office);
            throw;
        }
    });

    private async Task<bool> GatherOfficeWindows(Layout expected, WindowPositions.Position[] windows)
    {
        Layout actual = native.Capture();
        if (!WindowsDisplays.Matches(actual, expected))
            throw new InvalidOperationException("办公显示布局仍在变化，请稍后再按 F4。");

        // Run the bulk move only after Windows has selected the office primary.
        try { await RunTool(TimeSpan.FromSeconds(2), "/MoveWindow", "Primary", "All"); }
        catch (Exception error) { Data.Log($"MultiMonitorTool 窗口迁移失败，继续用 Windows 接口归位：{error.Message}"); }

        actual = native.Capture();
        if (!WindowsDisplays.Matches(actual, expected))
            throw new InvalidOperationException("窗口迁移期间办公显示布局发生变化，请再次按 F4。");
        return WindowPositions.MoveOfficeContent(actual, windows);
    }

    public async Task<SwitchResult> EmergencyRestoreAsync()
    {
        var exact = await RestoreOfficeAsync();
        if (exact.Success || Busy)
            return exact;
        return await Exclusive(async () =>
        {
            var office = ReadOffice();
            var available = native.AvailableOffice(office.Layout);
            native.Validate(available);
            await ApplyAndVerify(available, null);
            bool moved = WindowPositions.MoveOfficeContent(available, WindowPositions.CaptureOfficeContent(available));
            return new(moved, moved ? "紧急恢复成功，所有窗口已迁到办公主屏。" : "办公屏已恢复，部分窗口未归位，请按 F4 重试。");
        });
    }

    private static Profile ReadOffice() => Data.Read<Profile>("office.json") ??
        throw new InvalidOperationException("请先保存办公布局。");

    private Profile StoreOffice(Layout current, string config)
    {
        if (!WindowsDisplays.Matches(native.Capture(), current))
            throw new InvalidOperationException("Windows 显示排列正在变化，请稍后重试。");
        var profile = new Profile(current, config);
        Data.Save("office.json", profile);
        Data.Write("office.cfg", config);
        return profile;
    }

    private async Task<SwitchResult> Exclusive(Func<Task<SwitchResult>> operation)
    {
        if (!await gate.WaitAsync(0))
            return new(false, "正在处理上一项显示操作。");
        try
        {
            var result = await Task.Run(operation);
            Data.Log(result.Message);
            return result;
        }
        catch (Exception error)
        {
            Data.Log(error.ToString());
            return new(false, error.Message);
        }
        finally { gate.Release(); }
    }

    private static bool SameBindings(Layout before, Layout after) =>
        before.Displays.Select(display => display.Identity.Path).SequenceEqual(
            after.Displays.Select(display => display.Identity.Path), StringComparer.OrdinalIgnoreCase) &&
        before.Paths.Zip(after.Paths).All(pair => pair.First.Source.Adapter.Equals(pair.Second.Source.Adapter) &&
            pair.First.Source.Id == pair.Second.Source.Id && pair.First.Target.Adapter.Equals(pair.Second.Target.Adapter) &&
            pair.First.Target.Id == pair.Second.Target.Id);

    private async Task RollBack(Layout before, string? checkpoint, Profile office)
    {
        // A repeated F3 while already TV-only must not roll back to an unseen TV.
        foreach (var fallback in new[] { before.Extended ? new Profile(before, checkpoint ?? "") : office, office })
        {
            try
            {
                Layout recovered = native.Rebind(fallback.Layout);
                native.Validate(recovered);
                await ApplyAndVerify(recovered, SameBindings(fallback.Layout, recovered) ? fallback.Config : null);
                if (recovered.Extended)
                    WindowPositions.MoveOfficeContent(recovered, WindowPositions.CaptureOfficeContent(recovered));
                Data.Log("已验证恢复布局。");
                return;
            }
            catch (Exception error) { Data.Log($"回退失败：{error}"); }
        }
        try
        {
            var available = native.AvailableOffice(office.Layout);
            native.Validate(available);
            await ApplyAndVerify(available, null);
            WindowPositions.MoveOfficeContent(available, WindowPositions.CaptureOfficeContent(available));
            Data.Log("已验证部分办公显示器恢复。");
            return;
        }
        catch (Exception error) { Data.Log($"部分恢复失败：{error}"); }
        throw new InvalidOperationException("自动回退失败。请接通办公显示器后按 Ctrl+Alt+F9，或从桌面运行「恢复办公显示」。");
    }

    private async Task ApplyNativeFirst(Layout desired, string? config, bool temporary = false)
    {
        try { await ApplyAndVerify(desired, null, temporary); }
        catch (Exception error) when (!string.IsNullOrWhiteSpace(config))
        {
            Data.Log($"原生显示切换失败，尝试 MultiMonitorTool 快照：{error.Message}");
            await ApplyAndVerify(desired, config, temporary);
        }
    }

    private async Task ApplyAndVerify(Layout desired, string? config, bool temporary = false)
    {
        Layout rebound = native.Rebind(desired);
        native.Validate(rebound);
        if (!SameBindings(desired, rebound))
            config = null;
        desired = rebound;
        if (!string.IsNullOrWhiteSpace(config))
        {
            try
            {
                await WithTempConfig(async file =>
                {
                    await File.WriteAllTextAsync(file, config, new UTF8Encoding(false));
                    await RunTool("/LoadConfig", file);
                    return true;
                });
                await WaitFor(layout => WindowsDisplays.Matches(layout, desired));
                return;
            }
            catch (Exception error) { Data.Log($"快照应用未通过验证，尝试原生布局：{error.Message}"); }
        }
        desired = native.Rebind(desired);
        native.Validate(desired);
        native.Apply(desired, temporary);
        await WaitFor(layout => WindowsDisplays.Matches(layout, desired));
    }

    private async Task WaitFor(Func<Layout, bool> predicate)
    {
        int matches = 0;
        for (int attempt = 0; attempt < 14; attempt++)
        {
            await Task.Delay(350);
            try { matches = predicate(native.Capture()) ? matches + 1 : 0; }
            catch (Exception error) { Data.Log($"等待显示路径：{error.Message}"); matches = 0; }
            if (matches >= 2)
                return;
        }
        throw new InvalidOperationException("Windows 实际显示布局与预期不一致。");
    }

    private static string? TvConfig(string config, Layout active, Monitor target)
    {
        if (active.Displays.Any(display => display.Output is null) ||
            active.Displays.Select(display => display.Identity.Product).Distinct().Count() != active.Displays.Length)
            return null;
        var sections = Regex.Split(config, @"(?m)(?=^\[Monitor\d+\])")
            .Where(section => section.StartsWith("[Monitor", StringComparison.Ordinal)).ToArray();
        string?[] mapping = active.Displays.Select(display => sections.Where(section =>
            Regex.IsMatch(section, $@"(?im)^MonitorID=MONITOR\\{Regex.Escape(display.Identity.Product)}\\"))
            .Take(2).ToArray() is [var section] ? section : null).ToArray();
        if (mapping.Any(section => section is null) || mapping.Distinct().Count() != mapping.Length)
            return null;
        int tvIndex = Array.IndexOf(active.Displays, target);
        if (tvIndex < 0 || mapping[tvIndex] is not string tvSection ||
            !Regex.IsMatch(tvSection, $@"(?m)^Width={target.Width}\r?$") ||
            !Regex.IsMatch(tvSection, $@"(?m)^Height={target.Height}\r?$"))
            return null;
        for (int index = 0; index < mapping.Length; index++)
        {
            string section = mapping[index]!;
            string revised = index == tvIndex
                ? Regex.Replace(section, @"(?m)^(Position[XY])=-?\d+", "$1=0")
                : Regex.Replace(section, @"(?m)^(Width|Height|BitsPerPixel|DisplayFrequency)=-?\d+", "$1=0");
            config = config.Replace(section, revised, StringComparison.Ordinal);
        }
        return config;
    }

    private static bool SameKnownOutputs(string config, Layout active) =>
        active.Displays.All(display => display.Output is not null &&
            Regex.IsMatch(config, $@"(?im)^Name={Regex.Escape(@"\\.\" + display.Output)}\r?$"));

    private async Task<string> CaptureConfig() => await WithTempConfig(async file =>
    {
        await RunTool("/SaveConfig", file);
        string config = await File.ReadAllTextAsync(file);
        if (!config.Contains("[Monitor0]", StringComparison.Ordinal))
            throw new InvalidOperationException("MultiMonitorTool 没有生成有效的配置。");
        return config;
    });

    private static async Task<T> WithTempConfig<T>(Func<string, Task<T>> operation)
    {
        Directory.CreateDirectory(Data.Root);
        string temp = Path.Combine(Data.Root, $"{Guid.NewGuid():N}.cfg");
        try { return await operation(temp); }
        finally { File.Delete(temp); }
    }

    private Task RunTool(params string[] arguments) => RunTool(TimeSpan.FromSeconds(15), arguments);

    private async Task RunTool(TimeSpan timeLimit, params string[] arguments)
    {
        if (!ToolAvailable)
            throw new InvalidOperationException("切屏组件缺失，请重新解压完整发布包。");
        var start = new ProcessStartInfo(ToolPath) { CreateNoWindow = true, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ToolPath)! };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 MultiMonitorTool。");
        Data.Log($"MultiMonitorTool {string.Join(" ", arguments)}");
        using var timeout = new CancellationTokenSource(timeLimit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            throw new TimeoutException("MultiMonitorTool 执行超时。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"MultiMonitorTool 退出码 {process.ExitCode}。");
    }
}
