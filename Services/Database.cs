using System.Text.Json;
using System.Text.Json.Serialization;
using PodereBot.Lib.Common;

namespace PodereBot.Services;

public class DatabaseSchema
{
    [JsonPropertyName("GatesOpenAccessExpirationDate")]
    public DateTime? GatesOpenAccessExpirationDate { get; set; } = null;

    [JsonPropertyName("HeatingProgram")]
    public HeatingProgram? HeatingProgram { get; set; }

    [JsonIgnore]
    public bool BoilerActive { get; set; }

    [JsonIgnore]
    public bool ManualHeatingActive { get; set; }

    [JsonPropertyName("GateAccessUsers")]
    public List<long> GateAccessUsers { get; set; } = [];

    public DatabaseSchema Clone() =>
        new()
        {
            GatesOpenAccessExpirationDate = GatesOpenAccessExpirationDate,
            HeatingProgram = HeatingProgram,
            BoilerActive = BoilerActive,
            ManualHeatingActive = ManualHeatingActive,
            GateAccessUsers = [.. GateAccessUsers],
        };
}

internal class Database
{
    private static readonly string DB_PATH = Path.Join(AppContext.BaseDirectory, "db.json");
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    private readonly ILogger<Database> logger;
    private readonly object sync = new();

    private readonly DatabaseSchema data;
    public DatabaseSchema Data => data.Clone();

    public Database(ILogger<Database> logger)
    {
        this.logger = logger;
        if (File.Exists(DB_PATH))
        {
            try
            {
                data = JsonSerializer.Deserialize<DatabaseSchema>(File.ReadAllText(DB_PATH), jsonOptions) ?? new DatabaseSchema();
                logger.LogDebug("local db found and loaded");
            }
            catch (Exception ex)
            {
                logger.LogError("unable to read {p}, starting from an empty database: {e}", DB_PATH, ex);
                data = new DatabaseSchema();
            }
        }
        else
        {
            data = new DatabaseSchema();
            logger.LogDebug("local db not found");
        }
    }

    private void Save()
    {
        var tmpPath = $"{DB_PATH}.tmp";
        try
        {
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(data, jsonOptions) + "\n");
            File.Move(tmpPath, DB_PATH, true);
            logger.LogTrace("local db serialized > {p}", DB_PATH);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to write {p}: {e}", DB_PATH, ex);
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
    }

    public void Edit(Action<DatabaseSchema> modifier)
    {
        lock (sync)
        {
            modifier?.Invoke(data);
            Save();
        }
    }

    /// <summary>Checks that the directory holding the database accepts writes.</summary>
    public bool TryProbeWritable()
    {
        var directory = Path.GetDirectoryName(DB_PATH) ?? AppContext.BaseDirectory;
        var probe = Path.Join(directory, ".db-probe");
        try
        {
            File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("database write probe failed: {e}", ex);
            return false;
        }
    }
}
