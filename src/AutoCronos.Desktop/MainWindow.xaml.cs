using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AutoCronos.Desktop.Domain;
using AutoCronos.Desktop.Services;

namespace AutoCronos.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalDataService _data;
    private TaskCard? _selectedCard;
    private Point _dragStartPosition;
    private bool _dragInProgress;
    private bool _refreshingBoards;
    private readonly DispatcherTimer _elapsedTimeTimer;

    public MainWindow(LocalDataService data)
    {
        _data = data;
        InitializeComponent();
        _elapsedTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _elapsedTimeTimer.Tick += (_, _) => UpdateElapsedTimes();
        _elapsedTimeTimer.Start();
        _data.StateChanged += Data_StateChanged;
        Closed += (_, _) =>
        {
            _elapsedTimeTimer.Stop();
            _data.StateChanged -= Data_StateChanged;
        };
        RefreshView();
    }

    private void Data_StateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshView);

    private void UpdateElapsedTimes()
    {
        foreach (var card in _data.Board.Columns.SelectMany(column => column.Cards))
            card.UpdateElapsedTime();
    }

    private void RefreshView()
    {
        BoardTitleTextBlock.Text = _data.Board.Name;
        ColumnsItemsControl.ItemsSource = _data.Board.Columns;
        _refreshingBoards = true;
        BoardComboBox.ItemsSource = _data.Boards;
        var selectedBoard = _data.Boards.FirstOrDefault(item => item.Id == _data.Board.Id);
        BoardComboBox.SelectedItem = selectedBoard;
        EditBoardRulesButton.IsEnabled = selectedBoard?.Id is { } selectedBoardId && !_data.IsDevolutionBoard(selectedBoardId);
        DeleteBoardButton.IsEnabled = selectedBoard?.CanDelete == true;
        _refreshingBoards = false;
        _selectedCard = null;
        DeleteSelectedCardButton.IsEnabled = false;

        var status = _data.GetEmailStatus();
        EmailStatusTextBlock.Text = status.StatusText;
        EmailDetailTextBlock.Text = status.DetailText;
        SyncEmailButton.IsEnabled = status.IsConnected;

        if (status.IsConnected)
        {
            EmailStatusBorder.Background = Brush("#E7F6EC");
            EmailStatusTextBlock.Foreground = Brush("#237A45");
            EmailDetailTextBlock.Foreground = Brush("#237A45");
        }
        else if (status.IsConfigured)
        {
            EmailStatusBorder.Background = Brush("#FFF2D9");
            EmailStatusTextBlock.Foreground = Brush("#7E5613");
            EmailDetailTextBlock.Foreground = Brush("#7E5613");
        }
        else
        {
            EmailStatusBorder.Background = Brush("#FCEBEA");
            EmailStatusTextBlock.Foreground = Brush("#A4382E");
            EmailDetailTextBlock.Foreground = Brush("#A4382E");
        }
    }

    private async void BoardComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingBoards || BoardComboBox.SelectedItem is not BoardOption option)
            return;

        if (option.IsCreateNew)
        {
            new AutoCronos.Desktop.Windows.BoardCreationWindow(_data) { Owner = this }.ShowDialog();
            RefreshView();
            return;
        }

        if (option.Id is not { } boardId || boardId == _data.Board.Id)
            return;

        try
        {
            await _data.SelectBoardAsync(boardId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Selecionar quadro", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshView();
        }
    }

    private async void DeleteBoard_Click(object sender, RoutedEventArgs e)
    {
        if (BoardComboBox.SelectedItem is not BoardOption { Id: { } boardId, CanDelete: true })
            return;
        if (MessageBox.Show(this, "Tem certeza que deseja excluir este quadro?", "Excluir quadro", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DeleteBoardButton.IsEnabled = false;
        try
        {
            await _data.DeleteBoardAsync(boardId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Excluir quadro", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshView();
        }
    }

    private void EditBoardRules_Click(object sender, RoutedEventArgs e)
    {
        if (BoardComboBox.SelectedItem is not BoardOption { Id: { } boardId } || _data.IsDevolutionBoard(boardId))
            return;

        try
        {
            new AutoCronos.Desktop.Windows.BoardCreationWindow(_data, boardId) { Owner = this }.ShowDialog();
            RefreshView();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Editar regras", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddManualCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KanbanColumn column)
            return;
        if (_data.IsDevolutionBoard(_data.Board.Id))
            new AutoCronos.Desktop.Windows.CardDetailsWindow(_data, _data.Board.Id, column.Name) { Owner = this }.ShowDialog();
        else
            new AutoCronos.Desktop.Windows.CustomCardDetailsWindow(_data, _data.Board.Id, initialColumnName: column.Name) { Owner = this }.ShowDialog();
        e.Handled = true;
    }

    private async void AddColumn_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new AutoCronos.Desktop.Windows.TextPromptWindow("Nova coluna", "Informe o nome da nova coluna.") { Owner = this };
        if (prompt.ShowDialog() != true)
            return;

        try
        {
            await _data.AddColumnAsync(_data.Board.Id, prompt.Value);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Nova coluna", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteColumn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KanbanColumn column)
            return;
        if (MessageBox.Show(this, "Tem certeza que deseja excluir esta coluna?", "Excluir coluna", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await _data.DeleteColumnAsync(_data.Board.Id, column.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Excluir coluna", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private async void SyncEmail_Click(object sender, RoutedEventArgs e)
    {
        SyncEmailButton.IsEnabled = false;
        try
        {
            var summary = await _data.SyncEmailAsync();
            RefreshView();
            MessageBox.Show(this, summary.Message, "Sincronizacao de e-mail", MessageBoxButton.OK, summary.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            RefreshView();
        }
    }

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskCard card)
            return;

        SelectCard(card);
        _dragStartPosition = e.GetPosition(this);
        _dragInProgress = false;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragInProgress)
        {
            _dragInProgress = false;
            return;
        }
        if (IsWithinButton(e.OriginalSource as DependencyObject) || (sender as FrameworkElement)?.DataContext is not TaskCard card)
            return;

        try
        {
            if (_data.IsDevolutionBoard(_data.Board.Id))
                new AutoCronos.Desktop.Windows.CardDetailsWindow(_data, card.OccurrenceId) { Owner = this }.ShowDialog();
            else
                new AutoCronos.Desktop.Windows.CustomCardDetailsWindow(_data, _data.Board.Id, card.OccurrenceId) { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Detalhes do card", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _selectedCard is null || sender is not UIElement source)
            return;

        var currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _dragStartPosition.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPosition.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragInProgress = true;
        var data = new DataObject(typeof(TaskCard), _selectedCard);
        DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TaskCard)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: KanbanColumn column } ||
            e.Data.GetData(typeof(TaskCard)) is not TaskCard card)
            return;

        try
        {
            await _data.MoveTaskCardAsync(card.OccurrenceId, column.Name);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Mover card", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void ColumnCards_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private async void DeleteCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TaskCard card)
            return;

        SelectCard(card);
        await DeleteSelectedCardAsync();
        e.Handled = true;
    }

    private async void DeleteSelectedCard_Click(object sender, RoutedEventArgs e) => await DeleteSelectedCardAsync();

    private async Task DeleteSelectedCardAsync()
    {
        if (_selectedCard is null)
            return;

        var confirmation = MessageBox.Show(
            this,
            "Tem certeza que deseja excluir este card?",
            "Excluir card",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        DeleteSelectedCardButton.IsEnabled = false;
        try
        {
            await _data.DeleteTaskCardAsync(_selectedCard.OccurrenceId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Excluir card", MessageBoxButton.OK, MessageBoxImage.Warning);
            DeleteSelectedCardButton.IsEnabled = true;
        }
    }

    private void SelectCard(TaskCard card)
    {
        foreach (var existingCard in _data.Board.Columns.SelectMany(column => column.Cards))
            existingCard.IsSelected = ReferenceEquals(existingCard, card);

        _selectedCard = card;
        DeleteSelectedCardButton.IsEnabled = true;
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button)
                return true;
            element = element switch
            {
                Visual => VisualTreeHelper.GetParent(element),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => null
            };
        }

        return false;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
