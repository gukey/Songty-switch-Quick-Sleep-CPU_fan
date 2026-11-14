using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using DisplaySwitcher;
using Monitor = DisplaySwitcher.Monitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (Environment.GetEnvironmentVariable("DISPLAY_SWITCHER_DATA_DIR") is null)
            Environment.SetEnvironmentVariable("DISPLAY_SWITCHER_DATA_DIR",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DisplaySwitcherTests", Guid.NewGuid().ToString("N")));
        var layout = new WindowsDisplays().Capture();
        if (!layout.Extended)
            throw new InvalidOperationException("This test requires the current multi-screen office desktop.");
        var primary = layout.Displays.Single(display => display.Primary);
        var secondary = layout.Displays.First(display => !display.Primary);
        var native = new WindowsDisplays();
        var backend = new MultiMonitorToolBackend(native);
        var missingTv = new DisplayIdentity("not-connected", "SNY0000", null, null);
        var offline = backend.SwitchToTvAsync(missingTv, () => throw new InvalidOperationException("Offline TV prompted for confirmation."))
            .GetAwaiter().GetResult();
        Require(!offline.Success && offline.Message.Contains("请开启电视") && WindowsDisplays.Matches(native.Capture(), layout),
            "offline TV shows power and connection prompt without changing layout");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var first = MakeWindow("Window position test - primary", primary, 120);
        var second = MakeWindow("Window position test - secondary", secondary, 240);
        var third = MakeWindow("Window position test - maximized", primary, 240);
        var fourth = MakeWindow("Window position test - minimized", primary, 360);
        fourth.WindowState = WindowState.Minimized;
        third.WindowState = WindowState.Maximized;
        Pump();
        var service = typeof(WindowsDisplays).Assembly.GetType("DisplaySwitcher.WindowPositions")!;
        var capture = service.GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static)!;
        var restore = service.GetMethod("Restore", BindingFlags.NonPublic | BindingFlags.Static)!;
        var handles = new[] { first, second, third, fourth }.Select(window => new WindowInteropHelper(window).Handle).ToArray();
        try
        {
            Array Snapshot(params IntPtr[] selected)
            {
                var all = (Array)capture.Invoke(null, [layout])!;
                var entries = all.Cast<object>().Where(value => selected.Any(handle => handle.ToInt64() ==
                    (long)value.GetType().GetProperty("Handle")!.GetValue(value)!)).ToArray();
                var result = Array.CreateInstance(all.GetType().GetElementType()!, entries.Length);
                Array.Copy(entries, result, entries.Length);
                return result;
            }

            string? Output(IntPtr handle) => Snapshot(handle).Cast<object>().Select(entry =>
                ((Monitor)entry.GetType().GetProperty("Display")!.GetValue(entry)!).Output).SingleOrDefault();

            Array original = Snapshot(handles);
            Require(original.Length == 4, "snapshot includes minimized test window");
            var snapshotType = service.GetNestedType("Snapshot", BindingFlags.NonPublic)!;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var persisted = Activator.CreateInstance(snapshotType, [process.SessionId, original])!;
            typeof(Data).GetMethod("Save")!.MakeGenericMethod(snapshotType)
                .Invoke(null, ["fixture-window-positions.json", persisted]);
            var loaded = typeof(Data).GetMethod("Read")!.MakeGenericMethod(snapshotType)
                .Invoke(null, ["fixture-window-positions.json"]);
            original = (Array)snapshotType.GetProperty("Windows")!.GetValue(loaded)!;
            Require(original.Length == 4, "window snapshots round-trip across app restarts");
            Require(Output(handles[0]) == primary.Output && Output(handles[1]) == secondary.Output &&
                Output(handles[2]) == primary.Output, "capture tracks actual physical screen");
            Move(first, secondary, 120);
            fourth.WindowState = WindowState.Normal;
            Move(fourth, secondary, 360);
            fourth.WindowState = WindowState.Minimized;
            Move(second, primary, 240);
            third.WindowState = WindowState.Normal;
            Move(third, secondary, 240);
            third.WindowState = WindowState.Maximized;
            Pump();
            Require(Output(handles[2]) == secondary.Output, "maximized window moved to secondary before restore");
            var lateMove = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2750) };
            lateMove.Tick += (_, _) => { lateMove.Stop(); Move(first, secondary, 120); };
            lateMove.Start();
            var restoreTime = System.Diagnostics.Stopwatch.StartNew();
            var pendingRestore = Task.Run(() => (int)restore.Invoke(null, [layout, original])!);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var waiting = new DispatcherFrame();
            _ = pendingRestore.ContinueWith(_ => dispatcher.BeginInvoke(() => waiting.Continue = false));
            Dispatcher.PushFrame(waiting);
            int restored = pendingRestore.GetAwaiter().GetResult();
            lateMove.Stop();
            Require(restoreTime.Elapsed.TotalSeconds >= 4 && restoreTime.Elapsed.TotalSeconds < 10,
                $"late window movement extends verification until stable ({restoreTime.Elapsed.TotalSeconds:F2}s)");
            Pump();
            Require(restored == 4 && Output(handles[0]) == primary.Output && Output(handles[1]) == secondary.Output &&
                Output(handles[2]) == primary.Output && third.WindowState == WindowState.Maximized,
                $"normal and maximized windows return to original screens (accepted={restored}, primary={primary.Output}, first={Output(handles[0])}, secondary={Output(handles[1])}/{secondary.Output}, maximized={Output(handles[2])}, state={third.WindowState})");
            fourth.WindowState = WindowState.Normal;
            Pump();
            Require(Output(handles[3]) == primary.Output,
                "minimized window reopens on the original primary, not the TV");
            Move(first, secondary, 120);
            var missing = layout with { Displays = [secondary] };
            var originalFirst = Array.CreateInstance(original.GetType().GetElementType()!, 1);
            originalFirst.SetValue(original.Cast<object>().Single(entry => (long)entry.GetType()
                .GetProperty("Handle")!.GetValue(entry)! == handles[0].ToInt64()), 0);
            Require((int)restore.Invoke(null, [missing, originalFirst])! == 0 &&
                Output(handles[0]) == secondary.Output, "unavailable original screen never redirects the window");

            Move(fourth, secondary, 360);
            fourth.WindowState = WindowState.Minimized;
            Pump();
            var gathered = (Array)service.GetMethod("CaptureOfficeContent", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [layout])!;
            var selected = gathered.Cast<object>().Where(entry => handles.Any(handle => handle.ToInt64() ==
                (long)entry.GetType().GetProperty("Handle")!.GetValue(entry)!)).ToArray();
            var allToPrimary = Array.CreateInstance(gathered.GetType().GetElementType()!, selected.Length);
            Array.Copy(selected, allToPrimary, selected.Length);
            Require(selected.Length == 4 && selected.All(entry => (bool)entry.GetType().GetProperty("PreferPrimary")!.GetValue(entry)!),
                "F4 without a saved snapshot gathers every window onto primary");
            var gatherTime = System.Diagnostics.Stopwatch.StartNew();
            var gatherTask = Task.Run(() => (int)restore.Invoke(null, [layout, allToPrimary])!);
            var gatherFrame = new DispatcherFrame();
            _ = gatherTask.ContinueWith(_ => dispatcher.BeginInvoke(() => gatherFrame.Continue = false));
            Dispatcher.PushFrame(gatherFrame);
            Require(gatherTask.GetAwaiter().GetResult() == 4, "all four window placements verified on primary");
            Require(gatherTime.Elapsed.TotalSeconds < 8,
                $"stable windows finish verification early ({gatherTime.Elapsed.TotalSeconds:F2}s)");
            fourth.WindowState = WindowState.Normal;
            Pump();
            Require(handles.All(handle => Output(handle) == primary.Output),
                "normal, maximized, secondary and reopened minimized windows are all on primary");

            var editorType = typeof(MainWindow).Assembly.GetType("DisplaySwitcher.HotkeyEditor")!;
            int key = 0;
            uint modifiers = 0;
            Func<int, uint, string?> save = (pressed, held) => { key = pressed; modifiers = held; return null; };
            var editor = (Window)Activator.CreateInstance(editorType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [first, new AppSettings(), save], null)!;
            editor.Loaded += (_, _) =>
            {
                var record = editorType.GetMethod("Record", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var confirm = ((StackPanel)((StackPanel)editor.Content).Children[3]).Children
                    .OfType<Button>().Single(button => (string)button.Content == "确定");
                record.Invoke(editor, [0x73, 0u]);
                Require(!confirm.IsEnabled, "fixed F4 cannot be saved as the TV hotkey");
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!,
                    Environment.TickCount, Key.F7) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                editor.RaiseEvent(args);
                Require(confirm.IsEnabled, "pressing a function key enables confirmation");
                record.Invoke(editor, [0x77, 6u]);
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            Require(editor.ShowDialog() == true && key == 0x77 && modifiers == 6,
                "confirm submits the detected Ctrl+Shift+F8 combination");
        }
        finally
        {
            fourth.Close();
            third.Close();
            second.Close();
            first.Close();
            app.Shutdown();
        }
    }

    private static Window MakeWindow(string title, Monitor display, int offset)
    {
        var window = new Window { Title = title, Width = 320, Height = 180,
            ShowActivated = false, Content = new TextBlock { Text = title } };
        window.Show();
        Move(window, display, offset);
        return window;
    }

    private static void Move(Window window, Monitor display, int offset)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
            display.X + offset, display.Y + offset, 0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new InvalidOperationException("Unable to position test window.");
        Pump();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool success, string message)
    {
        Console.WriteLine($"{(success ? "PASS" : "FAIL")} {message}");
        if (!success)
            throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
