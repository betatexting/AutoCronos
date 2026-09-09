using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class CustomCardDetailsWindow : Window
{
    private readonly LocalDataService _data;
    private readonly CustomCardEditor _editor;
    private readonly ObservableCollection<FieldValueDraft> _fields;

    public CustomCardDetailsWindow(LocalDataService data, Guid boardId, Guid? occurrenceId = null, string? initialColumnName = null)
    {
        _data = data;
        _editor = _data.GetCustomCardEditor(boardId, occurrenceId, initialColumnName);
        _fields = new ObservableCollection<FieldValueDraft>(_editor.Fields.Select(field => new FieldValueDraft(field)));
        InitializeComponent();
        var isNew = occurrenceId is null;
        Title = isNew ? $"Novo card | {_editor.BoardName}" : $"Card | {_editor.BoardName}";
        HeadingTextBlock.Text = isNew ? "Novo card" : "Detalhes do card";
        DescriptionTextBlock.Text = isNew ? $"Preencha os campos definidos para o quadro {_editor.BoardName}." : $"Edite os campos definidos para o quadro {_editor.BoardName}.";
        FieldsItemsControl.ItemsSource = _fields;
        DeadlineTextBox.Text = _editor.DeadlineAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? string.Empty;
        ColumnComboBox.ItemsSource = _editor.Columns;
        ColumnComboBox.SelectedItem = _editor.CurrentColumn;
        ColumnComboBox.IsEnabled = !isNew;
        EmailSubjectTextBox.Text = _editor.EmailSubject;
        OriginPanel.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        UpdateElapsedTime();
    }

    private void UpdateElapsedTime()
    {
        var elapsed = (_editor.CompletedAtUtc ?? DateTime.UtcNow) - _editor.ReceivedAtUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        ElapsedTimeTextBlock.Text = elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays} dia(s) e {elapsed.Hours} hora(s)"
            : $"{(int)elapsed.TotalHours} hora(s) e {elapsed.Minutes} minuto(s)";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnComboBox.SelectedItem is not string selectedColumn)
        {
            MessageBox.Show(this, "Selecione uma coluna.", "Card", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DateTime? deadlineAtUtc = null;
        if (!string.IsNullOrWhiteSpace(DeadlineTextBox.Text))
        {
            if (!DateTime.TryParse(DeadlineTextBox.Text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AllowWhiteSpaces, out var deadlineDate))
            {
                MessageBox.Show(this, "Informe o prazo no formato dd/MM/aaaa HH:mm.", "Card", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            deadlineAtUtc = DateTime.SpecifyKind(deadlineDate, DateTimeKind.Local).ToUniversalTime();
        }
        var updated = _editor with
        {
            CurrentColumn = selectedColumn,
            DeadlineAtUtc = deadlineAtUtc,
            Fields = _fields.Select(field => new CustomCardFieldEditor(field.DefinitionId, field.Name, field.FieldType, field.IsRequired, field.Value)).ToList()
        };
        SaveButton.IsEnabled = false;
        try
        {
            await _data.SaveCustomCardAsync(updated);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Card", MessageBoxButton.OK, MessageBoxImage.Warning);
            SaveButton.IsEnabled = true;
        }
    }

    private sealed class FieldValueDraft(CustomCardFieldEditor field)
    {
        public Guid DefinitionId { get; } = field.DefinitionId;
        public string Name { get; } = field.Name;
        public CardFieldType FieldType { get; } = field.FieldType;
        public bool IsRequired { get; } = field.IsRequired;
        public string Value { get; set; } = field.Value;
        public string TypeHint => FieldType switch { CardFieldType.Number => "(numero)", CardFieldType.Date => "(data)", _ => string.Empty };
    }
}
