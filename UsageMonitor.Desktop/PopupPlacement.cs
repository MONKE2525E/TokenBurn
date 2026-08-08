using System.Drawing;

namespace UsageMonitor.Desktop;

internal enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

internal static class PopupPlacement
{
    public static Rectangle NearWidget(
        Rectangle widget,
        Rectangle taskbar,
        Rectangle workingArea,
        Size popupSize,
        int gap = 8)
    {
        var edge = GetTaskbarEdge(taskbar, workingArea);
        var candidate = edge switch
        {
            TaskbarEdge.Top => new Rectangle(widget.Right - popupSize.Width, taskbar.Bottom + gap, popupSize.Width, popupSize.Height),
            TaskbarEdge.Left => new Rectangle(taskbar.Right + gap, widget.Bottom - popupSize.Height, popupSize.Width, popupSize.Height),
            TaskbarEdge.Right => new Rectangle(taskbar.Left - popupSize.Width - gap, widget.Bottom - popupSize.Height, popupSize.Width, popupSize.Height),
            _ => new Rectangle(widget.Right - popupSize.Width, taskbar.Top - popupSize.Height - gap, popupSize.Width, popupSize.Height)
        };

        return Clamp(candidate, workingArea, gap);
    }

    public static Rectangle NearTaskbar(
        Point anchor,
        Rectangle taskbar,
        Rectangle workingArea,
        Size popupSize,
        int gap = 8)
    {
        if (taskbar.IsEmpty)
        {
            return Clamp(
                new Rectangle(anchor.X - popupSize.Width / 2, anchor.Y - popupSize.Height - gap, popupSize.Width, popupSize.Height),
                workingArea,
                gap);
        }

        var edge = GetTaskbarEdge(taskbar, workingArea);
        var candidate = edge switch
        {
            TaskbarEdge.Top => new Rectangle(anchor.X - popupSize.Width / 2, taskbar.Bottom + gap, popupSize.Width, popupSize.Height),
            TaskbarEdge.Left => new Rectangle(taskbar.Right + gap, anchor.Y - popupSize.Height / 2, popupSize.Width, popupSize.Height),
            TaskbarEdge.Right => new Rectangle(taskbar.Left - popupSize.Width - gap, anchor.Y - popupSize.Height / 2, popupSize.Width, popupSize.Height),
            _ => new Rectangle(anchor.X - popupSize.Width / 2, taskbar.Top - popupSize.Height - gap, popupSize.Width, popupSize.Height)
        };

        return Clamp(candidate, workingArea, gap);
    }

    public static TaskbarEdge GetTaskbarEdge(Rectangle taskbar, Rectangle workingArea)
    {
        if (taskbar.Width >= taskbar.Height)
            return taskbar.Bottom <= workingArea.Top + 16 ? TaskbarEdge.Top : TaskbarEdge.Bottom;

        return taskbar.Right <= workingArea.Left + 16 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private static Rectangle Clamp(Rectangle candidate, Rectangle workingArea, int gap)
    {
        var minLeft = workingArea.Left + gap;
        var minTop = workingArea.Top + gap;
        var maxLeft = Math.Max(minLeft, workingArea.Right - candidate.Width - gap);
        var maxTop = Math.Max(minTop, workingArea.Bottom - candidate.Height - gap);
        return new Rectangle(
            Math.Clamp(candidate.Left, minLeft, maxLeft),
            Math.Clamp(candidate.Top, minTop, maxTop),
            candidate.Width,
            candidate.Height);
    }
}
