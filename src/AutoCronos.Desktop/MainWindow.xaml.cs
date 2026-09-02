using System.Windows;
using System.Windows.Media;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalDataService _data;

    public MainWindow(LocalDataService data)
    {
        _data = data;
        InitializeComponent();
        _data.StateChanged += Data_StateChanged;
        Closed += (_, _) => _data.StateChanged -= Data_StateChanged;
        RefreshView();
    }

    private void Data_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshView);

    private void RefreshView()
    {
        ColumnsItemsControl.ItemsSource = _data.Board.Columns;

        var status = _data.GetEmailStatus();
        EmailStatusTextBlock.Text = status.StatusText;
        EmailDetailTextBlock.Text = status.DetailText;
        SyncEmailButton.IsEnabled = status.IsConnected;

        if (status.IsConnected)
        {
            EmailStatusBorder.Background = Brush("#E7F6EC");
            EmailStatusTextBlock.Foreground = Brush("#237A45");
            EmailDetailTextBlock.Foreground = Brush("#237A45");
        }
        else if (status.IsConfigured)
        {
            EmailStatusBorder.Background = Brush("#FFF2D9");
            EmailStatusTextBlock.Foreground = Brush("#7E5613");
            EmailDetailTextBlock.Foreground = Brush("#7E5613");
        }
        else
        {
            EmailStatusBorder.Background = Brush("#FCEBEA");
            EmailStatusTextBlock.Foreground = Brush("#A4382E");
            EmailDetailTextBlock.Foreground = Brush("#A4382E");
        }
    }

    private void ManageEmail_Click(object sender, RoutedEventArgs e) =>
        new AutoCronos.Desktop.Windows.EmailSettingsWindow(_data) { Owner = this }.ShowDialog();

    private async void SyncEmail_Click(object sender, RoutedEventArgs e)
    {
        SyncEmailButton.IsEnabled = false;
        try
        {
            var summary = await _data.SyncEmailAsync();
            RefreshView();
            MessageBox.Show(this, summary.Message, "Sincronizacao de e-mail", MessageBoxButton.OK, summary.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            RefreshView();
        }
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
