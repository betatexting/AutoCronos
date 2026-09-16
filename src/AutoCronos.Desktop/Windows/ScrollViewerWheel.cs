using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoCronos.Desktop.Windows;

internal static class ScrollViewerWheel
{
    public static void Scroll(DependencyObject element, int delta)
    {
        if (FindScrollViewer(element) is { } scrollViewer)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - delta);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is ScrollViewer scrollViewer)
                return scrollViewer;
            if (FindScrollViewer(child) is { } nestedScrollViewer)
                return nestedScrollViewer;
        }

        return null;
    }
}
