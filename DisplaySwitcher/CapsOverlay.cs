using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DisplaySwitcher;

internal sealed class CapsOverlay : Window
{
    private readonly TextBlock iconText = new() { FontSize = 17, Foreground = Brushes.White,
        FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock state = new() { FontSize = 28, FontWeight = FontWeights.ExtraBold,
        LineHeight = 28, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextAlignment = TextAlignment.Center };
    private readonly Border icon = new() { Width = 58, Height = 58, CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)) };
    private readonly DispatcherTimer hide = new() { Interval = TimeSpan.FromMilliseconds(1150) };

    internal CapsOverlay()
    {
        Title = "DisplaySwitcher.CapsOverlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 166; Height = 90;
        FontFamily = new FontFamily("Segoe UI");
        icon.Child = iconText;
        var copy = new StackPanel { Width = 56, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        copy.Children.Add(new TextBlock { Text = "CAP", FontSize = 12, FontWeight = FontWeights.ExtraBold,
            Foreground = new SolidColorBrush(Color.FromArgb(184, 248, 250, 252)), Margin = new Thickness(0, 0, 0, 2),
            TextAlignment = TextAlignment.Center });
        copy.Children.Add(state);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(copy);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(148, 24, 27, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 0, 16, 0), Child = row };
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongW(handle, -20, GetWindowLongW(handle, -20) | 0x80 | 0x20 | 0x08000000);
        };
        hide.Tick += (_, _) => Dismiss();
    }

    internal void Display()
    {
        bool enabled = System.Windows.Forms.Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock);
        iconText.Text = enabled ? "ABC" : "abc";
        icon.Background = new SolidColorBrush(enabled ? Color.FromRgb(31, 157, 99) : Color.FromArgb(31, 255, 255, 255));
        state.Text = enabled ? "ON" : "OFF";
        state.Foreground = new SolidColorBrush(enabled ? Color.FromRgb(154, 211, 185) : Color.FromRgb(216, 222, 233));
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 70;
        hide.Stop();
        Show();
        hide.Start();
    }

    internal void Dismiss() { hide.Stop(); Hide(); }
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr window, int index, int value);
}
