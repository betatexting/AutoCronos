using System.Windows;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class EmailSettingsWindow : Window
{
    private readonly LocalDataService _data;

    public EmailSettingsWindow(LocalDataService data)
    {
        _data = data;
        InitializeComponent();
        _data.StateChanged += Data_StateChanged;
        Closed += (_, _) => _data.StateChanged -= Data_StateChanged;
        RefreshStatus();
    }

    private void Data_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshStatus);

    private void RefreshStatus()
    {
        var status = _data.GetEmailStatus();
        StatusTextBlock.Text = status.StatusText;
        DetailTextBlock.Text = status.DetailText;
        ConnectedEmailTextBlock.Text = string.IsNullOrWhiteSpace(status.ConnectedEmail)
            ? "Nenhuma conta conectada."
            : $"Conta conectada: {status.ConnectedEmail}";

        DisconnectButton.IsEnabled = status.IsConnected;
        SyncButton.IsEnabled = status.IsConnected;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        StatusTextBlock.Text = "Aguardando autorizacao no Google";
        DetailTextBlock.Text = "Conclua o acesso na janela do navegador. O AutoCronos continuara automaticamente depois disso.";
        try
        {
            var status = await _data.ConnectEmailAsync();
            RefreshStatus();
            MessageBox.Show(this, status.DetailText, "Integracao de e-mail", MessageBoxButton.OK, status.IsConnected ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var status = await _data.DisconnectEmailAsync();
        RefreshStatus();
        MessageBox.Show(this, status.DetailText, "Integracao de e-mail", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var summary = await _data.SyncEmailAsync();
        RefreshStatus();
        MessageBox.Show(this, summary.Message, "Sincronizacao de e-mail", MessageBoxButton.OK, summary.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
