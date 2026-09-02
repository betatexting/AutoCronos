using System.Windows;
using System.Windows.Threading;

namespace AutoCronos.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly AutoCronos.Desktop.Services.LocalDataService _data = new();
    private DispatcherTimer? _emailSyncTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _data.Initialize();
        new AutoCronos.Desktop.Services.StartupService().Enable();
        ConfigureEmailSyncTimer();
        new AutoCronos.Desktop.Windows.FloatingLauncherWindow(OpenBoard, OpenEmailSettings, OpenWarnings).Show();
    }

    private void OpenBoard()
    {
        var window = Current.Windows.OfType<MainWindow>().FirstOrDefault() ?? new MainWindow(_data);
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void OpenEmailSettings()
    {
        var window = Current.Windows.OfType<AutoCronos.Desktop.Windows.EmailSettingsWindow>().FirstOrDefault();
        if (window is null)
        {
            new AutoCronos.Desktop.Windows.EmailSettingsWindow(_data).Show();
            return;
        }

        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void OpenWarnings()
    {
        var window = Current.Windows.OfType<AutoCronos.Desktop.Windows.WarningWindow>().FirstOrDefault()
            ?? new AutoCronos.Desktop.Windows.WarningWindow(_data);
        if (!window.IsVisible) window.Show();
        window.Activate();
    }

    private void ConfigureEmailSyncTimer()
    {
        _emailSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _emailSyncTimer.Tick += async (_, _) =>
        {
            if (_data.GetEmailStatus().IsConnected)
                await _data.SyncEmailAsync();
        };
        _emailSyncTimer.Start();
    }
}
