namespace EmailTriage.Core.Services;

/// <summary>
/// The hours mail is allowed to go out in, so a reply written late at night
/// waits for the morning instead of landing in someone's inbox at 11pm (#100).
///
/// Times are in the working-hours zone (US Eastern by default), not the PC's
/// own, so the rule holds when travelling. Weekends count as outside hours:
/// Friday evening's reply goes Monday morning.
/// </summary>
public sealed record WorkingHours
{
    /// <summary>Windows id, then the IANA one for machines that only know that.</summary>
    public const string EasternZoneId = "Eastern Standard Time";
    private const string EasternIana = "America/New_York";

    public TimeSpan Start { get; init; } = TimeSpan.FromHours(7);
    public TimeSpan End { get; init; } = TimeSpan.FromHours(18);
    public TimeZoneInfo Zone { get; init; } = Eastern;

    /// <summary>A short label for the zone, for status lines ("ET").</summary>
    public string ZoneLabel { get; init; } = "ET";

    public static TimeZoneInfo Eastern => FindZone(EasternZoneId);

    public static TimeZoneInfo FindZone(string id)
    {
        foreach (var candidate in new[] { id, id == EasternZoneId ? EasternIana : id })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>
    /// Null while <paramref name="now"/> is inside working hours, so the mail
    /// can go now; otherwise the next opening: today at the start hour before
    /// it, the next weekday's start hour after the end or at a weekend.
    /// </summary>
    public DateTimeOffset? NextOpening(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        var day = local.Date;
        var time = local.TimeOfDay;

        if (IsWorkday(day) && time >= Start && time < End) return null;

        // Before opening on a workday: today. Otherwise the next workday.
        if (!IsWorkday(day) || time >= End)
        {
            do day = day.AddDays(1); while (!IsWorkday(day));
        }

        var opening = DateTime.SpecifyKind(day + Start, DateTimeKind.Unspecified);
        return new DateTimeOffset(opening, Zone.GetUtcOffset(opening));
    }

    private static bool IsWorkday(DateTime day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
