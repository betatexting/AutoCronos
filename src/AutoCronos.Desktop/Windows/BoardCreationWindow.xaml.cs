using System.Collections.ObjectModel;
using System.Windows;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class BoardCreationWindow : Window
{
    private readonly LocalDataService _data;
    private readonly Guid? _boardId;
    private IReadOnlyList<string> _existingColumnNames = [];

    public ObservableCollection<CardFieldDraft> Fields { get; } = [];

    public IReadOnlyList<Choice<CardFieldType>> FieldTypeOptions { get; } =
    [new("Texto", CardFieldType.Text), new("Numero", CardFieldType.Number), new("Data", CardFieldType.Date)];

    public IReadOnlyList<Choice<EmailFieldSource>> EmailSourceOptions { get; } =
    [
        new("Sem preenchimento", EmailFieldSource.None),
        new("Assunto", EmailFieldSource.Subject),
        new("Remetente", EmailFieldSource.Sender),
        new("Razao social", EmailFieldSource.CompanyName),
        new("CPF/CNPJ", EmailFieldSource.TaxId),
        new("Competencia", EmailFieldSource.Competence),
        new("Data de recebimento", EmailFieldSource.ReceivedAt),
        new("Corpo do e-mail", EmailFieldSource.Body)
    ];

    public IReadOnlyList<Choice<DeadlineUnit>> DeadlineUnitOptions { get; } =
    [new("Horas", DeadlineUnit.Hours), new("Dias corridos", DeadlineUnit.CalendarDays), new("Dias uteis", DeadlineUnit.BusinessDays)];

    public BoardCreationWindow(LocalDataService data) : this(data, null)
    {
    }

    public BoardCreationWindow(LocalDataService data, Guid? boardId)
    {
        _data = data;
        _boardId = boardId;
        DataContext = this;
        InitializeComponent();
        FieldsItemsControl.ItemsSource = Fields;
        DeadlineUnitComboBox.ItemsSource = DeadlineUnitOptions;
        DeadlineUnitComboBox.SelectedValue = DeadlineUnit.CalendarDays;
        if (boardId is { } existingBoardId)
            LoadExistingBoard(existingBoardId);
        else
            AddDefaultFields();
        Loaded += (_, _) => BoardNameTextBox.Focus();
    }

    private void AddDefaultFields()
    {
        Fields.Add(new CardFieldDraft { Name = "Titulo", IsRequired = true, ShowOnCard = true, EmailSource = EmailFieldSource.Subject });
        Fields.Add(new CardFieldDraft { Name = "Responsavel", ShowOnCard = true, EmailSource = EmailFieldSource.Sender });
        Fields.Add(new CardFieldDraft { Name = "CPF/CNPJ", EmailSource = EmailFieldSource.TaxId });
    }

    private void LoadExistingBoard(Guid boardId)
    {
        var editor = _data.GetBoardRulesEditor(boardId);
        _existingColumnNames = editor.ColumnNames;
        Title = $"AutoCronos | Regras de {editor.Name}";
        WindowHeadingTextBlock.Text = "Editar regras do quadro";
        WindowDescriptionTextBlock.Text = "Altere os campos dos cards, a captura de e-mail e a contabilizacao de prazo. Os campos sao atualizados no quadro; as novas regras valem nas proximas sincronizacoes e movimentacoes.";
        CreateButton.Content = "Salvar alteracoes";
        BoardNameTextBox.Text = editor.Name;
        ColumnsEditorPanel.Visibility = Visibility.Collapsed;
        ExistingColumnsPanel.Visibility = Visibility.Visible;
        ExistingColumnsTextBlock.Text = string.Join("  •  ", editor.ColumnNames);
        SubjectPatternsTextBox.Text = string.Join(Environment.NewLine, editor.EmailSubjectPatterns);
        AutomaticMoveCheckBox.IsChecked = editor.AutomaticMoveAmount is not null;
        DeadlineAmountTextBox.Text = editor.AutomaticMoveAmount?.ToString() ?? "30";
        DeadlineUnitComboBox.SelectedValue = editor.AutomaticMoveUnit ?? DeadlineUnit.CalendarDays;
        TargetColumnTextBox.Text = editor.AutomaticMoveTargetColumn ?? editor.ColumnNames.LastOrDefault() ?? string.Empty;

        foreach (var field in editor.CardFields)
        {
            Fields.Add(new CardFieldDraft
            {
                Id = field.Id,
                Name = field.Name,
                FieldType = field.FieldType,
                IsRequired = field.IsRequired,
                ShowOnCard = field.ShowOnCard,
                EmailSource = field.EmailSource
            });
        }
    }

    private void AddField_Click(object sender, RoutedEventArgs e) => Fields.Add(new CardFieldDraft());

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CardFieldDraft field)
        {
            if (field.Id.HasValue && MessageBox.Show(
                    this,
                    $"Excluir o campo '{field.Name}' tambem removera seu preenchimento dos cards existentes. Deseja continuar?",
                    "Excluir campo",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            Fields.Remove(field);
        }
    }

    private void AutomaticMove_Changed(object sender, RoutedEventArgs e)
    {
        if (AutomaticRulePanel is not null)
            AutomaticRulePanel.IsEnabled = AutomaticMoveCheckBox.IsChecked == true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var columns = _boardId is null
            ? new[] { Column1TextBox.Text, Column2TextBox.Text, Column3TextBox.Text, Column4TextBox.Text, Column5TextBox.Text }
            : _existingColumnNames;
        var patterns = SubjectPatternsTextBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? amount = null;
        DeadlineUnit? unit = null;
        string? targetColumn = null;
        if (AutomaticMoveCheckBox.IsChecked == true)
        {
            if (!int.TryParse(DeadlineAmountTextBox.Text, out var parsedAmount))
            {
                MessageBox.Show(this, "Informe uma quantidade de prazo valida.", WindowHeadingTextBlock.Text, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (DeadlineUnitComboBox.SelectedValue is not DeadlineUnit selectedUnit)
            {
                MessageBox.Show(this, "Selecione a unidade do prazo.", WindowHeadingTextBlock.Text, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            amount = parsedAmount;
            unit = selectedUnit;
            targetColumn = TargetColumnTextBox.Text;
        }

        var fields = Fields.Select(field => new CardFieldDefinitionInput(field.Name, field.FieldType, field.IsRequired, field.ShowOnCard, field.EmailSource, field.Id)).ToList();
        CreateButton.IsEnabled = false;
        try
        {
            if (_boardId is { } boardId)
            {
                await _data.UpdateBoardRulesAsync(new BoardRulesUpdateRequest(
                    boardId,
                    BoardNameTextBox.Text,
                    fields,
                    patterns,
                    amount,
                    unit,
                    targetColumn));
            }
            else
            {
                await _data.CreateBoardAsync(new BoardCreationRequest(BoardNameTextBox.Text, columns, fields, patterns, amount, unit, targetColumn));
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, WindowHeadingTextBlock.Text, MessageBoxButton.OK, MessageBoxImage.Warning);
            CreateButton.IsEnabled = true;
        }
    }

    public sealed class CardFieldDraft
    {
        public Guid? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public CardFieldType FieldType { get; set; } = CardFieldType.Text;
        public bool IsRequired { get; set; }
        public bool ShowOnCard { get; set; }
        public EmailFieldSource EmailSource { get; set; }
    }

    public sealed record Choice<T>(string Label, T Value);
}
