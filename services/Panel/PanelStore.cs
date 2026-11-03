using Microsoft.Data.Sqlite;

namespace PodereBot.Services.Panel;

internal sealed record TemperatureBucket(long HourUtc, double Avg, double Min, double Max, int Samples)
{
    public DateTime AtUtc => DateTimeOffset.FromUnixTimeSeconds(HourUtc).UtcDateTime;
}

/// <summary>One sensor reading folded into the hourly history.</summary>
internal sealed record SensorSample(string Id, string Label, double Value);

internal sealed record SensorBucket(string Id, string Label, long HourUtc, double Avg, double Min, double Max, int Samples)
{
    public DateTime AtUtc => DateTimeOffset.FromUnixTimeSeconds(HourUtc).UtcDateTime;
}

internal sealed record PanelEvent(long Id, string Topic, string Title, string? Detail, DateTime At);

/// <summary>
/// SQLite store for the panel: hourly temperature history, the event log and the paired devices.
/// A missing or broken database must never take the bot down, so every write is guarded and
/// reported through <see cref="Available"/>.
/// </summary>
internal sealed class PanelStore
{
    public const string FileName = "panel.db";

    private static readonly object schemaLock = new();
    private readonly ILogger<PanelStore> logger;
    private readonly string path;
    private readonly object sync = new();

    public bool Available { get; private set; }
    public string Path => path;

    public PanelStore(IHostEnvironment environment, ILogger<PanelStore> logger)
        : this(environment.ContentRootPath, logger, AppContext.BaseDirectory) { }

    internal PanelStore(string contentRoot, ILogger<PanelStore> logger, string publishFolder)
    {
        this.logger = logger;
        path = System.IO.Path.Join(contentRoot, FileName);

        // Runtime state must survive the updater replacing the publish folder.
        RuntimeFile.Migrate(FileName, contentRoot, publishFolder, logger);
        Initialize();
    }

    /// <summary>Store pointing at an arbitrary file; used by the tests.</summary>
    internal PanelStore(string databasePath, ILogger<PanelStore> logger)
    {
        this.logger = logger;
        path = databasePath;
        Initialize();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
            }.ToString()
        );
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        lock (schemaLock)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS temperature_hourly (
                        hour_utc INTEGER PRIMARY KEY,
                        avg REAL NOT NULL,
                        min REAL NOT NULL,
                        max REAL NOT NULL,
                        samples INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS sensor_hourly (
                        hour_utc INTEGER NOT NULL,
                        sensor_id TEXT NOT NULL,
                        label TEXT NOT NULL,
                        avg REAL NOT NULL,
                        min REAL NOT NULL,
                        max REAL NOT NULL,
                        samples INTEGER NOT NULL,
                        PRIMARY KEY (hour_utc, sensor_id)
                    );
                    CREATE INDEX IF NOT EXISTS sensor_hourly_id ON sensor_hourly(sensor_id, hour_utc);
                    CREATE TABLE IF NOT EXISTS events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        topic TEXT NOT NULL,
                        title TEXT NOT NULL,
                        detail TEXT,
                        at_utc INTEGER NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS events_at ON events(at_utc DESC);
                    CREATE TABLE IF NOT EXISTS panel_devices (
                        id TEXT PRIMARY KEY,
                        name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        key_hash TEXT,
                        pairing_hash TEXT,
                        pairing_expires_at INTEGER,
                        created_at INTEGER NOT NULL,
                        created_by TEXT,
                        last_seen_at INTEGER,
                        revoked_at INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS devices_pairing ON panel_devices(pairing_hash);
                    DELETE FROM panel_devices WHERE revoked_at IS NOT NULL;
                    """;
                command.ExecuteNonQuery();
                Available = true;
            }
            catch (Exception ex)
            {
                Available = false;
                logger.LogError("panel database at {p} is not usable: {e}", path, ex);
            }
        }
    }

    /// <summary>Folds one sample into the bucket of the UTC hour it belongs to.</summary>
    public void RecordTemperature(DateTime timestampUtc, double value)
    {
        if (!Available)
            return;

        var hour = new DateTime(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, timestampUtc.Hour, 0, 0, DateTimeKind.Utc);
        var hourUtc = new DateTimeOffset(hour).ToUnixTimeSeconds();

        try
        {
            lock (sync)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO temperature_hourly(hour_utc, avg, min, max, samples)
                    VALUES($h, $t, $t, $t, 1)
                    ON CONFLICT(hour_utc) DO UPDATE SET
                        avg = (avg * samples + excluded.avg) / (samples + 1),
                        min = MIN(min, excluded.min),
                        max = MAX(max, excluded.max),
                        samples = samples + 1;
                    """;
                command.Parameters.AddWithValue("$h", hourUtc);
                command.Parameters.AddWithValue("$t", value);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to record temperature sample: {m}", ex.Message);
        }
    }

    /// <summary>Folds one sample per sensor into the bucket of the UTC hour it belongs to.</summary>
    public void RecordSensorTemperatures(DateTime timestampUtc, IReadOnlyList<SensorSample> samples)
    {
        if (!Available || samples.Count == 0)
            return;

        var hour = new DateTime(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, timestampUtc.Hour, 0, 0, DateTimeKind.Utc);
        var hourUtc = new DateTimeOffset(hour).ToUnixTimeSeconds();

        try
        {
            lock (sync)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO sensor_hourly(hour_utc, sensor_id, label, avg, min, max, samples)
                    VALUES($h, $id, $label, $t, $t, $t, 1)
                    ON CONFLICT(hour_utc, sensor_id) DO UPDATE SET
                        label = excluded.label,
                        avg = (avg * samples + excluded.avg) / (samples + 1),
                        min = MIN(min, excluded.min),
                        max = MAX(max, excluded.max),
                        samples = samples + 1;
                    """;

                foreach (var sample in samples)
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$h", hourUtc);
                    command.Parameters.AddWithValue("$id", Truncate(sample.Id, 64) ?? "");
                    command.Parameters.AddWithValue("$label", Truncate(sample.Label, 80) ?? "");
                    command.Parameters.AddWithValue("$t", sample.Value);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to record sensor samples: {m}", ex.Message);
        }
    }

    public IReadOnlyList<TemperatureBucket> Hourly(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<TemperatureBucket>();
        if (!Available)
            return result;

        try
        {
            lock (sync)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT hour_utc, avg, min, max, samples
                    FROM temperature_hourly
                    WHERE hour_utc >= $from AND hour_utc < $to
                    ORDER BY hour_utc;
                    """;
                command.Parameters.AddWithValue("$from", new DateTimeOffset(fromUtc.ToUniversalTime()).ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$to", new DateTimeOffset(toUtc.ToUniversalTime()).ToUnixTimeSeconds());
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(
                        new TemperatureBucket(
                            reader.GetInt64(0),
                            reader.GetDouble(1),
                            reader.GetDouble(2),
                            reader.GetDouble(3),
                            reader.GetInt32(4)
                        )
                    );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to read temperature history: {m}", ex.Message);
        }
        return result;
    }

    public IReadOnlyList<SensorBucket> SensorHourly(DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<SensorBucket>();
        if (!Available)
            return result;

        try
        {
            lock (sync)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT sensor_id, label, hour_utc, avg, min, max, samples
                    FROM sensor_hourly
                    WHERE hour_utc >= $from AND hour_utc < $to
                    ORDER BY sensor_id, hour_utc;
                    """;
                command.Parameters.AddWithValue("$from", new DateTimeOffset(fromUtc.ToUniversalTime()).ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$to", new DateTimeOffset(toUtc.ToUniversalTime()).ToUnixTimeSeconds());
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(
                        new SensorBucket(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetInt64(2),
                            reader.GetDouble(3),
                            reader.GetDouble(4),
                            reader.GetDouble(5),
                            reader.GetInt32(6)
                        )
                    );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to read sensor history: {m}", ex.Message);
        }
        return result;
    }

    public void AddEvent(string topic, string title, string? detail, DateTime atUtc)
    {
        if (!Available)
            return;

        try
        {
            lock (sync)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO events(topic, title, detail, at_utc)
                    VALUES($topic, $title, $detail, $at);
                    """;
                command.Parameters.AddWithValue("$topic", topic);
                command.Parameters.AddWithValue("$title", Truncate(title, 160));
                command.Parameters.AddWithValue("$detail", (object?)Truncate(detail, 600) ?? DBNull.Value);
                command.Parameters.AddWithValue("$at", new DateTimeOffset(atUtc.ToUniversalTime()).ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to record event: {m}", ex.Message);
        }
    }

    public IReadOnlyList<PanelEvent> Events(int limit, string? topic = null)
    {
        var result = new List<PanelEvent>();
        if (!Available)
            return result;

        try
        {
            lock (sync)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = topic == null
                    ? "SELECT id, topic, title, detail, at_utc FROM events ORDER BY at_utc DESC, id DESC LIMIT $limit;"
                    : "SELECT id, topic, title, detail, at_utc FROM events WHERE topic = $topic ORDER BY at_utc DESC, id DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
                if (topic != null)
                    command.Parameters.AddWithValue("$topic", topic);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(
                        new PanelEvent(
                            reader.GetInt64(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).LocalDateTime
                        )
                    );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to read events: {m}", ex.Message);
        }
        return result;
    }

    /// <summary>Drops history older than the retention window and trims the event log.</summary>
    public void Prune(int historyRetentionDays, int eventRetentionDays, int maxEvents)
    {
        if (!Available)
            return;

        try
        {
            lock (sync)
            {
                using var connection = Open();

                using (var history = connection.CreateCommand())
                {
                    history.CommandText = """
                    DELETE FROM temperature_hourly WHERE hour_utc < $cutoff;
                    DELETE FROM sensor_hourly WHERE hour_utc < $cutoff;
                    """;
                    history.Parameters.AddWithValue(
                        "$cutoff",
                        new DateTimeOffset(DateTime.UtcNow.AddDays(-Math.Max(1, historyRetentionDays))).ToUnixTimeSeconds()
                    );
                    history.ExecuteNonQuery();
                }

                using (var old = connection.CreateCommand())
                {
                    old.CommandText = "DELETE FROM events WHERE at_utc < $cutoff;";
                    old.Parameters.AddWithValue(
                        "$cutoff",
                        new DateTimeOffset(DateTime.UtcNow.AddDays(-Math.Max(1, eventRetentionDays))).ToUnixTimeSeconds()
                    );
                    old.ExecuteNonQuery();
                }

                using (var extra = connection.CreateCommand())
                {
                    extra.CommandText = """
                        DELETE FROM events WHERE id NOT IN (
                            SELECT id FROM events ORDER BY at_utc DESC, id DESC LIMIT $max
                        );
                        """;
                    extra.Parameters.AddWithValue("$max", Math.Max(1, maxEvents));
                    extra.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to prune the panel database: {m}", ex.Message);
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
