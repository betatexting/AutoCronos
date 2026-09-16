using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;
using Microsoft.Win32;

namespace AutoCronos.Desktop.Windows;

public partial class WhatsAppMonitorWindow : ChromeWindow
{
    private readonly WhatsAppMonitoringService _monitor;
    private readonly WhatsAppMonitorSettingsService _settings;
    private readonly ObservableCollection<SectorSelection> _sectors = [];
    private readonly HashSet<string> _selectedSummaryFilters = [];
    private bool _updatingSectorControls;

    public WhatsAppMonitorWindow(WhatsAppMonitoringService monitor, WhatsAppMonitorSettingsService settings)
    {
        _monitor = monitor;
        _settings = settings;
        InitializeComponent();
        SectorItemsControl.ItemsSource = _sectors;
        ResponseTimeTextBox.Text = _settings.Current.ResponseTimeMinutes.ToString();
        _monitor.SnapshotChanged += Monitor_SnapshotChanged;
        Closed += (_, _) =>
        {
            _monitor.SetActive(false);
            _monitor.SnapshotChanged -= Monitor_SnapshotChanged;
        };
        _monitor.SetActive(true);
        RefreshView();
    }

    private void Monitor_SnapshotChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RefreshView);

    private void RefreshView()
    {
        var snapshot = _monitor.Snapshot;
        UpdateSectorOptions(snapshot.Sectors);
        var hidden = _settings.Current.HiddenSectorIds;
        var search = SearchTextBox.Text.Trim();
        var eligible = snapshot.Conversations
            .Where(item => item.SectorId is null || !hidden.Contains(item.SectorId.Value))
            .Where(item => string.IsNullOrWhiteSpace(search) ||
                           item.ContactName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                           item.Phone.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                           item.Protocol.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var visible = eligible.Where(MatchesSelectedSummaryFilters).ToList();
        ConversationsListBox.ItemsSource = visible;
        OpenCountTextBlock.Text = eligible.Count(item => !item.IsTemplate).ToString();
        WaitingCountTextBlock.Text = eligible.Count(item => !item.IsTemplate && item.Status == "aguardando").ToString();
        InServiceCountTextBlock.Text = eligible.Count(item => !item.IsTemplate && item.Status == "em_atendimento").ToString();
        TemplateCountTextBlock.Text = eligible.Count(item => item.IsTemplate).ToString();
        OverdueCountTextBlock.Text = eligible.Count(item => item.IsOverdue).ToString();
        SyncStatusTextBlock.Text = snapshot.ErrorMessage is not null
            ? $"Falha na atualização: {snapshot.ErrorMessage}"
            : snapshot.UpdatedAtUtc == DateTime.MinValue
                ? "Carregando atendimentos..."
                : $"Atualizado às {snapshot.UpdatedAtUtc.ToLocalTime():HH:mm:ss}";
        IntegrationErrorBorder.Visibility = snapshot.ErrorMessage is not null && snapshot.Conversations.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        IntegrationErrorTextBlock.Text = snapshot.ErrorMessage ?? string.Empty;
    }

    private bool MatchesSelectedSummaryFilters(WhatsAppConversationView conversation)
    {
        if (_selectedSummaryFilters.Count == 0)
            return true;

        return (_selectedSummaryFilters.Contains("Open") && !conversation.IsTemplate) ||
               (_selectedSummaryFilters.Contains("Waiting") && !conversation.IsTemplate && conversation.Status == "aguardando") ||
               (_selectedSummaryFilters.Contains("InService") && !conversation.IsTemplate && conversation.Status == "em_atendimento") ||
               (_selectedSummaryFilters.Contains("Template") && conversation.IsTemplate) ||
               (_selectedSummaryFilters.Contains("Overdue") && conversation.IsOverdue);
    }

    private void SummaryCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Uid: { Length: > 0 } filter } card)
            return;

        if (!_selectedSummaryFilters.Add(filter))
            _selectedSummaryFilters.Remove(filter);

        card.Tag = _selectedSummaryFilters.Contains(filter) ? "Selected" : filter;
        RefreshView();
    }

    private void UpdateSectorOptions(IReadOnlyList<WhatsAppSectorView> sectors)
    {
        _updatingSectorControls = true;
        try
        {
            foreach (var sector in sectors)
            {
                var existing = _sectors.FirstOrDefault(item => item.Id == sector.Id);
                if (existing is null)
                {
                    _sectors.Add(new SectorSelection(
                        sector.Id,
                        sector.Name,
                        sector.OpenCount,
                        sector.Id is null || !_settings.Current.HiddenSectorIds.Contains(sector.Id.Value)));
                }
                else
                {
                    existing.Name = sector.Name;
                    existing.OpenCount = sector.OpenCount;
                }
            }
            foreach (var removed in _sectors.Where(item => sectors.All(sector => sector.Id != item.Id)).ToList())
                _sectors.Remove(removed);
        }
        finally
        {
            _updatingSectorControls = false;
        }
    }

    private void SectorVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSectorControls || sender is not CheckBox { DataContext: SectorSelection { Id: { } sectorId } sector })
            return;
        var wasVisible = !_settings.Current.HiddenSectorIds.Contains(sectorId);
        if (wasVisible == sector.IsVisible)
            return;
        _settings.SetSectorVisible(sectorId, sector.IsVisible);
        RefreshView();
    }

    private void ShowAllSectors_Click(object sender, RoutedEventArgs e) => SetAllSectorsVisible(true);
    private void HideAllSectors_Click(object sender, RoutedEventArgs e) => SetAllSectorsVisible(false);

    private void SetAllSectorsVisible(bool visible)
    {
        _settings.SetAllSectorsVisible(_sectors.Where(item => item.Id.HasValue).Select(item => item.Id!.Value), visible);
        _updatingSectorControls = true;
        foreach (var sector in _sectors)
            sector.IsVisible = visible || sector.Id is null;
        _updatingSectorControls = false;
        RefreshView();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshView();

    private void ExportFilteredConversations_Click(object sender, RoutedEventArgs e)
    {
        var conversations = ConversationsListBox.Items
            .Cast<WhatsAppConversationView>()
            .ToList();
        if (conversations.Count == 0)
        {
            MessageBox.Show(this, "Não há conversas visíveis para exportar.", "Exportar atendimentos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar atendimentos filtrados",
            Filter = "Arquivo CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"atendimentos-{DateTime.Now:yyyy-MM-dd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("Contato;Setor;Status;Tempo;Não lidas");
            foreach (var conversation in conversations)
            {
                csv.Append(CsvCell(conversation.ContactName)).Append(';')
                    .Append(CsvCell(conversation.SectorName)).Append(';')
                    .Append(CsvCell(conversation.StatusLabel)).Append(';')
                    .Append(CsvCell(conversation.ElapsedLabel)).Append(';')
                    .Append(conversation.UnreadCount)
                    .AppendLine();
            }
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            MessageBox.Show(this, $"{conversations.Count} conversa(s) exportada(s) com sucesso.", "Exportar atendimentos", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Não foi possível exportar o arquivo: {exception.Message}", "Exportar atendimentos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string CsvCell(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private void ConversationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationsListBox.SelectedItem is not WhatsAppConversationView conversation)
            return;
        ConversationsListBox.SelectedItem = null;
        if (string.IsNullOrWhiteSpace(conversation.HashId))
        {
            MessageBox.Show(this, "O Suite360 não retornou o identificador de acesso desta conversa.", "Abrir atendimento", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var url = $"https://suiteweb.contasnet.com.br/whatsapp/atendimento?conversa={Uri.EscapeDataString(conversation.HashId)}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Não foi possível abrir a conversa no navegador: {exception.Message}", "Abrir atendimento", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResponseTimeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveResponseTime();
            Keyboard.ClearFocus();
        }
    }

    private void ResponseTimeTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveResponseTime();

    private void SaveResponseTime()
    {
        if (!int.TryParse(ResponseTimeTextBox.Text, out var minutes) || minutes is < 1 or > 1440)
        {
            ResponseTimeTextBox.Text = _settings.Current.ResponseTimeMinutes.ToString();
            return;
        }
        if (minutes == _settings.Current.ResponseTimeMinutes)
            return;
        _settings.SetResponseTime(minutes);
        _ = _monitor.RefreshNowAsync();
    }

    private sealed class SectorSelection(long? id, string name, int openCount, bool isVisible) : INotifyPropertyChanged
    {
        private string _name = name;
        private int _openCount = openCount;
        private bool _isVisible = isVisible;
        public long? Id { get; } = id;
        public string Name { get => _name; set => SetField(ref _name, value); }
        public int OpenCount { get => _openCount; set => SetField(ref _openCount, value); }
        public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
