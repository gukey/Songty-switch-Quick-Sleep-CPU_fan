using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DisplaySwitcher;

internal sealed class SodaFavoriteOverlay : Window
{
    private readonly TextBlock heart = new() { Text = "♥", FontSize = 34, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock message = new() { FontSize = 22, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis,
        FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    internal SodaFavoriteOverlay()
    {
        Title = "DisplaySwitcher 汽水音乐收藏";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(heart);
        row.Children.Add(message);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(242, 37, 39, 44)),
            Padding = new Thickness(27, 12, 27, 12), CornerRadius = new CornerRadius(14), Child = row };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongW(handle, -20, GetWindowLongW(handle, -20) | 0x080000A0);
        };
        timer.Tick += (_, _) => { timer.Stop(); Hide(); };
        Closed += (_, _) => timer.Stop();
    }

    internal static System.Drawing.Rectangle ForegroundScreen() =>
        System.Windows.Forms.Screen.FromHandle(GetForegroundWindow()).Bounds;

    internal void Display(System.Drawing.Rectangle screen, bool collected, string title)
    {
        timer.Stop();
        Opacity = 0;
        heart.Foreground = message.Foreground = collected ? new SolidColorBrush(Color.FromRgb(56, 216, 120)) : Brushes.White;
        message.Text = (collected ? "已收藏：" : "已取消收藏：") + title;
        Show();
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, IntPtr.Zero, screen.Left + screen.Width / 2, screen.Top + screen.Height / 2, 0, 0, 0x15);
        UpdateLayout();
        if (GetWindowRect(handle, out var rect))
            SetWindowPos(handle, IntPtr.Zero, screen.Left + (screen.Width - (rect.Right - rect.Left)) / 2,
                screen.Top + (screen.Height - (rect.Bottom - rect.Top)) / 2, 0, 0, 0x15);
        Opacity = 1;
        timer.Start();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr window, int index, int value);
}
