using System.Windows;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop.Windows;

public partial class WarningWindow : Window
{
    public WarningWindow(LocalDataService data) { InitializeComponent(); DataContext = data.Warnings; }
}
