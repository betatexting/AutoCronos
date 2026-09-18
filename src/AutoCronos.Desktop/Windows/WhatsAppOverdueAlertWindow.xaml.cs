using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AutoCronos.Desktop.Windows;

public partial class WhatsAppOverdueAlertWindow : Window
{
    private readonly Action _openWhatsAppMonitor;
    private readonly DispatcherTimer _blinkTimer;
    private int _count;
    private bool _acknowledged;
    private DateTime _hiddenUntilUtc;
    private Point _dragStart;
    private bool _wasDragged;

    public bool IsSnoozed => DateTime.UtcNow < _hiddenUntilUtc;

    public WhatsAppOverdueAlertWindow(Action openWhatsAppMonitor)
    {
        _openWhatsAppMonitor = openWhatsAppMonitor;
        InitializeComponent();
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _blinkTimer.Tick += (_, _) => AlertVisual.Opacity = AlertVisual.Opacity < 0.7 ? 1 : 0.32;
        Loaded += (_, _) => PositionAtTopRight();
    }

    public void UpdateCount(int count)
    {
        if (count <= 0)
        {
            _count = 0;
            _acknowledged = false;
            _hiddenUntilUtc = DateTime.MinValue;
            StopBlinking();
            Hide();
            return;
        }

        if (DateTime.UtcNow < _hiddenUntilUtc)
        {
            _count = count;
            CountTextBlock.Text = count > 99 ? "99+" : count.ToString();
            return;
        }

        var shouldRestartAlert = _count == 0 || count > _count;
        _count = count;
        CountTextBlock.Text = count > 99 ? "99+" : count.ToString();
        if (shouldRestartAlert)
        {
            _acknowledged = false;
            StartBlinking();
        }
        else if (!_acknowledged && !_blinkTimer.IsEnabled)
        {
            StartBlinking();
        }
    }

    private void AlertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wasDragged)
        {
            _wasDragged = false;
            return;
        }

        _acknowledged = true;
        StopBlinking();
        _openWhatsAppMonitor();
    }

    private void CloseAlertButton_Click(object sender, RoutedEventArgs e)
    {
        _hiddenUntilUtc = DateTime.UtcNow.AddMinutes(5);
        _acknowledged = false;
        StopBlinking();
        Hide();
        e.Handled = true;
    }

    private void Draggable_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _wasDragged = false;
    }

    private void Draggable_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _wasDragged = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void StartBlinking()
    {
        AlertVisual.Opacity = 1;
        _blinkTimer.Start();
    }

    private void StopBlinking()
    {
        _blinkTimer.Stop();
        AlertVisual.Opacity = 1;
    }

    private void PositionAtTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
    }
}
