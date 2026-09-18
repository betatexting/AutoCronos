using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoCronos.Desktop.Windows;

public partial class FloatingLauncherWindow : Window
{
    private const double OrbitRadius = 58;
    private const double LauncherCollapsedScale = 0.75;
    private const double DotExpandedScale = 2.00;
    private const int ResizeDurationMilliseconds = 430;
    private const int PositionDurationMilliseconds = 430;
    private const int OrbitDurationMilliseconds = 1800;
    private const int StackDurationMilliseconds = 600;

    private static readonly double[] InitialYPositions = [-21, -7, 7, 21];
    private static readonly double[] OrbitAngles = [-90, 0, 90, 180];
    private static readonly double[] FinalYPositions = [-176, -132, -88, -44];

    private readonly Action _openBoard;
    private readonly Action _openEmail;
    private readonly Action _openWarnings;
    private readonly Action _openWhatsApp;
    private bool _isMenuOpen;
    private bool _isAnimating;
    private Point _dragStart;
    private bool _wasDragged;

    public FloatingLauncherWindow(Action openBoard, Action openEmail, Action openWarnings, Action openWhatsApp)
    {
        _openBoard = openBoard;
        _openEmail = openEmail;
        _openWarnings = openWarnings;
        _openWhatsApp = openWhatsApp;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width + 10;
            Top = area.Bottom - Height - 2;
        };
    }

    private async void LauncherButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wasDragged)
        {
            _wasDragged = false;
            return;
        }

        if (_isAnimating)
            return;

        if (_isMenuOpen)
            await CloseMenuAsync();
        else
            await OpenMenuAsync();
    }

    private void Draggable_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _wasDragged = false;
    }

    private void Draggable_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isAnimating)
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

    private void CloseLauncherButton_Click(object sender, RoutedEventArgs e) =>
        System.Windows.Application.Current.Shutdown();

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        var destination = (sender as FrameworkElement)?.Tag switch
        {
            "Board" => _openBoard,
            "Email" => _openEmail,
            "Warnings" => _openWarnings,
            "WhatsApp" => _openWhatsApp,
            _ => null
        };

        if (destination is not null)
            await NavigateAsync(destination);
    }

    private async Task NavigateAsync(Action destination)
    {
        if (_isAnimating || !_isMenuOpen)
            return;

        destination();
        await CloseMenuAsync();
    }

    private async Task OpenMenuAsync()
    {
        _isAnimating = true;
        SetDotButtonsEnabled(false);

        var offsets = GetDotOffsets();
        var dotScales = GetDotScales();

        try
        {
            await AnimateAsync(PositionDurationMilliseconds, progress =>
            {
                var eased = EaseOut(progress);
                for (var index = 0; index < offsets.Length; index++)
                {
                    var angle = DegreesToRadians(OrbitAngles[index]);
                    var orbitX = Math.Cos(angle) * OrbitRadius;
                    var orbitY = Math.Sin(angle) * OrbitRadius;
                    SetPosition(
                        offsets[index],
                        Lerp(0, orbitX, eased),
                        Lerp(InitialYPositions[index], orbitY, eased));
                }
            });

            await AnimateAsync(ResizeDurationMilliseconds, progress =>
            {
                var eased = EaseOut(progress);
                SetScale(LauncherScale, Lerp(1, LauncherCollapsedScale, eased));
                foreach (var dotScale in dotScales)
                    SetScale(dotScale, Lerp(1, DotExpandedScale, eased));
            });

            await AnimateAsync(OrbitDurationMilliseconds, progress =>
            {
                var eased = EaseInOut(progress);
                for (var index = 0; index < offsets.Length; index++)
                {
                    var angle = DegreesToRadians(OrbitAngles[index] + (360 * eased));
                    SetPosition(
                        offsets[index],
                        Math.Cos(angle) * OrbitRadius,
                        Math.Sin(angle) * OrbitRadius);
                }
            });

            await AnimateAsync(StackDurationMilliseconds, progress =>
            {
                var eased = EaseInOut(progress);
                for (var index = 0; index < offsets.Length; index++)
                {
                    var angle = DegreesToRadians(OrbitAngles[index]);
                    var orbitX = Math.Cos(angle) * OrbitRadius;
                    var orbitY = Math.Sin(angle) * OrbitRadius;
                    SetPosition(
                        offsets[index],
                        Lerp(orbitX, 0, eased),
                        Lerp(orbitY, FinalYPositions[index], eased));
                }
            });

            _isMenuOpen = true;
            SetDotButtonsEnabled(true);
        }
        finally
        {
            _isAnimating = false;
        }
    }

    private async Task CloseMenuAsync()
    {
        _isAnimating = true;
        SetDotButtonsEnabled(false);

        var offsets = GetDotOffsets();
        var dotScales = GetDotScales();

        try
        {
            await AnimateAsync(StackDurationMilliseconds, progress =>
            {
                var eased = EaseInOut(progress);
                for (var index = 0; index < offsets.Length; index++)
                {
                    var angle = DegreesToRadians(OrbitAngles[index]);
                    var orbitX = Math.Cos(angle) * OrbitRadius;
                    var orbitY = Math.Sin(angle) * OrbitRadius;
                    SetPosition(
                        offsets[index],
                        Lerp(0, orbitX, eased),
                        Lerp(FinalYPositions[index], orbitY, eased));
                }
            });

            await AnimateAsync(OrbitDurationMilliseconds, progress =>
            {
                var eased = EaseInOut(progress);
                for (var index = 0; index < offsets.Length; index++)
                {
                    var angle = DegreesToRadians(OrbitAngles[index] - (360 * eased));
                    SetPosition(
                        offsets[index],
                        Math.Cos(angle) * OrbitRadius,
                        Math.Sin(angle) * OrbitRadius);
                }
            });

            await AnimateAsync(ResizeDurationMilliseconds, progress =>
            {
                var eased = EaseOut(progress);
                SetScale(LauncherScale, Lerp(LauncherCollapsedScale, 1, eased));
                foreach (var dotScale in dotScales)
                    SetScale(dotScale, Lerp(DotExpandedScale, 1, eased));
            });

            await AnimateAsync(PositionDurationMilliseconds, progress =>
            {
                var eased = EaseOut(progress);
                for (var index = 0; index < offsets.Length; index++)
                {
                    var angle = DegreesToRadians(OrbitAngles[index]);
                    var orbitX = Math.Cos(angle) * OrbitRadius;
                    var orbitY = Math.Sin(angle) * OrbitRadius;
                    SetPosition(
                        offsets[index],
                        Lerp(orbitX, 0, eased),
                        Lerp(orbitY, InitialYPositions[index], eased));
                }
            });

            _isMenuOpen = false;
        }
        finally
        {
            _isAnimating = false;
        }
    }

    private TranslateTransform[] GetDotOffsets() => [WhatsAppOffset, BoardOffset, EmailOffset, WarningsOffset];
    private ScaleTransform[] GetDotScales() => [WhatsAppDotScale, BoardDotScale, EmailDotScale, WarningsDotScale];

    private void SetDotButtonsEnabled(bool enabled)
    {
        BoardButton.IsHitTestVisible = enabled;
        EmailButton.IsHitTestVisible = enabled;
        WarningsButton.IsHitTestVisible = enabled;
        WhatsAppButton.IsHitTestVisible = enabled;
    }

    private static Task AnimateAsync(int durationMilliseconds, Action<double> update)
    {
        var completion = new TaskCompletionSource<bool>();
        var stopwatch = Stopwatch.StartNew();
        EventHandler? renderingHandler = null;

        renderingHandler = (_, _) =>
        {
            var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds);
            update(progress);

            if (progress < 1)
                return;

            CompositionTarget.Rendering -= renderingHandler;
            completion.TrySetResult(true);
        };

        CompositionTarget.Rendering += renderingHandler;
        return completion.Task;
    }

    private static double EaseOut(double value) => 1 - Math.Pow(1 - value, 3);

    private static double EaseInOut(double value) => value < 0.5
        ? 4 * value * value * value
        : 1 - (Math.Pow((-2 * value) + 2, 3) / 2);

    private static double Lerp(double from, double to, double progress) => from + ((to - from) * progress);
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static void SetPosition(TranslateTransform transform, double x, double y)
    {
        transform.X = x;
        transform.Y = y;
    }

    private static void SetScale(ScaleTransform transform, double scale)
    {
        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }
}
