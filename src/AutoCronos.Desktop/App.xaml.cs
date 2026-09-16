using System.Windows;
using System.Windows.Threading;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly AutoCronos.Desktop.Services.LocalDataService _data = new();
    private readonly AutoCronos.Desktop.Services.WhatsAppMonitorSettingsService _whatsAppSettings = new();
    private readonly AutoCronos.Desktop.Services.WhatsAppMonitoringService _whatsAppMonitor;
    private DispatcherTimer? _emailSyncTimer;
    private DispatcherTimer? _deadlineTimer;
    private bool _emailSyncInProgress;
    private bool _deadlineCheckInProgress;
    private bool _notificationVisible;
    private readonly Queue<AppNotification> _notificationQueue = new();

    public App()
    {
        _whatsAppMonitor = new AutoCronos.Desktop.Services.WhatsAppMonitoringService(
            new AutoCronos.Desktop.Services.Suite360IntegrationService(),
            _whatsAppSettings);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _data.NotificationRaised += Data_NotificationRaised;
        var startupNotifications = _data.Initialize();
        new AutoCronos.Desktop.Services.StartupService().Enable();
        ConfigureEmailSyncTimer();
        ConfigureDeadlineTimer();
        _whatsAppMonitor.Start();
        new AutoCronos.Desktop.Windows.FloatingLauncherWindow(OpenBoard, OpenEmailSettings, OpenWarnings, OpenWhatsAppMonitor).Show();

        foreach (var notification in startupNotifications)
            _notificationQueue.Enqueue(notification with { IsPersistent = true });
        ShowNextNotification();

        if (!_data.GetEmailStatus().IsConnected)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenEmailSettings));
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

    private void OpenWhatsAppMonitor()
    {
        var window = Current.Windows.OfType<AutoCronos.Desktop.Windows.WhatsAppMonitorWindow>().FirstOrDefault();
        if (window is null)
        {
            new AutoCronos.Desktop.Windows.WhatsAppMonitorWindow(_whatsAppMonitor, _whatsAppSettings).Show();
            return;
        }

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _whatsAppMonitor.Dispose();
        base.OnExit(e);
    }

    private void ConfigureEmailSyncTimer()
    {
        _emailSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _emailSyncTimer.Tick += async (_, _) =>
        {
            if (_emailSyncInProgress || !_data.GetEmailStatus().IsConnected)
                return;

            _emailSyncInProgress = true;
            try
            {
                await _data.SyncEmailAsync();
            }
            finally
            {
                _emailSyncInProgress = false;
            }
        };
        _emailSyncTimer.Start();
    }

    private void ConfigureDeadlineTimer()
    {
        _deadlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _deadlineTimer.Tick += async (_, _) =>
        {
            if (_deadlineCheckInProgress)
                return;

            _deadlineCheckInProgress = true;
            try
            {
                await _data.AdvanceDueCardsAsync();
            }
            catch (Exception exception)
            {
                Data_NotificationRaised(new AppNotification("Automacao de prazo", $"Nao foi possivel verificar os prazos: {exception.Message}"));
            }
            finally
            {
                _deadlineCheckInProgress = false;
            }
        };
        _deadlineTimer.Start();
    }

    private void Data_NotificationRaised(AppNotification notification)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _notificationQueue.Enqueue(notification);
            ShowNextNotification();
        });
    }

    private void ShowNextNotification()
    {
        if (_notificationVisible || _notificationQueue.Count == 0)
            return;

        _notificationVisible = true;
        var window = new AutoCronos.Desktop.Windows.NotificationWindow(_notificationQueue.Dequeue());
        window.Closed += (_, _) =>
        {
            _notificationVisible = false;
            ShowNextNotification();
        };
        window.Show();
    }
}
