using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DisplaySwitcher;

public partial class MainWindow : Window
{
    private readonly MultiMonitorToolBackend backend;
    private readonly Keyboard keyboard;
    private readonly Tray tray;
    private readonly FanWorker fan = new();
    private readonly DispatcherTimer timer;
    private readonly Queue<FanSample> history = new();
    private AppSettings settings;
    private bool working, refreshing, exiting;
    private bool legacySleep;
    private readonly string? startupRegistrationError;
    private DateTime sampleAt, sleepAt;
    private Window? test;
    private HotkeyEditor? hotkeyEditor;
    private bool emergencyPending;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        backend = new(new WindowsDisplays());
        startupRegistrationError = Startup.Ensure();
        HotkeyBox.Text = HotkeyEditor.Label(settings.Key, settings.Modifiers);

        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(handle).AddHook(WindowMessage);
        keyboard = new(handle, settings, () => Dispatcher.BeginInvoke(() => QuickSleep(fromHotkey: true)));
        tray = new(Reveal, () => _ = Switch(true), () => _ = Switch(false), () => QuickSleep(false), Quit);
        fan.Sample += sample => Dispatcher.BeginInvoke(() => RenderFan(sample));
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        StatusLabel.Text = backend.HasOffice ? "就绪。" : "首次切换会记录当前扩展布局。";
        Tick();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x312)
        {
            handled = true;
            if (hotkeyEditor is not null)
            {
                switch (wParam.ToInt64())
                {
                    case 1: hotkeyEditor.Record(settings.Key, settings.Modifiers); break;
                    case 2: hotkeyEditor.Record(0x73, 0); break;
                    case 3: hotkeyEditor.Record(0x78, 3); break;
                }
                return IntPtr.Zero;
            }
            switch (wParam.ToInt64())
            {
                case 1: _ = Switch(true); break;
                case 2:
                    if (test is not null)
                        test.Close();
                    else
                        _ = Switch(false);
                    break;
                case 3: Emergency(); break;
            }
        }
        else if (message is 0x7e or 0x218)
        {
            _ = RefreshDisplays();
        }
        return IntPtr.Zero;
    }

    private void Tick()
    {
        if (exiting)
            return;
        legacySleep = FanWorker.Running("Quick-Sleep");
        fan.Check();
        if (sampleAt != DateTime.MinValue && DateTime.UtcNow - sampleAt > TimeSpan.FromSeconds(12))
        {
            sampleAt = DateTime.MinValue;
            history.Clear();
            RenderFan(new(null, null, null, false, "等待新的温度读数…"), false);
        }
        RenderHotkeys();
        RenderStartup(startupRegistrationError ?? Startup.Check());
        _ = RefreshDisplays();
    }

    private void RenderStartup(string? error)
    {
        bool success = error is null;
        StartupLabel.Text = success ? "开机启动 · 状态正常" : "开机启动 · 异常";
        StartupBadge.Background = new SolidColorBrush(success ? Color.FromRgb(214, 244, 224) : Color.FromRgb(255, 218, 219));
        StartupLabel.Foreground = new SolidColorBrush(success ? Color.FromRgb(20, 105, 63) : Color.FromRgb(153, 37, 41));
        StartupBadge.ToolTip = success ? $"{Environment.ProcessPath} --tray" : error;
    }

    private void RenderHotkeys()
    {
        TvKeyBadge.Text = keyboard.TvRegistered ? HotkeyEditor.Label(settings.Key, settings.Modifiers) : "—";
        TvKeyBadge.FontSize = TvKeyBadge.Text.Length > 7 ? 14 : 23;
        string office = keyboard.OfficeRegistered ? "F4 固定" : "F4 被占用";
        string sleep = legacySleep ? "右 Alt 由旧版处理" : keyboard.SleepRegistered ? "右 Alt 快速关屏" : "右 Alt 未启用";
        HotkeyLabel.Text = $"{office} · {sleep}" + (keyboard.TvRegistered ? "" : " · 电视热键被占用");
        HotkeyLabel.ToolTip = keyboard.RecoveryRegistered ? "紧急恢复 Ctrl+Alt+F9" : "紧急恢复热键被占用，可用桌面快捷方式";
    }

    private async Task RefreshDisplays()
    {
        if (refreshing || exiting || working)
            return;
        refreshing = true;
        try
        {
            Monitor[] displays = await backend.GetDisplaysAsync();
            Layout layout = await backend.CaptureAsync();
            if (working || exiting)
                return;
            Monitor? availableTv = FindTv(displays, settings.Tv, out string? targetError);
            Monitor? existing = TargetBox.SelectedItem as Monitor;
            Monitor? selected = existing is not null ? WindowsDisplays.Resolve(existing.Identity, displays) :
                availableTv;
            if (!TargetBox.IsDropDownOpen)
            {
                if (TargetBox.ItemsSource is not Monitor[] old || !old.SequenceEqual(displays))
                    TargetBox.ItemsSource = displays;
                var items = (Monitor[])TargetBox.ItemsSource;
                TargetBox.SelectedItem = selected is null ? null : WindowsDisplays.Resolve(selected.Identity, items);
                TargetBox.ToolTip = selected?.Details;
            }
            string? targetStatus = MultiMonitorToolBackend.ToolAvailable ? targetError : "切屏组件缺失，请重新解压发布包。";
            TargetStatusLabel.Text = targetStatus;
            TargetStatusLabel.Visibility = targetStatus is null ? Visibility.Collapsed : Visibility.Visible;
            var target = availableTv?.Identity;
            ModeLabel.Text = layout.Displays.Length == 1 && target is not null &&
                WindowsDisplays.Resolve(target, layout.Displays) is not null ? "电视模式" :
                layout.Extended ? "扩展模式" : layout.Displays.Length > 1 ? "镜像／混合模式" : "单屏模式";
            bool officeReady = backend.HasOffice || layout.Extended;
            SaveOfficeButton.IsEnabled = layout.Extended;
            OfficeButton.IsEnabled = officeReady;
            TvButton.IsEnabled = true;
        }
        catch (Exception error)
        {
            ModeLabel.Text = "检测失败";
            Data.Log(error.ToString());
        }
        finally { refreshing = false; }
    }

    private static Monitor? FindTv(Monitor[] displays, DisplayIdentity? saved, out string? error)
    {
        error = MultiMonitorToolBackend.TvUnavailable;
        if (saved is not null)
        {
            var bound = WindowsDisplays.Resolve(saved, displays);
            if (bound is not null)
                error = null;
            return bound;
        }

        var matches = displays.Where(monitor =>
            monitor.Identity.Product.StartsWith("SNY", StringComparison.OrdinalIgnoreCase) &&
            monitor.Name.Equals("SONY TV", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches is [var sony])
        {
            error = null;
            return sony;
        }
        if (matches.Length > 1)
            error = "检测到多台索尼电视，请选择目标并保存设置。";
        return null;
    }

    private void RenderFan(FanSample sample, bool fresh = true)
    {
        if (exiting)
            return;
        if (fresh)
            sampleAt = sample.Error is null ? DateTime.UtcNow : DateTime.MinValue;
        if (sample.Cpu is null && sample.Gpu is null)
            history.Clear();
        else
        {
            history.Enqueue(sample);
            while (history.Count > 30)
                history.Dequeue();
        }
        TemperatureLabel.Text = $"CPU  {Tray.Temp(sample.Cpu)}     GPU  {Tray.Temp(sample.Gpu)}";
        FanLabel.Text = sample.Error ?? (sample.Connected ?
            $"ESP32 已连接 · {sample.Port}" : "ESP32 未确认连接");
        FanLabel.ToolTip = FanLabel.Text;
        var scale = TemperatureScale.From(history);
        CpuWave.Points = Chart(history, sample => sample.Cpu, scale);
        GpuWave.Points = Chart(history, sample => sample.Gpu, scale);
        tray.Update(sample, history);
    }

    private static PointCollection Chart(IEnumerable<FanSample> samples, Func<FanSample, double?> select, TemperatureScale scale)
    {
        var values = samples.Select(select).ToArray();
        var points = new PointCollection();
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] is not double value)
                continue;
            double y = 32 - scale.Fraction(value) * 30;
            points.Add(new Point(index * 168.0 / Math.Max(values.Length - 1, 1), y));
            if (values.Length == 1)
                points.Add(new Point(168, y));
        }
        return points;
    }

    public void Reveal()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        _ = RefreshDisplays();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal void Emergency()
    {
        if (test is not null)
        {
            test.Close();
            return;
        }
        if (working)
        {
            emergencyPending = true;
            return;
        }
        _ = Switch(false, emergency: true);
    }

    private async Task Switch(bool tv, bool emergency = false)
    {
        if (working || hotkeyEditor is not null && !emergency)
            return;
        StartWork(tv ? "正在切换到电视…" : "正在恢复办公布局…");
        try
        {
            if (tv)
            {
                var displays = await backend.GetDisplaysAsync();
                var detected = FindTv(displays, settings.Tv, out string? targetError);
                if (detected is null)
                {
                    Report(new(false, targetError!));
                    return;
                }
                var target = detected.Identity;
                if (!backend.HasOffice)
                {
                    var office = await SaveOfficeAndShortcut();
                    if (!office.Success || emergencyPending)
                    {
                        Report(office);
                        return;
                    }
                }
                if (settings.Tv is null)
                {
                    var changed = settings with { Tv = target };
                    Data.Save("settings.json", changed);
                    settings = changed;
                }
                Report(await backend.SwitchToTvAsync(target, () => Dispatcher.InvokeAsync(ConfirmTv).Task));
            }
            else if (!backend.HasOffice && !emergency)
            {
                Reveal();
                Report(await SaveOfficeAndShortcut());
            }
            else
            {
                Report(emergency ? await backend.EmergencyRestoreAsync() : await backend.RestoreOfficeAsync());
            }
        }
        catch (Exception error)
        {
            Data.Log(error.ToString());
            Report(new(false, error.Message));
        }
        finally { EndWork(); }
    }

    private bool ConfirmTv()
    {
        if (emergencyPending)
        {
            emergencyPending = false;
            return false;
        }
        int seconds = 18;
        var label = new TextBlock { Text = $"若没有确认，将在 {seconds} 秒后恢复。",
            Margin = new Thickness(0, 0, 0, 16) };
        var accept = new Button { Content = "画面正常，保留", Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "立即恢复", IsCancel = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(accept);
        actions.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = "电视画面是否正常？", FontSize = 22, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(label);
        content.Children.Add(actions);
        var dialog = new Window { Title = "首次电视模式测试", Content = content,
            Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(241, 247, 255)), FontSize = 14 };
        var countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        countdown.Tick += (_, _) =>
        {
            if (--seconds == 0)
                dialog.Close();
            else
                label.Text = $"若没有确认，将在 {seconds} 秒后恢复。";
        };
        accept.Click += (_, _) => dialog.DialogResult = true;
        test = dialog;
        countdown.Start();
        try { return dialog.ShowDialog() == true; }
        finally { test = null; countdown.Stop(); }
    }

    private void StartWork(string status)
    {
        working = true;
        StatusLabel.Text = status;
        TvButton.IsEnabled = OfficeButton.IsEnabled = SaveOfficeButton.IsEnabled = false;
    }

    private void EndWork()
    {
        working = false;
        _ = RefreshDisplays();
        if (emergencyPending)
        {
            emergencyPending = false;
            _ = Switch(false, emergency: true);
        }
    }

    private void Report(SwitchResult result)
    {
        StatusLabel.Text = result.Message;
        if (!result.Success && !result.Message.StartsWith("测试结束", StringComparison.Ordinal))
            Reveal();
    }

    private void TvClick(object sender, RoutedEventArgs args) => _ = Switch(true);
    private void OfficeClick(object sender, RoutedEventArgs args) => _ = Switch(false);

    private async void SaveOfficeClick(object sender, RoutedEventArgs args)
    {
        if (working)
            return;
        string text = backend.HasOffice ? "用当前扩展布局更新办公配置？" : "将当前扩展布局设为默认办公布局？";
        if (MessageBox.Show(this, text, "保存办公布局", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        StartWork("正在保存办公布局…");
        try { Report(await SaveOfficeAndShortcut()); }
        finally { EndWork(); }
    }

    private async Task<SwitchResult> SaveOfficeAndShortcut()
    {
        var result = await backend.SaveOfficeLayoutAsync();
        if (!result.Success)
            return result;
        try { Startup.CreateRecoveryShortcut(); }
        catch (Exception error)
        {
            Data.Log(error.ToString());
            return new(true, "办公布局已保存，桌面紧急恢复快捷方式创建失败。");
        }
        return result;
    }

    private void SaveSettingsClick(object sender, RoutedEventArgs args)
    {
        if (working)
            return;
        try
        {
            var changed = settings with { Tv = (TargetBox.SelectedItem as Monitor)?.Identity ?? settings.Tv };
            Data.Save("settings.json", changed);
            settings = changed;
            StatusLabel.Text = changed.Tv is null ? "设置已保存。电视暂未绑定。" : "设置已保存。";
            _ = RefreshDisplays();
        }
        catch (Exception error)
        {
            Data.Log(error.ToString());
            StatusLabel.Text = $"无法保存设置：{error.Message}";
        }
    }

    private void EditHotkeyClick(object sender, RoutedEventArgs args)
    {
        if (working)
            return;
        hotkeyEditor = new HotkeyEditor(this, settings, SaveTvHotkey);
        try { hotkeyEditor.ShowDialog(); }
        finally { hotkeyEditor = null; }
    }

    private void ResetHotkeyClick(object sender, RoutedEventArgs args)
    {
        if (!working)
            StatusLabel.Text = SaveTvHotkey(0x72, 0) ?? "电视快捷键已恢复为 F3。";
    }

    private string? SaveTvHotkey(int key, uint modifiers)
    {
        if (!keyboard.SetTv(key, modifiers))
        {
            RenderHotkeys();
            return "此快捷键已被占用或保留，请换一个。";
        }

        try
        {
            var changed = settings with { Key = key, Modifiers = modifiers };
            Data.Save("settings.json", changed);
            settings = changed;
            HotkeyBox.Text = HotkeyEditor.Label(key, modifiers);
            StatusLabel.Text = $"电视快捷键已保存为 {HotkeyBox.Text}。";
            return null;
        }
        catch (Exception error)
        {
            keyboard.SetTv(settings.Key, settings.Modifiers);
            Data.Log(error.ToString());
            return $"无法保存快捷键：{error.Message}";
        }
        finally { RenderHotkeys(); }
    }

    private async void QuickSleep(bool fromHotkey)
    {
        if (working || hotkeyEditor is not null || DateTime.UtcNow - sleepAt < TimeSpan.FromSeconds(3))
            return;
        if (fromHotkey && legacySleep)
            return;
        sleepAt = DateTime.UtcNow;
        StartWork("正在暂停媒体并请求关屏…");
        try
        {
            await SleepControl.RequestAsync();
            StatusLabel.Text = "关屏请求已发送。";
        }
        catch (Exception error)
        {
            Data.Log(error.ToString());
            Report(new(false, error.Message));
        }
        finally { EndWork(); }
    }

    private void Quit()
    {
        if (!working)
            Application.Current.Shutdown();
    }

    internal void Stop()
    {
        if (exiting)
            return;
        exiting = true;
        timer.Stop();
        fan.Dispose();
        keyboard.Dispose();
        tray.Dispose();
    }
}
