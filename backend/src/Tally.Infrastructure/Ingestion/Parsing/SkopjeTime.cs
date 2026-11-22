namespace Tally.Infrastructure.Ingestion.Parsing;

/// <summary>
/// Chains publish North Macedonian local times. The database stores UTC (DATA-MODEL.md §1).
/// </summary>
internal static class SkopjeTime
{
    // IANA id. Resolves on Windows too (.NET 6+ maps IANA ids via ICU).
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Skopje");

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        // A wall-clock time inside the spring-forward gap does not exist; nudge past it.
        if (Zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);
}
