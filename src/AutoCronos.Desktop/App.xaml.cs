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
    private DispatcherTimer? _emailSyncTimer;
    private DispatcherTimer? _deadlineTimer;
    private bool _emailSyncInProgress;
    private bool _deadlineCheckInProgress;
    private bool _notificationVisible;
    private readonly Queue<AppNotification> _notificationQueue = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _data.NotificationRaised += Data_NotificationRaised;
        _data.Initialize();
        new AutoCronos.Desktop.Services.StartupService().Enable();
        ConfigureEmailSyncTimer();
        ConfigureDeadlineTimer();
        new AutoCronos.Desktop.Windows.FloatingLauncherWindow(OpenBoard, OpenEmailSettings, OpenWarnings).Show();

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
