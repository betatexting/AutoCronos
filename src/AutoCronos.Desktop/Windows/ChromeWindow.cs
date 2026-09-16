using System.Windows;
using System.Windows.Input;

namespace AutoCronos.Desktop.Windows;

public class ChromeWindow : Window
{
    protected void ChromeTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    protected void ChromeMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    protected void ChromeMaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleWindowState();

    protected void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;
}
