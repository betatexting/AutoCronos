using System.Windows;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class CardDetailsWindow : Window
{
    private readonly LocalDataService _data;
    private readonly TaskCardDetails? _details;
    private readonly Guid? _newCardBoardId;
    private readonly string? _newCardColumnName;

    public CardDetailsWindow(LocalDataService data, Guid occurrenceId)
    {
        _data = data;
        _details = _data.GetTaskCardDetails(occurrenceId);
        InitializeComponent();

        CompanyNameTextBox.Text = _details.CompanyName;
        TaxIdTextBox.Text = _details.TaxId;
        CompetenceTextBox.Text = _details.Competence ?? string.Empty;
        DeadlineDatePicker.SelectedDate = _details.DeadlineAtUtc?.ToLocalTime().Date;
        ColumnComboBox.ItemsSource = _details.Columns;
        ColumnComboBox.SelectedItem = _details.CurrentColumn;
        ReceivedAtTextBlock.Text = _details.ReceivedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        EmailSubjectTextBox.Text = _details.EmailSubject;
    }

    public CardDetailsWindow(LocalDataService data, Guid boardId, string initialColumnName)
    {
        _data = data;
        _newCardBoardId = boardId;
        _newCardColumnName = initialColumnName;
        InitializeComponent();

        Title = "Novo card";
        HeadingTextBlock.Text = "Novo card";
        DescriptionTextBlock.Text = "Preencha os dados para adicionar o card ao quadro.";
        OriginDetailsPanel.Visibility = Visibility.Collapsed;
        ColumnComboBox.ItemsSource = _data.GetBoardColumns(boardId);
        ColumnComboBox.SelectedItem = initialColumnName;
        ColumnComboBox.IsEnabled = false;
        CompanyNameTextBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnComboBox.SelectedItem is not string selectedColumn)
        {
            MessageBox.Show(this, "Selecione uma coluna.", "Detalhes do card", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DateTime? deadlineAtUtc = DeadlineDatePicker.SelectedDate is { } deadlineDate
            ? DateTime.SpecifyKind(deadlineDate.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        SaveButton.IsEnabled = false;
        try
        {
            if (_details is not null)
            {
                var updatedDetails = _details with
                {
                    CompanyName = CompanyNameTextBox.Text,
                    TaxId = TaxIdTextBox.Text,
                    Competence = CompetenceTextBox.Text,
                    DeadlineAtUtc = deadlineAtUtc,
                    CurrentColumn = selectedColumn
                };
                await _data.SaveTaskCardDetailsAsync(updatedDetails);
            }
            else
            {
                await _data.CreateManualTaskCardAsync(new ManualTaskCardInput(
                    _newCardBoardId!.Value,
                    _newCardColumnName!,
                    CompanyNameTextBox.Text,
                    TaxIdTextBox.Text,
                    CompetenceTextBox.Text,
                    deadlineAtUtc));
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Detalhes do card", MessageBoxButton.OK, MessageBoxImage.Warning);
            SaveButton.IsEnabled = true;
        }
    }
}
