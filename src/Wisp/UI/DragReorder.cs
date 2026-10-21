using System.Windows;
using System.Windows.Controls;

namespace Wisp.UI;

/// <summary>Helpers for drag-to-reorder in horizontal or vertical item rows.</summary>
public static class DragReorder
{
    /// <summary>Movement (DIPs) before a press becomes a drag, so ordinary clicks still click.</summary>
    public const double Threshold = 6;

    public static bool PastThreshold(Point start, Point now) =>
        Math.Abs(now.X - start.X) > Threshold || Math.Abs(now.Y - start.Y) > Threshold;

    /// <summary>The index of the slot under the pointer, by comparing against each item's center.</summary>
    public static int IndexAt(ItemsControl items, Point pointer, Orientation orientation)
    {
        int count = items.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
            var bounds = c.TransformToAncestor(items).TransformBounds(new Rect(c.RenderSize));
            double center = orientation == Orientation.Horizontal ? bounds.Left + bounds.Width / 2 : bounds.Top + bounds.Height / 2;
            double at = orientation == Orientation.Horizontal ? pointer.X : pointer.Y;
            if (at < center) return i;
        }
        return count - 1;
    }

    public static UIElement? ContainerOf(ItemsControl items, object item) =>
        items.ItemContainerGenerator.ContainerFromItem(item) as UIElement;
}
