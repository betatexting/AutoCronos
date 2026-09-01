using System.Configuration;
using System.Data;
using System.Windows;

namespace AutoCronos.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly AutoCronos.Desktop.Services.LocalDataService _data = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _data.Initialize();
        new AutoCronos.Desktop.Services.StartupService().Enable();
        new AutoCronos.Desktop.Windows.FloatingLauncherWindow(OpenBoard, OpenWarnings).Show();
    }

    private void OpenBoard()
    {
        var window = Current.Windows.OfType<MainWindow>().FirstOrDefault() ?? new MainWindow(_data);
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
}

