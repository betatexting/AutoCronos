using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Windows;

public partial class SuiteTicketApprovalWindow : ChromeWindow
{
    private readonly ObservableCollection<SuiteCustomerChoice> _choices;
    private readonly Func<Task<SuiteTicketApprovalEditor>>? _editorLoader;
    private IReadOnlyList<SuiteTicketOption> _executors = [];

    public SuiteTicketApprovalWindow(
        SuiteTicketApprovalEditor editor,
        Func<Task<SuiteTicketApprovalEditor>>? editorLoader = null)
    {
        _choices = [];
        _editorLoader = editorLoader;
        InitializeComponent();
        ApplyEditor(editor);
        CreateTicketsButton.IsEnabled = editorLoader is null;
        if (editorLoader is not null)
            Loaded += SuiteTicketApprovalWindow_Loaded;
    }

    private async void SuiteTicketApprovalWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_editorLoader is null)
            return;

        try
        {
            var editor = await _editorLoader();
            ApplyEditor(editor);
            LoadStatusTextBlock.Text = "Dados carregados. Revise e selecione as empresas antes de criar os chamados.";
            CreateTicketsButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            LoadStatusTextBlock.Text = "Nao foi possivel carregar os dados do Suite360.";
            SelectionHintTextBlock.Text = exception.Message;
            CreateTicketsButton.IsEnabled = false;
        }
    }

    private void ApplyEditor(SuiteTicketApprovalEditor editor)
    {
        _choices.Clear();
        foreach (var candidate in editor.Candidates)
            _choices.Add(new SuiteCustomerChoice(candidate));
        if (_choices.Count == 1)
            _choices[0].IsSelected = true;
        _executors = editor.Executors;
        ActivityTextBlock.Text = editor.ActivityName;
        SelectionHintTextBlock.Text = _choices.Count == 0
            ? "Aguardando a localizacao das empresas pelo e-mail ou CPF/CNPJ do card."
            : string.IsNullOrWhiteSpace(editor.SourceEmail)
            ? "Selecione as empresas localizadas pelo CPF/CNPJ informado no card."
            : $"Selecione uma ou mais empresas vinculadas ao e-mail {editor.SourceEmail}.";
        TicketTypeComboBox.ItemsSource = editor.TicketTypes;
        OriginComboBox.ItemsSource = editor.Origins;
        SectorComboBox.ItemsSource = editor.Sectors;
        TicketTitleTextBox.Text = editor.Title;
        TicketDescriptionTextBox.Text = editor.Description;
        CustomersListBox.ItemsSource = _choices;
        UpdateExecutors();
    }

    public IReadOnlyList<long> SelectedCustomerIds => _choices.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToList();

    public SuiteTicketSubmission? Submission =>
        TicketTypeComboBox.SelectedItem is not SuiteTicketOption ticketType ||
        OriginComboBox.SelectedItem is not SuiteTicketOption origin ||
        SectorComboBox.SelectedItem is not SuiteTicketOption sector
            ? null
            : new SuiteTicketSubmission(
                SelectedCustomerIds,
                ticketType.Id,
                origin.Id,
                sector.Id,
                (ExecutorComboBox.SelectedItem as SuiteTicketOption)?.Id,
                TicketTitleTextBox.Text,
                TicketDescriptionTextBox.Text);

    private void CreateTickets_Click(object sender, RoutedEventArgs e)
    {
        if (Submission is null)
        {
            MessageBox.Show(this, "Selecione ao menos uma empresa, o tipo, a origem e o setor do chamado.", "Chamados", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Customers_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject element)
            return;
        ScrollViewerWheel.Scroll(element, e.Delta);
        e.Handled = true;
    }

    private void Sector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateExecutors();

    private void UpdateExecutors()
    {
        var selectedExecutorId = (ExecutorComboBox.SelectedItem as SuiteTicketOption)?.Id;
        var sectorId = (SectorComboBox.SelectedItem as SuiteTicketOption)?.Id;
        ExecutorComboBox.ItemsSource = sectorId is null
            ? _executors
            : _executors.Where(executor => executor.SectorId is null || executor.SectorId == sectorId).ToList();
        ExecutorComboBox.SelectedItem = _executors.FirstOrDefault(executor => executor.Id == selectedExecutorId);
    }

    private sealed class SuiteCustomerChoice(SuiteCustomerCandidate candidate)
    {
        public long Id { get; } = candidate.Id;
        public string CompanyName { get; } = candidate.CompanyName;
        public string TaxId { get; } = candidate.TaxId ?? "CPF/CNPJ nao informado";
        public bool IsSelected { get; set; }
    }
}
