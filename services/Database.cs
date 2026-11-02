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

    /// <summary>When the boiler was last switched on, so the panel can show how long it has run.</summary>
    [JsonPropertyName("BoilerSinceUtc")]
    public DateTime? BoilerSinceUtc { get; set; }

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
            BoilerSinceUtc = BoilerSinceUtc,
            BoilerActive = BoilerActive,
            ManualHeatingActive = ManualHeatingActive,
            GateAccessUsers = [.. GateAccessUsers],
        };
}

internal class Database
{
    public const string FileName = "db.json";

    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    private readonly ILogger<Database> logger;
    private readonly object sync = new();
    private readonly string dbPath;

    private readonly DatabaseSchema data;
    public DatabaseSchema Data => data.Clone();

    /// <summary>The path the database is stored at; exposed so the updater and tests can reason about it.</summary>
    public string Path => dbPath;

    public Database(IHostEnvironment environment, ILogger<Database> logger)
        : this(environment, logger, AppContext.BaseDirectory) { }

    internal Database(IHostEnvironment environment, ILogger<Database> logger, string publishFolder)
    {
        this.logger = logger;
        dbPath = System.IO.Path.Join(environment.ContentRootPath, FileName);

        // Runtime state must survive the updater replacing the publish folder.
        RuntimeFile.Migrate(FileName, environment.ContentRootPath, publishFolder, logger);

        data = Load();
    }

    private DatabaseSchema Load()
    {
        if (!File.Exists(dbPath))
        {
            logger.LogDebug("local db not found at {p}", dbPath);
            return new DatabaseSchema();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<DatabaseSchema>(File.ReadAllText(dbPath), jsonOptions);
            logger.LogDebug("local db found and loaded from {p}", dbPath);
            return loaded ?? new DatabaseSchema();
        }
        catch (Exception ex)
        {
            logger.LogError("unable to read {p}, starting from an empty database: {e}", dbPath, ex);
            return new DatabaseSchema();
        }
    }

    private void Save()
    {
        var tmpPath = $"{dbPath}.tmp";
        try
        {
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(data, jsonOptions) + "\n");
            File.Move(tmpPath, dbPath, true);
            logger.LogTrace("local db serialized > {p}", dbPath);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to write {p}: {e}", dbPath, ex);
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
        var directory = System.IO.Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        var probe = System.IO.Path.Join(directory, ".db-probe");
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
