using System.Globalization;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;

namespace PodereBot.Services;

internal class UsageEntry
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long Calls { get; set; }
    public double CostUsd { get; set; }
}

internal class UsageSummary
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long Calls { get; set; }
    public double CostUsd { get; set; }
}

internal class UsageTracker
{
    private static readonly object fileLock = new();
    private readonly ILogger<UsageTracker> logger;
    private readonly ModelPricing pricing;
    private readonly string path;
    private readonly Dictionary<string, UsageEntry> days = [];

    public UsageTracker(IHostEnvironment environment, ILogger<UsageTracker> logger, ModelPricing pricing)
    {
        this.logger = logger;
        this.pricing = pricing;
        path = Path.Join(environment.ContentRootPath, "usage.json");
        Load();
    }

    public void Record(ChatResponse response)
    {
        var usage = response.Usage;
        if (usage == null)
            return;
        Record(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, usage.CachedInputTokenCount ?? 0);
    }

    public void Record(long inputTokens, long outputTokens, long cachedInputTokens)
    {
        var key = DateKey(DateTime.Now);
        var cost = pricing.EstimateCost(inputTokens, cachedInputTokens, outputTokens, DateTime.UtcNow);
        lock (fileLock)
        {
            if (!days.TryGetValue(key, out var entry))
            {
                entry = new UsageEntry();
                days[key] = entry;
            }
            entry.InputTokens += inputTokens;
            entry.OutputTokens += outputTokens;
            entry.CachedInputTokens += cachedInputTokens;
            entry.Calls++;
            entry.CostUsd += cost;
            Prune();
            Save();
        }
    }

    public UsageSummary GetLastDays(int days)
    {
        var cutoff = DateKey(DateTime.Now.Date.AddDays(-(days - 1)));
        var summary = new UsageSummary();
        lock (fileLock)
        {
            foreach (var (key, entry) in this.days)
            {
                if (string.CompareOrdinal(key, cutoff) < 0)
                    continue;
                summary.InputTokens += entry.InputTokens;
                summary.OutputTokens += entry.OutputTokens;
                summary.CachedInputTokens += entry.CachedInputTokens;
                summary.Calls += entry.Calls;
                summary.CostUsd += entry.CostUsd;
            }
        }
        return summary;
    }

    private static string DateKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Prune()
    {
        var cutoff = DateKey(DateTime.Now.Date.AddDays(-60));
        foreach (var key in days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            days.Remove(key);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
                return;
            var data = JsonConvert.DeserializeObject<Dictionary<string, UsageEntry>>(File.ReadAllText(path));
            if (data == null)
                return;
            foreach (var (key, entry) in data)
                days[key] = entry;
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to load usage ledger: {m}", ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonConvert.SerializeObject(days, Formatting.Indented).Replace("\r\n", "\n") + "\n";
            var tmpPath = $"{path}.tmp";
            try
            {
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, path, true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to save usage ledger: {m}", ex.Message);
        }
    }
}
