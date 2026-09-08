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
        ConnectButton.IsEnabled = status.IsConfigured;
        ConnectButton.Content = status.IsConnected ? "Conectar outra conta" : "Conectar com Google";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var isChangingAccount = _data.GetEmailStatus().IsConnected;
        if (isChangingAccount &&
            MessageBox.Show(
                this,
                "Deseja conectar outra conta? A integracao passara a usar a conta escolhida no Google.",
                "Conectar outra conta",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ConnectButton.IsEnabled = false;
        StatusTextBlock.Text = isChangingAccount ? "Escolha a nova conta no Google" : "Aguardando autorizacao no Google";
        DetailTextBlock.Text = isChangingAccount
            ? "Selecione a conta que passara a ser usada pelo AutoCronos na janela do navegador."
            : "Conclua o acesso na janela do navegador. O AutoCronos continuara automaticamente depois disso.";
        try
        {
            var status = isChangingAccount
                ? await _data.ConnectAnotherEmailAsync()
                : await _data.ConnectEmailAsync();
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
