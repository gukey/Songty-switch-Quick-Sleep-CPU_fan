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
    private readonly TextBlock label = new() { FontSize = 24, Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
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
        Width = 220; Height = 76;
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 35, 53, 68)),
            CornerRadius = new CornerRadius(16), Child = label };
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongW(handle, -20, GetWindowLongW(handle, -20) | 0x80 | 0x20 | 0x08000000);
        };
        hide.Tick += (_, _) => Dismiss();
    }

    internal void Display()
    {
        label.Text = System.Windows.Forms.Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock)
            ? "ABC  大写已开启" : "abc  小写已开启";
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
