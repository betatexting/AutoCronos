using System.Windows;

namespace AutoCronos.Desktop.Windows;

public partial class FloatingLauncherWindow : Window
{
    private readonly Action _openBoard;
    private readonly Action _openEmail;
    private readonly Action _openWarnings;

    public FloatingLauncherWindow(Action openBoard, Action openEmail, Action openWarnings)
    {
        _openBoard = openBoard;
        _openEmail = openEmail;
        _openWarnings = openWarnings;
        InitializeComponent();
        Loaded += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Right - Width - 22; Top = area.Bottom - Height - 22; };
    }

    private void LauncherButton_Click(object sender, RoutedEventArgs e) => MenuPopup.IsOpen = true;
    private void Board_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; _openBoard(); }
    private void Email_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; _openEmail(); }
    private void Warnings_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; _openWarnings(); }
}
