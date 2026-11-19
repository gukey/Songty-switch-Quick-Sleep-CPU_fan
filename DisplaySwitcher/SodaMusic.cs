using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DisplaySwitcher;

internal sealed class SodaMusic
{
    private const string Root = @"D:\Tools\Soda Music\";
    private static readonly string Session = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData), "SodaMusic", "FavoriteBridge", "session.json");
    private Task? pending;
    private Task? favoritePending;
    private DateTime nextCheck;
    private int completedPid;
    private long completedStart;

    internal void Check()
    {
        if (DateTime.UtcNow < nextCheck || pending is { IsCompleted: false }) return;
        nextCheck = DateTime.UtcNow.AddSeconds(5);
        pending = Task.Run(CloseControlsAsync);
    }

    internal void Favorite()
    {
        if (favoritePending is { IsCompleted: false }) return;
        favoritePending = Task.Run(() => SendAsync("favorite"));
    }

    private Task CloseControlsAsync() => SendAsync("disable-taskbar-controls");

    private async Task SendAsync(string command)
    {
        bool favorite = command == "favorite";
        try
        {
            if (!File.Exists(Path.Combine(Root, "SodaMusicLauncher.exe")) || !File.Exists(Session)) return;
            using var session = JsonDocument.Parse(File.ReadAllText(Session));
            int pid = session.RootElement.GetProperty("pid").GetInt32();
            using var process = Process.GetProcessById(pid);
            using var current = Process.GetCurrentProcess();
            if (process.SessionId != current.SessionId || !process.ProcessName.Equals("SodaMusic", StringComparison.OrdinalIgnoreCase)) return;
            long started = process.StartTime.ToUniversalTime().Ticks;
            if (!favorite && pid == completedPid && started == completedStart) return;
            string? image = process.MainModule?.FileName;
            if (image is null || !image.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return;
            string name = $"SodaFavorite-{pid}";
            if (session.RootElement.GetProperty("pipe").GetString() != @"\\.\pipe\" + name) return;
            string? token = session.RootElement.GetProperty("token").GetString();
            if (token?.Length != 64) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(14));
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { token, command }).AsMemory(), timeout.Token);
            string? line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return;
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.GetProperty("ok").GetBoolean())
            {
                if (favorite && response.RootElement.TryGetProperty("error", out var error))
                    Data.Log($"汽水音乐收藏失败：{error.GetString()}");
                return;
            }
            if (response.RootElement.GetProperty("pid").GetInt32() != pid) return;
            if (favorite)
            {
                if (response.RootElement.GetProperty("isCollected").GetBoolean())
                    Data.Log("汽水音乐已确认当前曲目为收藏状态。");
                return;
            }
            completedStart = started;
            completedPid = pid;
            Data.Log("已确认汽水音乐任务栏快捷播控及自动开启均关闭。");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ArgumentException or
            InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or System.Collections.Generic.KeyNotFoundException)
        {
            if (favorite) Data.Log($"汽水音乐收藏通道不可用：{error.Message}");
        }
    }
}
