using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DisplaySwitcher;

internal sealed class HotkeyEditor : Window
{
    private readonly TextBlock captured;
    private readonly TextBlock hint;
    private readonly Button confirm;
    private int key;
    private uint modifiers;

    internal HotkeyEditor(Window owner, AppSettings settings, Func<int, uint, string?> save)
    {
        Title = "修改电视快捷键";
        Owner = owner;
        Width = 390;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 14;
        Background = new SolidColorBrush(Color.FromRgb(241, 247, 255));

        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "请按下新的快捷键",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        captured = new TextBlock
        {
            Text = $"当前为 {Label(settings.Key, settings.Modifiers)}",
            Margin = new Thickness(0, 0, 0, 6)
        };
        hint = new TextBlock
        {
            Text = "可用功能键，或 Ctrl、Alt、Shift 加其他键。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };
        content.Children.Add(captured);
        content.Children.Add(hint);
        confirm = new Button { Content = "确定", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);
        Content = content;

        PreviewKeyDown += OnKeyDown;
        confirm.Click += (_, _) =>
        {
            string? error = save(key, modifiers);
            if (error is null)
                DialogResult = true;
            else
                hint.Text = error;
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        Key pressed = args.Key == Key.System ? args.SystemKey : args.Key;
        if (pressed is Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or
            Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin or Key.None)
            return;
        var held = System.Windows.Input.Keyboard.Modifiers;
        if (held == ModifierKeys.None && pressed is Key.Tab or Key.Escape or Key.Enter)
            return;
        args.Handled = true;
        if ((held & ModifierKeys.Windows) != 0)
        {
            Reject("请选择不含 Windows 键的组合。");
            return;
        }
        Record(KeyInterop.VirtualKeyFromKey(pressed), (uint)held);
    }

    internal void Record(int nextKey, uint nextModifiers)
    {
        if ((nextKey == 0x73 && nextModifiers == 0) || (nextKey == 0x78 && nextModifiers == 3) || nextKey == 0x7B)
        {
            Reject("F4、Ctrl+Alt+F9 和 F12 为保留快捷键。");
            return;
        }
        if (nextKey == 0 || (nextModifiers == 0 && (nextKey < 0x70 || nextKey > 0x87)))
        {
            Reject("请按功能键，或按住 Ctrl、Alt、Shift 再按其他键。");
            return;
        }
        key = nextKey;
        modifiers = nextModifiers;
        captured.Text = Label(key, modifiers);
        hint.Text = "点击「确定」保存并立即生效。";
        confirm.IsEnabled = true;
    }

    private void Reject(string message)
    {
        captured.Text = "等待新的快捷键…";
        hint.Text = message;
        confirm.IsEnabled = false;
    }

    internal static string Label(int key, uint modifiers) =>
        ((modifiers & 2) != 0 ? "Ctrl+" : "") + ((modifiers & 1) != 0 ? "Alt+" : "") +
        ((modifiers & 4) != 0 ? "Shift+" : "") + KeyInterop.KeyFromVirtualKey(key);
}
