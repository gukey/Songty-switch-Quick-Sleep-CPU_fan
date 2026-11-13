using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DisplaySwitcher;

internal sealed class XunleiService : IDisposable
{
    private Process? process;
    private DateTime retryAt;
    internal bool Ready { get; private set; }
    internal bool Listening { get; private set; }
    internal int Port { get; private set; } = 8787;
    internal event Action? Changed;
    internal event Action<string>? Notification;

    internal void Check()
    {
        if (process is { HasExited: false } || DateTime.UtcNow < retryAt) return;
        Dispose();
        retryAt = DateTime.UtcNow.AddSeconds(30);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(BundledComponents.Root, "XunleiWorker.exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(Path.Combine(Data.Root, "xunlei"));
            start.Environment["PYTHONUTF8"] = "1";
            process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                try
                {
                    using var state = JsonDocument.Parse(args.Data);
                    if (state.RootElement.TryGetProperty("notification", out var notification))
                    {
                        Notification?.Invoke(notification.GetString() ?? "任务已提交迅雷");
                        return;
                    }
                    Ready = state.RootElement.GetProperty("ready").GetBoolean();
                    Listening = state.RootElement.GetProperty("listening").GetBoolean();
                    Port = state.RootElement.GetProperty("port").GetInt32();
                    Changed?.Invoke();
                }
                catch (JsonException) { Data.Log("迅雷服务返回无效状态。"); }
            };
            process.ErrorDataReceived += (_, args) => {
                if (!string.IsNullOrWhiteSpace(args.Data)) Data.Log("迅雷服务：" + args.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception error)
        {
            process?.Dispose();
            process = null;
            Data.Log("迅雷服务启动失败：" + error.Message);
        }
        Changed?.Invoke();
    }

    internal void SetListening(bool enabled)
    {
        if (!Ready || process is not { HasExited: false }) return;
        try
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { action = "listen", enabled }));
            process.StandardInput.Flush();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            Ready = false;
            Data.Log("迅雷控制通道已断开：" + error.Message);
            Changed?.Invoke();
        }
    }

    internal void OpenWeb()
    {
        if (Ready) Process.Start(new ProcessStartInfo($"http://127.0.0.1:{Port}/") { UseShellExecute = true });
    }

    public void Dispose()
    {
        Ready = false;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("{\"action\":\"stop\"}");
                process.StandardInput.Flush();
                if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        finally { process.Dispose(); process = null; }
    }
}
