namespace SpreadTrader.Engine;

/// <summary>
/// The IST timezone this app schedules everything against, resolved once. Every scheduled job here
/// (daily maintenance, instrument-master refresh, history capture) works off the exchange's wall
/// clock rather than the host's, so the same build behaves identically on a Windows box in IST, a
/// Linux container in UTC, or an Azure region anywhere else.
///
/// Kept as a shared helper because the lookup has a platform wrinkle: Windows knows the zone as
/// "India Standard Time", Linux/Docker as the IANA id "Asia/Kolkata", and a stripped-down container
/// image may ship no timezone database at all - hence the fixed-offset fallback, which is exact
/// (IST is UTC+05:30 year-round with no DST).
///
/// The existing hosted services each still carry their own private copy of this lookup; this is the
/// one new code should use.
/// </summary>
public static class IstClock
{
    /// <summary>India Standard Time (UTC+05:30, no DST).</summary>
    public static TimeZoneInfo TimeZone { get; } = Resolve();

    /// <summary>Now, on the exchange's wall clock, regardless of the host's own timezone.</summary>
    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);

    /// <summary>Converts a UTC instant to the exchange's wall clock.</summary>
    public static DateTimeOffset ToIst(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, TimeZone);

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // IST is a fixed UTC+05:30 with no DST, so a hand-built zone is exact.
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "IST");
    }
}
