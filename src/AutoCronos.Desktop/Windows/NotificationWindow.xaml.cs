using System.Windows;
using System.Windows.Threading;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Windows;

public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    public NotificationWindow(AppNotification notification)
    {
        InitializeComponent();
        Title = notification.Title;
        ShowInTaskbar = notification.IsPersistent;
        TitleTextBlock.Text = notification.Title;
        MessageTextBlock.Text = notification.Message;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top = area.Bottom - Height - 18;
            if (!notification.IsPersistent)
                _closeTimer.Start();
        };
        _closeTimer.Tick += (_, _) => Close();
        Closed += (_, _) => _closeTimer.Stop();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
