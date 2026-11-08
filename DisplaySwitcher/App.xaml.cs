using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DisplaySwitcher;

public partial class App : Application
{
    private readonly CancellationTokenSource stop = new();
    private Mutex? instance;
    private MainWindow? main;
    private static readonly string InstanceName = $"DisplaySwitcher.{Process.GetCurrentProcess().SessionId}";

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        bool restore = Array.Exists(args.Args, value => value == "--restore-office");
        try
        {
            instance = new Mutex(true, @"Local\" + InstanceName, out bool primary);
            if (!primary)
            {
                using var client = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(2500);
                using var writer = new StreamWriter(client);
                await writer.WriteLineAsync(restore ? "office" : "show");
                Shutdown();
                return;
            }

            AppSettings settings;
            try { settings = Data.Read<AppSettings>("settings.json") ?? new(); }
            catch (Exception error) { Data.Log(error.ToString()); settings = new(); }
            if (restore)
            {
                var backend = new MultiMonitorToolBackend(new WindowsDisplays(), () => settings);
                var result = await backend.EmergencyRestoreAsync();
                if (!result.Success)
                    MessageBox.Show(result.Message, "恢复办公显示", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(result.Success ? 0 : 1);
                return;
            }

            main = new MainWindow(settings);
            MainWindow = main;
            _ = Listen();
            if (Array.Exists(args.Args, value => value == "--show"))
                main.Reveal();
        }
        catch (Exception error)
        {
            Data.Log(error.ToString());
            MessageBox.Show(error.Message, "显示器切换", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                string? command = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (command == "show")
                    await Dispatcher.InvokeAsync(() => main?.Reveal());
                else if (command == "office")
                    await Dispatcher.InvokeAsync(() => main?.Emergency());
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Data.Log($"单实例通讯：{error.Message}");
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        stop.Cancel();
        main?.Stop();
        instance?.Dispose();
        base.OnExit(e);
    }
}
