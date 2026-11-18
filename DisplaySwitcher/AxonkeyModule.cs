using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DisplaySwitcher;

internal sealed class AxonkeyModule : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Command();

    private readonly IntPtr library;
    private readonly Command show;
    private readonly Command stop;
    private readonly Thread thread;

    private AxonkeyModule(IntPtr library)
    {
        this.library = library;
        var run = Export("display_switcher_axonkey_run");
        show = Export("display_switcher_axonkey_show");
        stop = Export("display_switcher_axonkey_stop");
        thread = new Thread(() =>
        {
            int code = run();
            if (code != 0) Data.Log($"AxonKey 内置模块退出码：{code}");
        }) { IsBackground = true, Name = "AxonKey runtime" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static AxonkeyModule? Start()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            string path = Path.Combine(BundledComponents.Root, "AxonKey", "axonkey_lib.dll");
            if (!File.Exists(path)) return null;
            using var current = Process.GetCurrentProcess();
            var standalone = Process.GetProcessesByName("axonkey");
            try
            {
                foreach (var process in standalone)
                {
                    if (process.SessionId != current.SessionId) continue;
                    Data.Log("独立 AxonKey 正在运行，内置模块未启动，以免重复读取设备和配置。");
                    return null;
                }
            }
            finally { foreach (var process in standalone) process.Dispose(); }
            Environment.SetEnvironmentVariable("DISPLAY_SWITCHER_AXONKEY_RESOURCES", Path.GetDirectoryName(path));
            handle = NativeLibrary.Load(path);
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.axonkey.app");
            SeedConfiguration(Path.GetDirectoryName(path)!, config);
            if (Environment.ProcessPath is string host)
            {
                string cli = Path.Combine(config, "cli");
                Directory.CreateDirectory(cli);
                File.WriteAllText(Path.Combine(cli, "display-switcher-host.txt"), host);
            }
            return new AxonkeyModule(handle);
        }
        catch (Exception error)
        {
            if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
            Data.Log($"无法加载 AxonKey 内置模块：{error.Message}");
            return null;
        }
    }

    internal static void SeedConfiguration(string module, string config)
    {
        string seed = Path.Combine(module, "Defaults", "device-instances.json");
        string destination = Path.Combine(config, "device-instances.json");
        if (!File.Exists(seed) || File.Exists(destination)) return;
        Directory.CreateDirectory(config);
        string temporary = Path.Combine(config, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(seed, temporary);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { return; }
            Data.Log("已写入内置 AxonKey 默认按键映射。");
        }
        finally { File.Delete(temporary); }
    }

    internal async Task ShowAsync()
    {
        for (int attempt = 0; attempt < 40 && thread.IsAlive; attempt++)
        {
            if (show() == 0) return;
            await Task.Delay(250);
        }
        System.Windows.MessageBox.Show("AxonKey 模块未就绪，请查看日志。", "AxonKey 配置");
    }

    public void Dispose()
    {
        stop();
        if (!thread.Join(TimeSpan.FromSeconds(3)))
            Data.Log("等待 AxonKey 模块停止超时。");
        // Native callbacks and the logger may still be referenced until process exit.
    }

    private Command Export(string name) => Marshal.GetDelegateForFunctionPointer<Command>(NativeLibrary.GetExport(library, name));
}
