using System.Windows;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(LocalDataService data)
    {
        InitializeComponent();
        DataContext = data.Board;
    }
}
