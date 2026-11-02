using System.Globalization;
using System.Text.Json.Nodes;

namespace PodereBot.Services.Panel;

/// <summary>Outdoor conditions at the house. Times are local to the house, as Open-Meteo returns them.</summary>
internal sealed record WeatherSnapshot(
    double Temperature,
    int Code,
    bool IsDay,
    int? Humidity,
    double? WindKmh,
    double? Min,
    double? Max,
    DateTime? Sunrise,
    DateTime? Sunset,
    DateTime UpdatedAt
);

/// <summary>
/// Polls Open-Meteo (free, no key) for the house coordinates and keeps the last good reading.
/// A failed refresh keeps the previous snapshot, so the panel shows its age instead of nothing.
/// </summary>
internal sealed class WeatherService : IDisposable
{
    private readonly ILogger<WeatherService> logger;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly System.Timers.Timer refreshTimer;
    private readonly Uri uri;
    private readonly bool enabled;
    private readonly TaskCompletionSource<WeatherSnapshot?> firstAttempt = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private WeatherSnapshot? current;

    public WeatherService(IConfiguration configuration, ILogger<WeatherService> logger)
    {
        this.logger = logger;
        var home = OptionsReader.ReadHome(configuration);
        var options = OptionsReader.ReadWeather(configuration);
        enabled = options.Enabled;
        uri = new Uri(
            "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={home.Latitude.ToString(CultureInfo.InvariantCulture)}"
                + $"&longitude={home.Longitude.ToString(CultureInfo.InvariantCulture)}"
                + "&current=temperature_2m,weather_code,relative_humidity_2m,wind_speed_10m,is_day"
                + "&daily=temperature_2m_min,temperature_2m_max,sunrise,sunset"
                + "&timezone=auto&forecast_days=1"
        );

        refreshTimer = new System.Timers.Timer(TimeSpan.FromMinutes(options.RefreshMinutes));
        if (!enabled)
        {
            firstAttempt.TrySetResult(null);
            return;
        }
        refreshTimer.Elapsed += async (_, _) => await RefreshAsync();
        refreshTimer.Start();
        _ = RefreshAsync();
    }

    public WeatherSnapshot? Current => Volatile.Read(ref current);

    /// <summary>
    /// The last good snapshot, waiting briefly for the first fetch when the service just started.
    /// Null when the weather is disabled or the first attempt failed.
    /// </summary>
    public async Task<WeatherSnapshot?> GetAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Current is WeatherSnapshot snapshot)
            return snapshot;
        if (firstAttempt.Task.IsCompleted)
            return firstAttempt.Task.Result;

        await Task.WhenAny(firstAttempt.Task, Task.Delay(timeout, cancellationToken));
        return Current;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = Parse(await http.GetStringAsync(uri), DateTime.Now);
            if (snapshot == null)
            {
                logger.LogWarning("weather response without current conditions");
                firstAttempt.TrySetResult(null);
                return;
            }
            Volatile.Write(ref current, snapshot);
            firstAttempt.TrySetResult(snapshot);
            logger.LogDebug("weather: {t} °C, code {c}", snapshot.Temperature, snapshot.Code);
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to fetch the weather: {e}", ex.Message);
            firstAttempt.TrySetResult(null);
        }
    }

    /// <summary>Reads the Open-Meteo forecast payload; null when the current block is missing.</summary>
    public static WeatherSnapshot? Parse(string json, DateTime now)
    {
        var root = JsonNode.Parse(json);
        var currentNode = root?["current"];
        if (ReadDouble(currentNode?["temperature_2m"]) is not double temperature)
            return null;

        var daily = root?["daily"];
        return new WeatherSnapshot(
            temperature,
            (int)(ReadDouble(currentNode?["weather_code"]) ?? 0),
            (ReadDouble(currentNode?["is_day"]) ?? 1) > 0,
            ReadDouble(currentNode?["relative_humidity_2m"]) is double humidity ? (int)Math.Round(humidity) : null,
            ReadDouble(currentNode?["wind_speed_10m"]),
            ReadDouble(daily?["temperature_2m_min"]?[0]),
            ReadDouble(daily?["temperature_2m_max"]?[0]),
            ReadTime(daily?["sunrise"]?[0]),
            ReadTime(daily?["sunset"]?[0]),
            now
        );
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.TryGetValue<double>(out var number) ? number : null;
    }

    private static DateTime? ReadTime(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : null;
    }

    public void Dispose()
    {
        refreshTimer.Dispose();
        http.Dispose();
    }
}
