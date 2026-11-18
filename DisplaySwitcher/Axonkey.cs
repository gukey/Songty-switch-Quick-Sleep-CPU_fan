using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DisplaySwitcher;

internal static class Axonkey
{
    private static readonly string Executable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Axonkey", "axonkey.exe");

    internal static void EnsureBackground()
    {
        if (!File.Exists(Executable)) return;
        try
        {
            using var current = Process.GetCurrentProcess();
            var running = Process.GetProcessesByName("axonkey");
            try
            {
                if (running.Any(process => process.SessionId == current.SessionId)) return;
            }
            finally { foreach (var process in running) process.Dispose(); }
            Launch("--autostart");
        }
        catch (Exception error) { Data.Log($"AxonKey 后台启动失败：{error.Message}"); }
    }

    internal static void OpenSettings()
    {
        try
        {
            if (File.Exists(Executable)) Launch("");
            else Process.Start(new ProcessStartInfo("https://github.com/leowzz/axonkey/releases/latest") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error)
        {
            Data.Log($"AxonKey 配置打开失败：{error.Message}");
            System.Windows.MessageBox.Show(error.Message, "AxonKey");
        }
    }

    private static void Launch(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(Executable, arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
            UseShellExecute = true,
            WindowStyle = arguments.Length == 0 ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        });
    }
}
