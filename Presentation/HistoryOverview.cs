using System.Globalization;
using NetTrace.Models;
using NetTrace.Utils;

namespace NetTrace.Presentation;

public sealed record RecentSessionPresentation(long Id, string ProfileName, string NetworkName,
    string TimeRange, string DownloadText, string UploadText, bool IsActive);

public sealed record HistoryOverview(int TodaySessionCount, TimeSpan TodayConnectedTime,
    string TodayConnectedTimeText, long TodayDownloadBytes, long TodayUploadBytes,
    string TodayDownloadText, string TodayUploadText, IReadOnlyList<RecentSessionPresentation> RecentSessions)
{
    // Stored totals have no per-day traffic breakdown. Attribute all session bytes to
    // its local start day, including live counters; never estimate a midnight split.
    public static HistoryOverview Create(IReadOnlyList<ConnectionSession> todaySessions,
        IReadOnlyList<ConnectionSession> recentSessions, ConnectionSession? activeSession,
        DateTimeOffset nowUtc, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(todaySessions);
        ArgumentNullException.ThrowIfNull(recentSessions);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        var (dayStart, dayEnd) = GetLocalDayBounds(nowUtc, localTimeZone);
        var today = Merge(todaySessions, activeSession);
        var count = 0;
        var connected = TimeSpan.Zero;
        long download = 0, upload = 0;
        foreach (var session in today.Values)
        {
            var isActive = activeSession is { EndedAtUtc: null } && session.Id == activeSession.Id;
            var end = isActive ? nowUtc : session.EndedAtUtc ?? session.LastConfirmedAtUtc;
            if (end < session.StartedAtUtc) end = session.StartedAtUtc;
            if (end > nowUtc) end = nowUtc;
            if (session.StartedAtUtc >= dayEnd || session.StartedAtUtc > nowUtc ||
                end < dayStart || (end == dayStart && session.StartedAtUtc < dayStart)) continue;

            count++;
            var start = session.StartedAtUtc > dayStart ? session.StartedAtUtc : dayStart;
            if (end > dayEnd) end = dayEnd;
            if (end > start) connected += end - start;
            if (session.StartedAtUtc >= dayStart)
            {
                download = AddBytes(download, session.DownloadBytes);
                upload = AddBytes(upload, session.UploadBytes);
            }
        }

        var recent = Merge(recentSessions, activeSession).Values
            .OrderByDescending(session => session.StartedAtUtc).ThenByDescending(session => session.Id)
            .Take(4).Select(session => PresentSession(session,
                activeSession is { EndedAtUtc: null } && session.Id == activeSession.Id,
                nowUtc, localTimeZone)).ToArray();
        return new(count, connected, FormatDuration(connected), download, upload,
            ByteFormatter.Format(download), ByteFormatter.Format(upload), Array.AsReadOnly(recent));
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetLocalDayBounds(
        DateTimeOffset nowUtc, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        var localDate = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone).Date;
        return GetLocalDayBounds(DateOnly.FromDateTime(localDate), localTimeZone);
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetLocalDayBounds(
        DateOnly localDate, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        return (DayBoundary(localDate.ToDateTime(TimeOnly.MinValue), localTimeZone),
            DayBoundary(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), localTimeZone));
    }

    private static DateTimeOffset DayBoundary(DateTime localDate, TimeZoneInfo zone)
    {
        // Some zones change clocks at midnight. Use the first existing instant of
        // that date and the earliest occurrence if midnight is repeated.
        var boundary = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(boundary)) boundary = boundary.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(boundary)
            ? zone.GetAmbiguousTimeOffsets(boundary).Max() : zone.GetUtcOffset(boundary);
        return new DateTimeOffset(boundary, offset).ToUniversalTime();
    }

    private static Dictionary<long, ConnectionSession> Merge(IReadOnlyList<ConnectionSession> sessions,
        ConnectionSession? activeSession)
    {
        var result = new Dictionary<long, ConnectionSession>();
        foreach (var session in sessions) result.TryAdd(session.Id, session);
        if (activeSession is not null)
        {
            // The join reflects current profile edits/deletion even if the live
            // tracker has not yet received its recognition callback. Live timing
            // and counters still replace the older persisted progress.
            result[activeSession.Id] = result.TryGetValue(activeSession.Id, out var persisted)
                ? activeSession with { ProfileId = persisted.ProfileId, ProfileDisplayName = persisted.ProfileDisplayName }
                : activeSession;
        }
        return result;
    }

    private static RecentSessionPresentation PresentSession(ConnectionSession session, bool isActive,
        DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(session.StartedAtUtc, zone);
        var end = TimeZoneInfo.ConvertTime(session.EndedAtUtc ?? session.LastConfirmedAtUtc, zone);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var startText = start.ToString(start.Date == today ? "h:mm tt" : "MMM d, h:mm tt", CultureInfo.InvariantCulture);
        var endText = isActive ? "Active" : end.ToString(end.Date == start.Date ? "h:mm tt" : "MMM d, h:mm tt", CultureInfo.InvariantCulture);
        if (!isActive && session.EndedAtUtc is null) endText = "Last confirmed " + endText;
        return new(session.Id, string.IsNullOrWhiteSpace(session.ProfileDisplayName) ? "Unassigned" : session.ProfileDisplayName,
            session.NetworkDisplayName, startText + " – " + endText,
            ByteFormatter.Format(session.DownloadBytes), ByteFormatter.Format(session.UploadBytes), isActive);
    }

    private static string FormatDuration(TimeSpan value)
    {
        var seconds = (long)Math.Max(0, value.TotalSeconds);
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
        if (seconds >= 60) return seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds}s";
    }

    private static long AddBytes(long total, long value)
    {
        value = Math.Max(0, value);
        return total > long.MaxValue - value ? long.MaxValue : total + value;
    }
}
