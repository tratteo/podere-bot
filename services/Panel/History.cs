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
/// grouped by the local calendar so the days follow the Pi time zone and its DST.
/// </summary>
internal static class TemperatureHistory
{
    public const int MaxDays = 365;

    public static readonly IReadOnlyList<string> Ranges = ["today", "7d", "30d", "1y", "custom"];

    public static (DateTime From, DateTime To) Resolve(string? range, DateTime? from, DateTime? to, DateTime now)
    {
        var key = string.IsNullOrWhiteSpace(range) ? "today" : range.Trim().ToLowerInvariant();
        var nowLocal = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;

        switch (key)
        {
            case "today":
                return (nowLocal.Date, nowLocal);
            case "7d":
                return (nowLocal.AddDays(-7), nowLocal);
            case "30d":
                return (nowLocal.AddDays(-30), nowLocal);
            case "1y":
                return (nowLocal.AddDays(-MaxDays), nowLocal);
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
                return (start, end);
            default:
                throw new PanelValidationException("Intervallo non riconosciuto.");
        }
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
        var hourly = (toLocal - fromLocal).TotalDays <= 7;
        var points = hourly
            ? BuildHourly(rows, zone)
            : BuildDaily(rows, zone);
        var series = hourly ? BuildSeriesHourly(sensors, zone) : BuildSeriesDaily(sensors, zone);
        var runtime = hourly ? BuildBoilerHourly(boiler, zone) : BuildBoilerDaily(boiler, zone);

        return new HistoryResult(range, hourly ? "hour" : "day", fromLocal, toLocal, points, series, runtime);
    }

    private static List<HistoryPoint> BuildHourly(IEnumerable<TemperatureBucket> rows, TimeZoneInfo zone) =>
        rows.OrderBy(r => r.HourUtc)
            .Select(r =>
                new HistoryPoint(
                    TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone),
                    Round(r.Avg),
                    Round(r.Min),
                    Round(r.Max),
                    r.Samples
                )
            )
            .ToList();

    private static List<HistoryPoint> BuildDaily(IEnumerable<TemperatureBucket> rows, TimeZoneInfo zone)
    {
        var result = new List<HistoryPoint>();
        foreach (var group in rows.GroupBy(r => TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone).Date).OrderBy(g => g.Key))
        {
            var samples = group.Sum(r => r.Samples);
            var weighted = samples > 0 ? group.Sum(r => r.Avg * r.Samples) / samples : group.Average(r => r.Avg);
            result.Add(
                new HistoryPoint(
                    DateTime.SpecifyKind(group.Key, DateTimeKind.Unspecified),
                    Round(weighted),
                    Round(group.Min(r => r.Min)),
                    Round(group.Max(r => r.Max)),
                    samples
                )
            );
        }
        return result;
    }

    private static List<HistorySeries> BuildSeriesHourly(IEnumerable<SensorBucket> rows, TimeZoneInfo zone) =>
        Order(
            rows.GroupBy(r => r.Id)
                .Select(group =>
                {
                    var ordered = group.OrderBy(r => r.HourUtc).ToList();
                    return new HistorySeries(
                        group.Key,
                        ordered[^1].Label,
                        ordered
                            .Select(r => new HistorySeriesPoint(TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone), Round(r.Avg)))
                            .ToList()
                    );
                })
        );

    private static List<HistorySeries> BuildSeriesDaily(IEnumerable<SensorBucket> rows, TimeZoneInfo zone)
    {
        var result = new List<HistorySeries>();
        foreach (var group in rows.GroupBy(r => r.Id))
        {
            var ordered = group.OrderBy(r => r.HourUtc).ToList();
            var points = group
                .GroupBy(r => TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone).Date)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var samples = g.Sum(r => r.Samples);
                    var weighted = samples > 0 ? g.Sum(r => r.Avg * r.Samples) / samples : g.Average(r => r.Avg);
                    return new HistorySeriesPoint(DateTime.SpecifyKind(g.Key, DateTimeKind.Unspecified), Round(weighted));
                })
                .ToList();
            result.Add(new HistorySeries(group.Key, ordered[^1].Label, points));
        }
        return Order(result);
    }

    /// <summary>Local probe first, then the external sensors by label.</summary>
    private static List<HistorySeries> Order(IEnumerable<HistorySeries> series) =>
        series.OrderBy(s => s.Id == "local" ? 0 : 1)
            .ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<HistoryBoilerPoint> BuildBoilerHourly(IEnumerable<BoilerBucket> rows, TimeZoneInfo zone) =>
        rows.OrderBy(r => r.HourUtc)
            .Select(r =>
                new HistoryBoilerPoint(TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone), Math.Round(r.Seconds))
            )
            .ToList();

    private static List<HistoryBoilerPoint> BuildBoilerDaily(IEnumerable<BoilerBucket> rows, TimeZoneInfo zone) =>
        rows.GroupBy(r => TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone).Date)
            .OrderBy(g => g.Key)
            .Select(g =>
                new HistoryBoilerPoint(DateTime.SpecifyKind(g.Key, DateTimeKind.Unspecified), Math.Round(g.Sum(r => r.Seconds)))
            )
            .ToList();

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
