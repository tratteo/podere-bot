using System.Globalization;
using System.Text.Json.Nodes;

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
        return (uncachedInput * current.Input + cachedInputTokens * current.Cached + outputTokens * current.Output)
            / 1_000_000d
            * factor;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var json = JsonNode.Parse(await http.GetStringAsync(modelsUri));
            var models = json?["data"] as JsonArray;
            var model = models?.FirstOrDefault(m => ReadString(m?["id"]) == modelId);
            if (model == null)
            {
                logger.LogWarning("model {m} not found in the pricing list", modelId);
                return;
            }

            var cost = model["cost"];
            var text = cost?["text"];
            var input = ReadDouble(text?["input"]?["price"]) ?? fallbackInput;
            var cached = ReadDouble(text?["input"]?["cache"]?["read"]) ?? fallbackCached;
            var output = ReadDouble(text?["output"]?["price"]) ?? fallbackOutput;
            var multipliers = cost?["multipliers"] as JsonArray;

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

    private static double ApplyMultipliers(JsonArray? multipliers, DateTime utcNow)
    {
        if (multipliers == null)
            return 1d;
        var factor = 1d;
        foreach (var item in multipliers)
        {
            if (item is not JsonObject rule)
                continue;
            if (rule["when"] is not JsonObject when || !Matches(when, utcNow))
                continue;
            if (ReadDouble(rule["factor"]) is double ruleFactor)
                factor *= ruleFactor;
        }
        return factor;
    }

    private static bool Matches(JsonObject condition, DateTime utcNow)
    {
        foreach (var (name, value) in condition)
        {
            if (!MatchesProperty(name, value, utcNow))
                return false;
        }
        return true;
    }

    private static bool MatchesProperty(string name, JsonNode? value, DateTime utcNow)
    {
        switch (name)
        {
            case "utc_days":
                if (value?["in"] is not JsonArray days)
                    return true;
                var day = utcNow.ToString("ddd", CultureInfo.InvariantCulture).ToLowerInvariant();
                return days.Any(d => string.Equals(ReadString(d), day, StringComparison.OrdinalIgnoreCase));
            case "utc_hours":
                var gte = ParseTime(ReadString(value?["gte"]));
                var lt = ParseTime(ReadString(value?["lt"]));
                if (gte != null && utcNow.TimeOfDay < gte)
                    return false;
                if (lt != null && utcNow.TimeOfDay >= lt)
                    return false;
                return true;
            case "any":
                return value is JsonArray any && any.Any(item => item is JsonObject o && Matches(o, utcNow));
            case "all":
                return value is JsonArray all && all.All(item => item is JsonObject o && Matches(o, utcNow));
            case "not":
                return value is JsonObject not && !Matches(not, utcNow);
            default:
                return true;
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) ? text : null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.TryGetValue<double>(out var number) ? number : null;
    }

    private static TimeSpan? ParseTime(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed class PriceSnapshot(double input, double cached, double output, JsonArray? multipliers)
    {
        public double Input { get; } = input;
        public double Cached { get; } = cached;
        public double Output { get; } = output;
        public JsonArray? Multipliers { get; } = multipliers;
    }
}
