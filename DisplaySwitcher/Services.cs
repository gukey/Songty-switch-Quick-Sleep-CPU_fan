using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Media.Control;

namespace DisplaySwitcher;

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Entry = "DisplaySwitcher";
    private const string Executable = "DisplaySwitcher.exe";
    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    internal static string? Ensure()
    {
        try
        {
            if (Environment.ProcessPath is not string path || !File.Exists(path))
                return "无法取得当前运行的程序路径。";
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!string.Equals(run.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase))
                run.SetValue(Entry, Command, RegistryValueKind.String);
            if (!string.Equals(run.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase))
                return "写入后未能读回当前启动路径。";

            foreach (string alias in run.GetValueNames())
            {
                if (!alias.Equals(Entry, StringComparison.OrdinalIgnoreCase) &&
                    StartsThisApp(run.GetValue(alias) as string))
                {
                    run.DeleteValue(alias, false);
                    Data.Log($"已清理旧开机启动项：{alias}");
                }
            }

            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (Directory.Exists(startupFolder))
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell", true)!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                foreach (string file in Directory.EnumerateFiles(startupFolder, "*.lnk"))
                {
                    string? target;
                    try { target = (string)shell.CreateShortcut(file).TargetPath; }
                    catch (COMException error) { Data.Log($"无法读取启动快捷方式：{error.Message}"); continue; }
                    if (string.Equals(Path.GetFileName(target), Executable, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        Data.Log($"已清理旧启动快捷方式：{Path.GetFileName(file)}");
                    }
                }
            }
            return null;
        }
        catch (Exception error)
        {
            Data.Log($"开机启动注册失败：{error}");
            return error.Message;
        }
    }

    internal static string? Check()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!string.Equals(run?.GetValue(Entry) as string, Command, StringComparison.OrdinalIgnoreCase))
                return "启动项缺失或路径不一致，请重启程序重建。";
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(Entry) is byte[] { Length: > 0 } state && state[0] is 3 or 7)
                return "Windows 已禁用此启动项，请在「启动应用」中启用。";
            return null;
        }
        catch (Exception error) { return error.Message; }
    }

    private static bool StartsThisApp(string? command)
    {
        string text = command?.TrimStart() ?? "";
        int end;
        if (text.StartsWith('"'))
        {
            end = text.IndexOf('"', 1);
            return end > 1 && Path.GetFileName(text[1..end]).Equals(Executable, StringComparison.OrdinalIgnoreCase);
        }
        Match executable = Regex.Match(text, @"^.+?\.exe(?=\s|$)", RegexOptions.IgnoreCase);
        return executable.Success &&
            Path.GetFileName(executable.Value).Equals(Executable, StringComparison.OrdinalIgnoreCase);
    }

    internal static void CreateRecoveryShortcut()
    {
        string location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "恢复办公显示.lnk");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(location);
        link.TargetPath = Environment.ProcessPath!;
        link.Arguments = "--restore-office";
        link.WorkingDirectory = AppContext.BaseDirectory;
        link.Description = "恢复已保存的办公显示布局";
        link.Save();
    }
}

internal sealed class Keyboard : IDisposable
{
    private const int HookKeyboard = 13;
    private const int RightAlt = 0xA5;
    private const uint NoRepeat = 0x4000;
    private readonly IntPtr window;
    private readonly HookCallback callback;
    private readonly Action quickSleep;
    private IntPtr hook;
    private bool altDown, solo;
    private int key;
    private uint modifiers;
    public bool TvRegistered { get; private set; }
    public bool OfficeRegistered { get; }
    public bool RecoveryRegistered { get; }
    public bool SleepRegistered => hook != IntPtr.Zero;

    internal Keyboard(IntPtr window, AppSettings settings, Action quickSleep)
    {
        this.window = window;
        this.quickSleep = quickSleep;
        callback = OnKeyboard;
        OfficeRegistered = RegisterHotKey(window, 2, NoRepeat, 0x73);
        RecoveryRegistered = RegisterHotKey(window, 3, NoRepeat | 3, 0x78);
        SetTv(settings.Key, settings.Modifiers);
        hook = SetWindowsHookExW(HookKeyboard, callback, GetModuleHandleW(null), 0);
        if (!SleepRegistered)
            Data.Log($"右 Alt 键盘钩子安装失败：{Marshal.GetLastWin32Error()}");
    }

    internal bool SetTv(int nextKey, uint nextModifiers)
    {
        if (nextKey == 0x73 && nextModifiers == 0 || nextKey == 0x78 && nextModifiers == 3)
            return false;
        if (TvRegistered && key == nextKey && modifiers == nextModifiers)
            return true;
        if (TvRegistered)
            UnregisterHotKey(window, 1);
        bool hadPrevious = TvRegistered;
        TvRegistered = RegisterHotKey(window, 1, nextModifiers | NoRepeat, (uint)nextKey);
        if (!TvRegistered)
        {
            Data.Log($"电视热键注册失败：VK={nextKey} modifiers={nextModifiers} Win32={Marshal.GetLastWin32Error()}");
            if (hadPrevious)
                TvRegistered = RegisterHotKey(window, 1, modifiers | NoRepeat, (uint)key);
            return false;
        }
        key = nextKey;
        modifiers = nextModifiers;
        return true;
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int keyCode = Marshal.ReadInt32(data);
            int flags = Marshal.ReadInt32(data, 8);
            bool down = message.ToInt64() is 0x100 or 0x104;
            bool up = message.ToInt64() is 0x101 or 0x105;
            if ((flags & 0x10) == 0)
            {
                if (keyCode == RightAlt && down && !altDown)
                {
                    altDown = true;
                    solo = !Held(0x10) && !Held(0x11) && !Held(0xA4) && !Held(0x5B) && !Held(0x5C);
                }
                else if (keyCode == RightAlt && up && altDown)
                {
                    altDown = false;
                    if (solo)
                        quickSleep();
                }
                else if (keyCode != RightAlt && down)
                    solo = false;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        for (int id = 1; id <= 3; id++)
            UnregisterHotKey(window, id);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookCallback action, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr GetModuleHandleW(string? name);
}

internal static class SleepControl
{
    internal static async Task RequestAsync()
    {
        await Task.Delay(300);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(timeout.Token);
        foreach (var session in media.GetSessions())
        {
            var playback = session.GetPlaybackInfo();
            if (playback.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                continue;
            if (!playback.Controls.IsPauseEnabled || !await session.TryPauseAsync().AsTask(timeout.Token))
                throw new InvalidOperationException($"无法暂停媒体：{session.SourceAppUserModelId}。已取消关屏。");
        }
        await Task.Delay(500);
        IntPtr desktop = FindWindowW("Progman", null);
        if (desktop == IntPtr.Zero || !IsWindow(desktop))
            throw new InvalidOperationException("无法定位桌面，已取消关屏。");
        if (!PostMessageW(desktop, 0x112, (IntPtr)0xF170, (IntPtr)2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Data.Log("快速关屏请求已入队。");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr FindWindowW(string name, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr first, IntPtr second);
}

internal sealed record FanSample(double? Cpu, double? Gpu, string? Port, bool Connected, string? Error = null);

internal sealed class FanWorker : IDisposable
{
    private Process? process;
    private DateTime retryAfter;
    private bool legacyNotified;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal event Action<FanSample>? Sample;

    internal static bool Running(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var item in processes)
            item.Dispose();
        return processes.Length > 0;
    }

    internal void Check()
    {
        if (Running("CPU_fan"))
        {
            Stop();
            if (!legacyNotified)
            {
                legacyNotified = true;
                Sample?.Invoke(new(null, null, null, false, "请退出旧版 CPU_fan，释放温控串口。"));
            }
            return;
        }
        legacyNotified = false;
        if (process is { HasExited: false } || DateTime.UtcNow < retryAfter)
            return;
        Stop();
        retryAfter = DateTime.UtcNow.AddSeconds(15);
        string file = Path.Combine(AppContext.BaseDirectory, "FanWorker.exe");
        if (!File.Exists(file))
        {
            Sample?.Invoke(new(null, null, null, false, "未找到 FanWorker.exe。"));
            return;
        }
        try
        {
            var child = new Process
            {
                StartInfo = new ProcessStartInfo(file)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                }
            };
            child.StartInfo.Environment["PYTHONUTF8"] = "1";
            child.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not { Length: > 0 } line)
                    return;
                try
                {
                    if (line[0] == '{' && JsonSerializer.Deserialize<FanSample>(line, Json) is { } sample)
                        Sample?.Invoke(sample);
                    else if (!line.Equals("Serial RX: ACK", StringComparison.OrdinalIgnoreCase))
                        Data.Log($"ESP32 {line}");
                }
                catch (JsonException error) { Data.Log($"ESP32 数据无效：{error.Message}"); }
            };
            child.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    Data.Log($"ESP32 {args.Data}");
            };
            process = child;
            child.Start();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }
        catch (Exception error)
        {
            Stop();
            Sample?.Invoke(new(null, null, null, false, "温控进程启动失败。"));
            Data.Log(error.ToString());
        }
    }

    private void Stop()
    {
        var child = process;
        process = null;
        if (child is null)
            return;
        _ = Task.Run(async () =>
        {
            using (child)
            {
                try
                {
                    if (child.HasExited)
                        return;
                    child.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await child.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception error) { Data.Log(error.Message); }
            }
        });
    }

    public void Dispose() => Stop();
}

internal sealed class Tray : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly Icon fallback;
    private Icon? wave;

    internal Tray(Action show, Action tv, Action office, Action sleep, Action quit)
    {
        using var stream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/QuickSleep.ico")).Stream;
        using var resource = new Icon(stream);
        fallback = (Icon)resource.Clone();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => show());
        menu.Items.Add("电视模式", null, (_, _) => tv());
        menu.Items.Add("恢复办公", null, (_, _) => office());
        menu.Items.Add("快速关屏", null, (_, _) => sleep());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => quit());
        icon = new NotifyIcon { Icon = fallback, Visible = true, Text = "显示器切换", ContextMenuStrip = menu };
        icon.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) show(); };
    }

    internal void Update(FanSample sample, IEnumerable<FanSample> history)
    {
        icon.Text = $"CPU: {Temp(sample.Cpu)} | GPU: {Temp(sample.Gpu)}";
        Icon? next = sample.Cpu is null && sample.Gpu is null ? null : DrawWave(history.ToArray());
        icon.Icon = next ?? fallback;
        wave?.Dispose();
        wave = next;
    }

    internal static string Temp(double? value) => value is null ? "--" : $"{value:F1}°C";

    private static Icon DrawWave(FanSample[] history)
    {
        double[] temperatures = history.SelectMany(sample => new[] { sample.Cpu, sample.Gpu })
            .OfType<double>().DefaultIfEmpty(0).ToArray();
        double min = temperatures.Min(), max = temperatures.Max();
        double span = Math.Max(2, max - min);
        double floor = (min + max - span) / 2;
        float Y(double temperature) => (float)(28 - (temperature - floor) / span * 24);

        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.FromArgb(16, 23, 37));
            using var cpu = new Pen(Color.Orange, 2);
            using var gpu = new Pen(Color.LawnGreen, 2);
            for (int index = 1; index < history.Length; index++)
            {
                float left = (float)(index - 1) * 28 / Math.Max(history.Length - 1, 1) + 2;
                float right = (float)index * 28 / Math.Max(history.Length - 1, 1) + 2;
                if (history[index - 1].Cpu is double a && history[index].Cpu is double b)
                    graphics.DrawLine(cpu, left, Y(a), right, Y(b));
                if (history[index - 1].Gpu is double c && history[index].Gpu is double d)
                    graphics.DrawLine(gpu, left, Y(c), right, Y(d));
            }
            if (history.Length <= 1)
            {
                if (history.LastOrDefault()?.Cpu is double currentCpu)
                    graphics.DrawLine(cpu, 2, Y(currentCpu), 30, Y(currentCpu));
                if (history.LastOrDefault()?.Gpu is double currentGpu)
                    graphics.DrawLine(gpu, 2, Y(currentGpu), 30, Y(currentGpu));
            }
        }
        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        wave?.Dispose();
        fallback.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
