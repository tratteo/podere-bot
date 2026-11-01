using System.Text.Json;
using Newtonsoft.Json;
using PodereBot.Lib.Common;

namespace PodereBot.Services;

public class DatabaseSchema
{
    [JsonProperty]
    public DateTime? GatesOpenAccessExpirationDate { get; set; } = null;

    [JsonProperty]
    public HeatingProgram? HeatingProgram { get; set; }

    [JsonIgnore]
    public bool BoilerActive { get; set; }

    [JsonIgnore]
    public bool ManualHeatingActive { get; set; }

    [JsonProperty]
    public List<long> TradingAlertsSubscriptions { get; set; } = [];

    public DatabaseSchema Clone() =>
        new()
        {
            GatesOpenAccessExpirationDate = GatesOpenAccessExpirationDate,
            HeatingProgram = HeatingProgram,
            BoilerActive = BoilerActive,
            ManualHeatingActive = ManualHeatingActive,
            TradingAlertsSubscriptions = TradingAlertsSubscriptions
        };
}

internal class Database
{
    private static readonly string DB_PATH = Path.Join(AppContext.BaseDirectory, "db.json");
    private readonly ILogger<Database> logger;

    private readonly DatabaseSchema data;
    public DatabaseSchema Data => data.Clone();

    public Database(ILogger<Database> logger)
    {
        this.logger = logger;
        if (File.Exists(DB_PATH))
        {
            data = JsonConvert.DeserializeObject<DatabaseSchema>(File.ReadAllText(DB_PATH))!;
            logger.LogDebug("local db found and loaded");
        }
        else
        {
            data = new DatabaseSchema();
            logger.LogDebug("local db not found");
        }
    }

    private void Save()
    {
        File.WriteAllTextAsync(DB_PATH, JsonConvert.SerializeObject(data));
        logger.LogTrace("local db serialized > {p}", DB_PATH);
    }

    public void Edit(Action<DatabaseSchema> modifier)
    {
        modifier?.Invoke(data);

        Save();
    }
}
