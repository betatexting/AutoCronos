using System.Windows;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class EmailSettingsWindow : ChromeWindow
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
        StatusTextBlock.Text = status.IsConnected
            ? "GMAIL CONECTADO"
            : status.IsConfigured
                ? "GMAIL CONFIGURADO"
                : "GMAIL NAO CONFIGURADO";
        DetailTextBlock.Text = status.DetailText.ToUpperInvariant();
        ConnectedEmailTextBlock.Text = string.IsNullOrWhiteSpace(status.ConnectedEmail)
            ? "NENHUMA CONTA CONECTADA"
            : $"CONTA CONECTADA: {status.ConnectedEmail.ToUpperInvariant()}";

        DisconnectButton.IsEnabled = status.IsConnected;
        SyncButton.IsEnabled = status.IsConnected;
        ConnectButton.IsEnabled = status.IsConfigured;
        ConnectButton.Content = status.IsConnected ? "CONECTAR OUTRA CONTA" : "CONECTAR COM GOOGLE";
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
        StatusTextBlock.Text = isChangingAccount ? "ESCOLHA A NOVA CONTA NO GOOGLE" : "AGUARDANDO AUTORIZACAO NO GOOGLE";
        DetailTextBlock.Text = isChangingAccount
            ? "SELECIONE A CONTA QUE PASSARA A SER USADA PELO AUTOCRONOS NA JANELA DO NAVEGADOR."
            : "CONCLUA O ACESSO NA JANELA DO NAVEGADOR. O AUTOCRONOS CONTINUARA AUTOMATICAMENTE DEPOIS DISSO.";
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
