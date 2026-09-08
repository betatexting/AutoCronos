using System.Windows;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class BoardCreationWindow : Window
{
    private readonly LocalDataService _data;

    public BoardCreationWindow(LocalDataService data)
    {
        _data = data;
        InitializeComponent();
        Loaded += (_, _) => BoardNameTextBox.Focus();
    }

    private void AutomaticMove_Changed(object sender, RoutedEventArgs e)
    {
        if (AutomaticRulePanel is not null)
            AutomaticRulePanel.IsEnabled = AutomaticMoveCheckBox.IsChecked == true;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var columns = new[]
        {
            Column1TextBox.Text,
            Column2TextBox.Text,
            Column3TextBox.Text,
            Column4TextBox.Text,
            Column5TextBox.Text
        };
        int? deadlineDays = null;
        string? targetColumn = null;
        if (AutomaticMoveCheckBox.IsChecked == true)
        {
            if (!int.TryParse(DeadlineDaysTextBox.Text, out var parsedDays))
            {
                MessageBox.Show(this, "Informe um prazo valido em dias.", "Criar quadro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            deadlineDays = parsedDays;
            targetColumn = TargetColumnTextBox.Text;
        }

        CreateButton.IsEnabled = false;
        try
        {
            await _data.CreateBoardAsync(new BoardCreationRequest(BoardNameTextBox.Text, columns, deadlineDays, targetColumn));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Criar quadro", MessageBoxButton.OK, MessageBoxImage.Warning);
            CreateButton.IsEnabled = true;
        }
    }
}
