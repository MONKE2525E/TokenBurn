namespace UsageMonitor.Core;

public enum PacingStatus
{
    Unknown,
    OnTrack,
    Ahead,
    Behind,
    Exhausted
}

public sealed record PacingResult(
    double UsedFraction,
    double ExpectedFraction,
    double DeltaFraction,
    PacingStatus Status,
    DateTimeOffset? EstimatedDepletion,
    TimeSpan? RemainingPeriod)
{
    public bool IsAtRisk => Status is PacingStatus.Behind or PacingStatus.Exhausted;
}

public static class PacingCalculator
{
    public static PacingResult Calculate(double used, double limit, DateTimeOffset periodStart,
        DateTimeOffset resetAt, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        if (limit <= 0 || resetAt <= periodStart || instant < periodStart)
        {
            return new PacingResult(0, 0, 0, PacingStatus.Unknown, null,
                resetAt > instant ? resetAt - instant : null);
        }

        var duration = resetAt - periodStart;
        var elapsed = instant - periodStart;
        var expected = UsageMath.Clamp(elapsed.TotalSeconds / duration.TotalSeconds);
        var actual = UsageMath.Clamp(used / limit);
        var delta = actual - expected;
        var status = actual >= 1 ? PacingStatus.Exhausted
            : delta > 0.1 ? PacingStatus.Behind
            : delta < -0.1 ? PacingStatus.Ahead
            : PacingStatus.OnTrack;
        DateTimeOffset? depletion = null;
        if (used > 0 && used < limit && elapsed > TimeSpan.Zero)
        {
            var unitsPerSecond = used / elapsed.TotalSeconds;
            if (unitsPerSecond > 0)
            {
                var seconds = (limit - used) / unitsPerSecond;
                depletion = instant.AddSeconds(seconds);
            }
        }
        return new PacingResult(actual, expected, delta, status,
            depletion, resetAt > instant ? resetAt - instant : TimeSpan.Zero);
    }

    public static double ProjectedUsageAtReset(double used, DateTimeOffset periodStart,
        DateTimeOffset resetAt, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        var elapsed = (instant - periodStart).TotalSeconds;
        var total = (resetAt - periodStart).TotalSeconds;
        if (used <= 0 || elapsed <= 0 || total <= 0) return 0;
        return used * total / elapsed;
    }
}

public static class ResetCalculator
{
    public static TimeSpan Remaining(DateTimeOffset? resetsAt, DateTimeOffset? now = null)
    {
        if (resetsAt is null) return TimeSpan.Zero;
        var remaining = resetsAt.Value - (now ?? DateTimeOffset.UtcNow);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static string FormatRemaining(DateTimeOffset? resetsAt, DateTimeOffset? now = null)
    {
        if (resetsAt is null) return "No reset scheduled";
        var remaining = Remaining(resetsAt, now);
        if (remaining <= TimeSpan.Zero) return "Resetting now";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
    }

    public static string FormatExact(DateTimeOffset? resetsAt, TimeZoneInfo? timeZone = null)
    {
        if (resetsAt is null) return "No reset scheduled";
        var zone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(resetsAt.Value, zone);
        return local.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }
}
