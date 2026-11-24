namespace RbxSwitcher.Core.Finance;

/// <summary>The civil day used by the daily estimate: America/Sao_Paulo (Brasília time).</summary>
public static class BrasiliaTime
{
    public static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        foreach (string id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Brazil has had no daylight saving time since 2019.
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    public static DateTimeOffset ToBrasilia(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Midnight (start of the civil day) in Brasília containing <paramref name="instant"/>, as an instant.</summary>
    public static DateTimeOffset StartOfDay(DateTimeOffset instant)
    {
        var local = ToBrasilia(instant);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, Zone.GetUtcOffset(midnight));
    }

    public static DateTimeOffset NextMidnight(DateTimeOffset instant)
    {
        var local = ToBrasilia(instant);
        var next = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        return new DateTimeOffset(next, Zone.GetUtcOffset(next));
    }
}
