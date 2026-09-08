using System.Windows;

namespace AutoCronos.Desktop.Windows;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        HeadingTextBlock.Text = title;
        PromptTextBlock.Text = prompt;
        Loaded += (_, _) => ValueTextBox.Focus();
    }

    public string Value => ValueTextBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueTextBox.Text))
        {
            MessageBox.Show(this, "Informe um nome.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
