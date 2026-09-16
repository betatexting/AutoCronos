using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class WarningWindow : ChromeWindow
{
    private readonly LocalDataService _data;

    public WarningWindow(LocalDataService data)
    {
        _data = data;
        InitializeComponent();
        _data.StateChanged += Data_StateChanged;
        Closed += (_, _) => _data.StateChanged -= Data_StateChanged;
        RefreshWarnings();
    }

    private void Data_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshWarnings);

    private void RefreshWarnings() => DataContext = _data.Warnings;

    private async void Resolve_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WarningItem warning, CommandParameter: bool approve })
            return;

        try
        {
            if (approve && warning.Type == ApprovalType.CreateSuiteTicket)
            {
                await SuiteTicketApprovalWorkflow.OpenAsync(this, _data, warning.Id);
            }
            else
            {
                await _data.ResolveApprovalAsync(warning.Id, approve);
            }
            RefreshWarnings();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Resolver pendencia", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PendingItems_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject element)
            return;
        ScrollViewerWheel.Scroll(element, e.Delta);
        e.Handled = true;
    }
}
