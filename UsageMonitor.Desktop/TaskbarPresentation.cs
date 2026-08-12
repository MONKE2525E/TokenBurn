using UsageMonitor.Core;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Desktop;

/// <summary>Presentation rules that are specific to the compact taskbar surface.</summary>
internal static class TaskbarMetricFilter
{
    public static bool IsConfigured(UsageSnapshotData snapshot)
    {
        if (snapshot.Lines.Count == 0) return false;
        var hasErrorBadge = snapshot.Lines.Any(line =>
            line is BadgeMetricData badge &&
            badge.Label.Equals(MetricLine.ErrorBadgeLabel, StringComparison.OrdinalIgnoreCase));
        // A failed refresh that carried over the last good bars (CoreUsageSnapshotSource appends
        // an error badge) must stay visible: dropping it makes providers vanish from the taskbar
        // until a clean refresh. A plain error envelope with no error badge is a placeholder.
        if (!string.IsNullOrWhiteSpace(snapshot.Error) && !hasErrorBadge) return false;
        return snapshot.Lines
            .Where(line => !(line is BadgeMetricData badge &&
                             badge.Label.Equals(MetricLine.ErrorBadgeLabel, StringComparison.OrdinalIgnoreCase)))
            .Any(line => line switch
            {
                ProgressMetricData progress => double.IsFinite(progress.Used) &&
                                               double.IsFinite(progress.Limit) &&
                                               progress.Limit > 0,
                TextMetricData text => HasValue(text.Value),
                BadgeMetricData badge => HasValue(badge.Text),
                ValuesMetricData values => values.Values.Count > 0,
                BarChartMetricData chart => chart.Points.Count > 0,
                _ => false
            });
    }

    private static bool HasValue(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           !value.Equals("not configured", StringComparison.OrdinalIgnoreCase) &&
           !value.Equals("unavailable", StringComparison.OrdinalIgnoreCase) &&
           !value.Equals("no data", StringComparison.OrdinalIgnoreCase);
}

internal static class MetricVisibility
{
    public static IReadOnlyList<MetricDisplay> SelectPinned(
        IEnumerable<MetricDisplay> metrics,
        Func<MetricDisplay, bool> isPinned,
        int maximum = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(isPinned);
        return metrics.Where(isPinned).Take(Math.Max(0, maximum)).ToArray();
    }
}

internal readonly record struct TaskbarLayoutDecision(double AvailableWidthDip)
{
    // The overlay controller asks for the width budget only; the renderer applies its own
    // compaction once it knows the budget.
    public static TaskbarLayoutDecision Calculate(
        double taskbarLengthDip,
        double reservedEdgeDip = 180,
        double sideMarginDip = 8)
    {
        var available = Math.Max(40, taskbarLengthDip - reservedEdgeDip - sideMarginDip);
        return new TaskbarLayoutDecision(available);
    }
}

internal readonly record struct TaskbarStripPlacementResult(
    System.Drawing.Rectangle Bounds,
    double EdgeOffsetDip,
    bool ResetPersistedOffset)
{
    public bool IsSane(System.Drawing.Rectangle taskbar)
        => Bounds.Width >= 40 && Bounds.Height >= 26 &&
           Bounds.IntersectsWith(taskbar) && taskbar.Contains(Bounds);
}

internal static class TaskbarStripPlacement
{
    private const double DefaultEdgeOffsetDip = 180;
    private const double SafeMarginDip = 180;
    private const double GapDip = 8;

    public static double CalculateReservedEdgeDip(
        System.Drawing.Rectangle taskbar,
        System.Drawing.Rectangle trayNotify,
        double dpi)
    {
        var scale = double.IsFinite(dpi) && dpi > 0 ? dpi / 96.0 : 1;
        if (taskbar.Width <= 0 || taskbar.Height <= 0 || trayNotify.Width <= 0 || trayNotify.Height <= 0)
            return SafeMarginDip;

        var vertical = taskbar.Width < taskbar.Height;
        var trayEdgePixels = vertical
            ? Math.Max(0, taskbar.Bottom - trayNotify.Top)
            : Math.Max(0, taskbar.Right - trayNotify.Left);
        var gapPixels = Math.Max(1, (int)Math.Ceiling(GapDip * scale));
        return Math.Max(SafeMarginDip, (trayEdgePixels + gapPixels) / scale);
    }

    public static TaskbarStripPlacementResult Calculate(
        System.Drawing.Rectangle taskbar,
        double idealWidthDip,
        double availableWidthDip,
        double persistedEdgeOffsetDip,
        double dpi,
        double reservedEdgeDip = SafeMarginDip)
    {
        if (taskbar.Width <= 0 || taskbar.Height <= 0)
            return new TaskbarStripPlacementResult(System.Drawing.Rectangle.Empty, DefaultEdgeOffsetDip, true);

        var scale = double.IsFinite(dpi) && dpi > 0 ? dpi / 96.0 : 1;
        var vertical = taskbar.Width < taskbar.Height;
        var mainAxisPixels = vertical ? taskbar.Height : taskbar.Width;
        var requestedPixels = Math.Max(40, (int)Math.Ceiling(Math.Max(40, idealWidthDip) * scale));
        var availablePixels = Math.Max(40, (int)Math.Round(Math.Max(40, availableWidthDip) * scale));
        var dimension = Math.Min(requestedPixels, availablePixels);
        var trailingMarginPixels = Math.Clamp((int)Math.Ceiling(Math.Max(4, reservedEdgeDip) * scale),
            4, Math.Max(4, mainAxisPixels / 3));
        var leadingMarginPixels = Math.Max(4, (int)Math.Ceiling(4 * scale));
        var maxOffsetPixels = Math.Max(trailingMarginPixels, mainAxisPixels - dimension - leadingMarginPixels);
        var persistedPixels = double.IsFinite(persistedEdgeOffsetDip)
            ? (int)Math.Round(persistedEdgeOffsetDip * scale)
            : int.MinValue;
        var legacyLeadingMarginPixels = Math.Clamp(
            (int)Math.Ceiling(Math.Max(SafeMarginDip, reservedEdgeDip) * scale),
            4, Math.Max(4, mainAxisPixels / 3));
        var legacyMaxOffsetPixels = Math.Max(trailingMarginPixels,
            mainAxisPixels - dimension - legacyLeadingMarginPixels);
        var migratedLegacyLeadingClamp = persistedPixels == legacyMaxOffsetPixels &&
            maxOffsetPixels > legacyMaxOffsetPixels;
        var reset = persistedPixels < trailingMarginPixels || persistedPixels > maxOffsetPixels ||
            migratedLegacyLeadingClamp;
        var edgeOffsetPixels = migratedLegacyLeadingClamp
            ? maxOffsetPixels
            : reset
            ? double.IsFinite(persistedEdgeOffsetDip)
                // The widget width can change after a refresh. Preserve the user's chosen
                // side instead of jumping to the default trailing-edge position when the old
                // offset no longer fits the newly sized widget.
                ? Math.Clamp(persistedPixels, trailingMarginPixels, maxOffsetPixels)
                : Math.Clamp((int)Math.Round(DefaultEdgeOffsetDip * scale), trailingMarginPixels, maxOffsetPixels)
            : persistedPixels;

        var crossAxisSize = Math.Max(26, vertical ? taskbar.Width : taskbar.Height);
        var x = vertical
            ? taskbar.Left + Math.Max(0, (taskbar.Width - crossAxisSize) / 2)
            : taskbar.Right - dimension - edgeOffsetPixels;
        var y = vertical
            ? taskbar.Bottom - dimension - edgeOffsetPixels
            : taskbar.Top + Math.Max(0, (taskbar.Height - crossAxisSize) / 2);
        var width = vertical ? Math.Min(taskbar.Width, crossAxisSize) : dimension;
        var height = vertical ? dimension : crossAxisSize;
        var bounds = new System.Drawing.Rectangle(x, y, width, height);
        return new TaskbarStripPlacementResult(bounds, edgeOffsetPixels / scale, reset);
    }

    public static double CalculateDraggedEdgeOffset(
        System.Drawing.Rectangle taskbar,
        System.Drawing.Rectangle widgetAtDragStart,
        double deltaXDip,
        double deltaYDip,
        double dpi,
        double trailingMarginDip = SafeMarginDip,
        double leadingMarginDip = 4)
    {
        if (taskbar.Width <= 0 || taskbar.Height <= 0 || widgetAtDragStart.Width <= 0 || widgetAtDragStart.Height <= 0)
            return TaskbarWidgetPlacement.Default.EdgeOffsetDip;

        var scale = double.IsFinite(dpi) && dpi > 0 ? dpi / 96.0 : 1;
        var vertical = taskbar.Width < taskbar.Height;
        var deltaPixels = (int)Math.Round((vertical ? deltaYDip : deltaXDip) * scale);
        var mainAxisPixels = vertical ? taskbar.Height : taskbar.Width;
        var widgetPixels = vertical ? widgetAtDragStart.Height : widgetAtDragStart.Width;
        var trailingMarginPixels = Math.Max(4, (int)Math.Round(Math.Max(4, trailingMarginDip) * scale));
        var leadingMarginPixels = Math.Max(4, (int)Math.Round(Math.Max(4, leadingMarginDip) * scale));
        var maxOffsetPixels = Math.Max(trailingMarginPixels,
            mainAxisPixels - widgetPixels - leadingMarginPixels);
        var proposedPixels = vertical
            ? taskbar.Bottom - (widgetAtDragStart.Bottom + deltaPixels)
            : taskbar.Right - (widgetAtDragStart.Right + deltaPixels);
        return Math.Clamp(proposedPixels, trailingMarginPixels, maxOffsetPixels) / scale;
    }
}
