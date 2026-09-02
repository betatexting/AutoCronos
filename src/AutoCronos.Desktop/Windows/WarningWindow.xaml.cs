using System.Windows;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class WarningWindow : Window
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

    private async void Approve_Click(object sender, RoutedEventArgs e) => await ResolveAsync(sender, true);
    private async void Reject_Click(object sender, RoutedEventArgs e) => await ResolveAsync(sender, false);

    private async Task ResolveAsync(object sender, bool approve)
    {
        if (((FrameworkElement)sender).Tag is not Guid id) return;
        await _data.ResolveApprovalAsync(id, approve);
        RefreshWarnings();
    }
}
