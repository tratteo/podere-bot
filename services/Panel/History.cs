namespace PodereBot.Services.Panel;

internal sealed record HistoryPoint(DateTime At, double Avg, double Min, double Max, int Samples);

internal sealed record HistorySeriesPoint(DateTime At, double Value);

internal sealed record HistorySeries(string Id, string Label, IReadOnlyList<HistorySeriesPoint> Points);

/// <summary>Seconds the boiler ran inside one bucket, hour or day.</summary>
internal sealed record HistoryBoilerPoint(DateTime At, double Seconds);

internal sealed record HistoryResult(
    string Range,
    string Bucket,
    DateTime From,
    DateTime To,
    IReadOnlyList<HistoryPoint> Points,
    IReadOnlyList<HistorySeries> Series,
    IReadOnlyList<HistoryBoilerPoint> Boiler
);

/// <summary>
/// Turns hourly samples into the buckets the chart draws. Hourly up to a week, daily beyond,
/// grouped by the local calendar so the days follow the Pi time zone and its DST. The axis spans
/// the whole requested range, but only the buckets that hold samples carry a temperature: the
/// line is cut where data is missing instead of dropping to zero.
/// </summary>
internal static class TemperatureHistory
{
    public const int MaxDays = 365;

    public static readonly IReadOnlyList<string> Ranges = ["today", "7d", "30d", "1y", "custom"];

    /// <summary>One bucket of the axis: the local time it starts at and the key its rows carry.</summary>
    private sealed record Bucket(DateTime At, long Key);

    public static (DateTime From, DateTime To) Resolve(string? range, DateTime? from, DateTime? to, DateTime now)
    {
        var key = string.IsNullOrWhiteSpace(range) ? "today" : range.Trim().ToLowerInvariant();
        var nowLocal = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;

        switch (key)
        {
            case "today":
                return (nowLocal.Date, nowLocal);
            case "7d":
                return (nowLocal.Date.AddDays(-6), nowLocal);
            case "30d":
                return (nowLocal.Date.AddDays(-29), nowLocal);
            case "1y":
                return (nowLocal.Date.AddDays(-(MaxDays - 1)), nowLocal);
            case "custom":
                if (from == null || to == null)
                    throw new PanelValidationException("Indica la data di inizio e di fine.");
                var start = from.Value;
                var end = to.Value;
                if (end <= start)
                    throw new PanelValidationException("La data di fine deve essere successiva a quella di inizio.");
                if (end > nowLocal.AddMinutes(1))
                    throw new PanelValidationException("Non puoi scegliere un intervallo nel futuro.");
                if ((end - start).TotalDays > MaxDays)
                    throw new PanelValidationException("L'intervallo massimo è di un anno.");
                return (Align(start, end), end);
            default:
                throw new PanelValidationException("Intervallo non riconosciuto.");
        }
    }

    /// <summary>Snaps the start to its bucket so the first day or hour is never charted only in part.</summary>
    private static DateTime Align(DateTime from, DateTime to) =>
        Hourly(from, to) ? FloorToLocalHour(from) : from.Date;

    private static DateTime FloorToLocalHour(DateTime local)
    {
        var utc = local.Kind == DateTimeKind.Utc ? local : DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc).ToLocalTime();
    }

    public static HistoryResult Build(
        IEnumerable<TemperatureBucket> rows,
        string range,
        DateTime fromLocal,
        DateTime toLocal,
        TimeZoneInfo? zone = null
    ) => Build(rows, [], [], range, fromLocal, toLocal, zone);

    public static HistoryResult Build(
        IEnumerable<TemperatureBucket> rows,
        IEnumerable<SensorBucket> sensors,
        string range,
        DateTime fromLocal,
        DateTime toLocal,
        TimeZoneInfo? zone = null
    ) => Build(rows, sensors, [], range, fromLocal, toLocal, zone);

    public static HistoryResult Build(
        IEnumerable<TemperatureBucket> rows,
        IEnumerable<SensorBucket> sensors,
        IEnumerable<BoilerBucket> boiler,
        string range,
        DateTime fromLocal,
        DateTime toLocal,
        TimeZoneInfo? zone = null
    )
    {
        zone ??= TimeZoneInfo.Local;
        var hourly = Hourly(fromLocal, toLocal);
        var buckets = Buckets(fromLocal, toLocal, hourly, zone);

        return new HistoryResult(
            range,
            hourly ? "hour" : "day",
            fromLocal,
            toLocal,
            BuildPoints([.. rows], buckets, hourly, zone),
            BuildSeries([.. sensors], buckets, hourly, zone),
            BuildBoiler([.. boiler], buckets, hourly, zone)
        );
    }

    private static bool Hourly(DateTime fromLocal, DateTime toLocal) => (toLocal - fromLocal).TotalDays <= 7;

    /// <summary>Every bucket between the two bounds, in order, whether or not it holds samples.</summary>
    private static List<Bucket> Buckets(DateTime fromLocal, DateTime toLocal, bool hourly, TimeZoneInfo zone)
    {
        var result = new List<Bucket>();

        if (hourly)
        {
            var last = ToUtcHour(toLocal, zone);
            for (var utc = ToUtcHour(fromLocal, zone); utc <= last; utc = utc.AddHours(1))
                result.Add(new Bucket(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), new DateTimeOffset(utc).ToUnixTimeSeconds()));
        }
        else
        {
            for (var day = fromLocal.Date; day <= toLocal.Date; day = day.AddDays(1))
                result.Add(new Bucket(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), day.Ticks));
        }

        return result;
    }

    /// <summary>The UTC hour that contains a local time, matching the hour_utc stored by the recorder.</summary>
    private static DateTime ToUtcHour(DateTime local, TimeZoneInfo zone)
    {
        var utc = local.Kind switch
        {
            DateTimeKind.Utc => local,
            DateTimeKind.Local => local.ToUniversalTime(),
            _ => TimeZoneInfo.ConvertTimeToUtc(local, zone),
        };
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
    }

    private static long BucketKey(long hourUtc, DateTime atUtc, bool hourly, TimeZoneInfo zone) =>
        hourly ? hourUtc : TimeZoneInfo.ConvertTimeFromUtc(atUtc, zone).Date.Ticks;

    /// <summary>Only the buckets that hold samples: an empty one is a cut in the line, never a zero.</summary>
    private static List<HistoryPoint> BuildPoints(
        List<TemperatureBucket> rows,
        List<Bucket> buckets,
        bool hourly,
        TimeZoneInfo zone
    )
    {
        var at = buckets.ToDictionary(b => b.Key, b => b.At);
        return rows.GroupBy(r => BucketKey(r.HourUtc, r.AtUtc, hourly, zone))
            .Where(g => at.ContainsKey(g.Key))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var samples = g.Sum(r => r.Samples);
                var weighted = samples > 0 ? g.Sum(r => r.Avg * r.Samples) / samples : g.Average(r => r.Avg);
                return new HistoryPoint(
                    at[g.Key],
                    Round(weighted),
                    Round(g.Min(r => r.Min)),
                    Round(g.Max(r => r.Max)),
                    samples
                );
            })
            .ToList();
    }

    /// <summary>Sensor series stay sparse: a missing reading is a gap, not a value of zero.</summary>
    private static List<HistorySeries> BuildSeries(
        List<SensorBucket> rows,
        List<Bucket> buckets,
        bool hourly,
        TimeZoneInfo zone
    )
    {
        var at = buckets.ToDictionary(b => b.Key, b => b.At);
        var result = new List<HistorySeries>();
        foreach (var group in rows.GroupBy(r => r.Id))
        {
            var ordered = group.OrderBy(r => r.HourUtc).ToList();
            var points = group
                .GroupBy(r => BucketKey(r.HourUtc, r.AtUtc, hourly, zone))
                .Where(g => at.ContainsKey(g.Key))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var samples = g.Sum(r => r.Samples);
                    var weighted = samples > 0 ? g.Sum(r => r.Avg * r.Samples) / samples : g.Average(r => r.Avg);
                    return new HistorySeriesPoint(at[g.Key], Round(weighted));
                })
                .ToList();
            if (points.Count == 0)
                continue;
            result.Add(new HistorySeries(group.Key, ordered[^1].Label, points));
        }
        return Order(result);
    }

    private static List<HistoryBoilerPoint> BuildBoiler(
        List<BoilerBucket> rows,
        List<Bucket> buckets,
        bool hourly,
        TimeZoneInfo zone
    )
    {
        var seconds = rows
            .GroupBy(r => BucketKey(r.HourUtc, r.AtUtc, hourly, zone))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Seconds));
        return buckets
            .Select(b => new HistoryBoilerPoint(b.At, Math.Round(seconds.TryGetValue(b.Key, out var value) ? value : 0)))
            .ToList();
    }

    /// <summary>Local probe first, then the external sensors by label.</summary>
    private static List<HistorySeries> Order(IEnumerable<HistorySeries> series) =>
        series.OrderBy(s => s.Id == "local" ? 0 : 1)
            .ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
