using System.Globalization;
using Newtonsoft.Json.Linq;

namespace PodereBot.Services;

internal sealed class ModelPricing
{
    private readonly ILogger<ModelPricing> logger;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly System.Timers.Timer refreshTimer = new(TimeSpan.FromHours(6));
    private readonly object sync = new();
    private readonly string modelId;
    private readonly Uri modelsUri;
    private readonly double fallbackInput;
    private readonly double fallbackCached;
    private readonly double fallbackOutput;
    private PriceSnapshot snapshot;

    public ModelPricing(IConfiguration configuration, ILogger<ModelPricing> logger)
    {
        this.logger = logger;
        modelId = configuration.GetSection("AI")["Model"] ?? "";
        var endpoint = configuration.GetSection("AI")["Endpoint"] ?? "https://opencode.ai/zen/go/v1/";
        modelsUri = new Uri($"{endpoint.TrimEnd('/')}/models");
        fallbackInput = configuration.GetValue<double?>("AI:InputPricePerMillion") ?? 0.15;
        fallbackCached = configuration.GetValue<double?>("AI:CachedInputPricePerMillion") ?? 0.003;
        fallbackOutput = configuration.GetValue<double?>("AI:OutputPricePerMillion") ?? 0.6;
        snapshot = new PriceSnapshot(fallbackInput, fallbackCached, fallbackOutput, null);

        refreshTimer.Elapsed += async (_, _) => await RefreshAsync();
        refreshTimer.Start();
        _ = RefreshAsync();
    }

    public double EstimateCost(long inputTokens, long cachedInputTokens, long outputTokens, DateTime utcNow)
    {
        PriceSnapshot current;
        lock (sync)
            current = snapshot;

        var factor = ApplyMultipliers(current.Multipliers, utcNow);
        var uncachedInput = Math.Max(0, inputTokens - cachedInputTokens);
        return (uncachedInput * current.Input + cachedInputTokens * current.Cached + outputTokens * current.Output) / 1_000_000d * factor;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync(modelsUri));
            var model = json["data"]?.FirstOrDefault(m => (string?)m["id"] == modelId);
            if (model == null)
            {
                logger.LogWarning("model {m} not found in the pricing list", modelId);
                return;
            }

            var cost = model["cost"];
            var text = cost?["text"];
            var input = text?["input"]?["price"]?.Value<double?>() ?? fallbackInput;
            var cached = text?["input"]?["cache"]?["read"]?.Value<double?>() ?? fallbackCached;
            var output = text?["output"]?["price"]?.Value<double?>() ?? fallbackOutput;
            var multipliers = cost?["multipliers"] as JArray;

            lock (sync)
                snapshot = new PriceSnapshot(input, cached, output, multipliers);
            logger.LogInformation(
                "pricing for {m}: input {i}/M, cached {c}/M, output {o}/M",
                modelId,
                input,
                cached,
                output
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to fetch pricing for {m}: {e}", modelId, ex.Message);
        }
    }

    private static double ApplyMultipliers(JArray? multipliers, DateTime utcNow)
    {
        if (multipliers == null)
            return 1d;
        var factor = 1d;
        foreach (var item in multipliers)
        {
            if (item is not JObject rule)
                continue;
            if (rule["when"] is not JObject when || !Matches(when, utcNow))
                continue;
            if (rule["factor"]?.Value<double?>() is double ruleFactor)
                factor *= ruleFactor;
        }
        return factor;
    }

    private static bool Matches(JObject condition, DateTime utcNow)
    {
        foreach (var property in condition.Properties())
        {
            if (!MatchesProperty(property.Name, property.Value, utcNow))
                return false;
        }
        return true;
    }

    private static bool MatchesProperty(string name, JToken value, DateTime utcNow)
    {
        switch (name)
        {
            case "utc_days":
                if (value["in"] is not JArray days)
                    return true;
                var day = utcNow.ToString("ddd", CultureInfo.InvariantCulture).ToLowerInvariant();
                return days.Any(d => string.Equals((string?)d, day, StringComparison.OrdinalIgnoreCase));
            case "utc_hours":
                var gte = ParseTime((string?)value["gte"]);
                var lt = ParseTime((string?)value["lt"]);
                if (gte != null && utcNow.TimeOfDay < gte)
                    return false;
                if (lt != null && utcNow.TimeOfDay >= lt)
                    return false;
                return true;
            case "any":
                return value is JArray any && any.Any(item => item is JObject o && Matches(o, utcNow));
            case "all":
                return value is JArray all && all.All(item => item is JObject o && Matches(o, utcNow));
            case "not":
                return value is JObject not && !Matches(not, utcNow);
            default:
                return true;
        }
    }

    private static TimeSpan? ParseTime(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed class PriceSnapshot(double input, double cached, double output, JArray? multipliers)
    {
        public double Input { get; } = input;
        public double Cached { get; } = cached;
        public double Output { get; } = output;
        public JArray? Multipliers { get; } = multipliers;
    }
}
