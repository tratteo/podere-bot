using System.Text.Json;
using System.Text.Json.Serialization;

namespace PodereBot.Services;

/// <summary>Entitlements grant capabilities. <see cref="Admin"/> implies every other entitlement.</summary>
internal static class Entitlement
{
    public const string Admin = "admin";
    public const string GateController = "gate_controller";

    public static readonly IReadOnlyList<string> All = [Admin, GateController];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                return known;
        }
        return null;
    }

    public static List<string> NormalizeAll(IEnumerable<string>? values)
    {
        if (values == null)
            return [];
        var result = new List<string>();
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized != null && !result.Contains(normalized))
                result.Add(normalized);
        }
        return result;
    }
}

/// <summary>Notification topics a user can subscribe to. Subscriptions are explicit for everyone.</summary>
internal static class NotificationTopic
{
    public const string Technical = "technical";
    public const string Heating = "heating";
    public const string Gates = "gates";

    public static readonly IReadOnlyList<string> All = [Technical, Heating, Gates];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                return known;
        }
        return null;
    }

    public static List<string> NormalizeAll(IEnumerable<string>? values)
    {
        if (values == null)
            return [];
        var result = new List<string>();
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized != null && !result.Contains(normalized))
                result.Add(normalized);
        }
        return result;
    }
}

internal sealed class UserEntry
{
    [JsonPropertyName("Id")]
    public long Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Entitlements")]
    public List<string> Entitlements { get; set; } = [];

    [JsonPropertyName("Topics")]
    public List<string> Topics { get; set; } = [];

    [JsonIgnore]
    public bool IsAdmin => HasEntitlement(Entitlement.Admin);

    [JsonIgnore]
    public bool CanControlGates => IsAdmin || HasEntitlement(Entitlement.GateController);

    public bool HasEntitlement(string entitlement) =>
        Entitlements.Contains(entitlement, StringComparer.OrdinalIgnoreCase) || HasAdminOverride(entitlement);

    public bool IsSubscribed(string topic) =>
        Topics.Contains(topic, StringComparer.OrdinalIgnoreCase);

    private bool HasAdminOverride(string entitlement) =>
        !string.Equals(entitlement, Entitlement.Admin, StringComparison.OrdinalIgnoreCase) &&
        Entitlements.Contains(Entitlement.Admin, StringComparer.OrdinalIgnoreCase);

    public UserEntry Normalized() =>
        new()
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            Entitlements = Entitlement.NormalizeAll(Entitlements),
            Topics = NotificationTopic.NormalizeAll(Topics),
        };

    public UserEntry Clone() => new() { Id = Id, Name = Name, Entitlements = [.. Entitlements], Topics = [.. Topics] };
}

internal sealed class UsersFile
{
    /// <summary>Bumped when the file semantics change; 0 means written before topics were explicit.</summary>
    public const int CurrentVersion = 2;

    [JsonPropertyName("Version")]
    public int Version { get; set; }

    [JsonPropertyName("Users")]
    public List<UserEntry> Users { get; set; } = [];

    [JsonPropertyName("Removed")]
    public List<long> Removed { get; set; } = [];
}

/// <summary>
/// Resolves the effective user set: the versioned seed in appsettings.json merged with the
/// gitignored runtime override, minus explicitly removed ids. Runtime entries win over the seed.
/// </summary>
internal sealed class UserDirectory
{
    public const string RuntimeFileName = "users.json";
    public const string LegacyFileName = "admins.json";

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<UserDirectory> logger;
    private readonly string runtimePath;
    private readonly string legacyPath;
    private readonly object sync = new();
    private readonly Dictionary<long, UserEntry> seed = [];
    private Dictionary<long, UserEntry> runtime = [];
    private HashSet<long> removed = [];
    private DateTime lastWriteUtc = DateTime.MinValue;

    public UserDirectory(IConfiguration configuration, IHostEnvironment environment, ILogger<UserDirectory> logger)
    {
        this.logger = logger;
        runtimePath = Path.Join(environment.ContentRootPath, RuntimeFileName);
        legacyPath = Path.Join(environment.ContentRootPath, LegacyFileName);

        foreach (var entry in configuration.GetSection("Users").Get<UserEntry[]>() ?? [])
        {
            if (entry.Id <= 0)
                continue;
            seed[entry.Id] = entry.Normalized();
        }

        RuntimeFile.Migrate(RuntimeFileName, environment.ContentRootPath, AppContext.BaseDirectory, logger);

        Reload();
        MigrateLegacyFile();
    }

    public UserEntry? Find(long id)
    {
        EnsureFresh();
        lock (sync)
        {
            if (removed.Contains(id))
                return null;
            if (runtime.TryGetValue(id, out var runtimeEntry))
                return runtimeEntry.Clone();
            if (seed.TryGetValue(id, out var seedEntry))
                return seedEntry.Clone();
            return null;
        }
    }

    public bool IsAdmin(long id) => Find(id)?.IsAdmin == true;

    public bool CanControlGates(long id) => Find(id)?.CanControlGates == true;

    public bool IsSubscribed(long id, string topic) => Find(id)?.IsSubscribed(topic) == true;

    public IReadOnlyList<UserEntry> All()
    {
        EnsureFresh();
        lock (sync)
        {
            var result = new List<UserEntry>();
            foreach (var (id, entry) in seed)
            {
                if (removed.Contains(id))
                    continue;
                result.Add(runtime.TryGetValue(id, out var over) ? over.Clone() : entry.Clone());
            }
            foreach (var (id, entry) in runtime)
            {
                if (seed.ContainsKey(id) || removed.Contains(id))
                    continue;
                result.Add(entry.Clone());
            }
            return result.OrderBy(u => u.Id).ToList();
        }
    }

    public long[] AdminIds() => All().Where(u => u.IsAdmin).Select(u => u.Id).ToArray();

    public long[] Subscribers(string topic) => All().Where(u => u.IsSubscribed(topic)).Select(u => u.Id).ToArray();

    public void Upsert(UserEntry entry)
    {
        if (entry.Id <= 0)
            throw new ArgumentException("user id must be positive", nameof(entry));

        EnsureFresh();
        lock (sync)
        {
            runtime[entry.Id] = entry.Normalized();
            removed.Remove(entry.Id);
            SaveLocked();
        }
    }

    public bool Remove(long id)
    {
        EnsureFresh();
        lock (sync)
        {
            if (!runtime.Remove(id) && !seed.ContainsKey(id))
                return false;
            if (seed.ContainsKey(id))
                removed.Add(id);
            SaveLocked();
            return true;
        }
    }

    /// <summary>True when the id is only overridden at runtime, i.e. it is not part of the versioned seed.</summary>
    public bool IsRuntimeOnly(long id)
    {
        lock (sync)
            return runtime.ContainsKey(id) && !seed.ContainsKey(id);
    }

    private void EnsureFresh()
    {
        if (!File.Exists(runtimePath))
            return;

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(runtimePath);
        }
        catch (Exception)
        {
            return;
        }

        if (stamp == lastWriteUtc)
            return;

        Reload();
    }

    private void Reload()
    {
        lock (sync)
        {
            try
            {
                if (!File.Exists(runtimePath))
                {
                    runtime = [];
                    removed = [];
                    lastWriteUtc = DateTime.MinValue;
                    return;
                }

                var text = File.ReadAllText(runtimePath);
                var file = JsonSerializer.Deserialize<UsersFile>(text, jsonOptions) ?? new UsersFile();
                var loaded = new Dictionary<long, UserEntry>();
                foreach (var entry in file.Users)
                {
                    if (entry.Id <= 0)
                        continue;
                    loaded[entry.Id] = entry.Normalized();
                }
                runtime = loaded;
                removed = [.. file.Removed.Where(id => id > 0)];

                // Before version 2 an admin implicitly received every topic, so an empty list
                // meant "all". Materialise it once: from now on an empty list means "none".
                if (file.Version < UsersFile.CurrentVersion)
                {
                    foreach (var admin in runtime.Values.Where(u => u.IsAdmin))
                        admin.Topics = [.. NotificationTopic.All];
                    SaveLocked();
                }
                else
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(runtimePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogError("unable to read {p}: {e}", runtimePath, ex);
            }
        }
    }

    private void SaveLocked()
    {
        var file = new UsersFile
        {
            Version = UsersFile.CurrentVersion,
            Users = [.. runtime.Values.OrderBy(u => u.Id).Select(u => u.Clone())],
            Removed = [.. removed.OrderBy(id => id)],
        };

        try
        {
            var tmpPath = $"{runtimePath}.tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(file, jsonOptions) + "\n");
            File.Move(tmpPath, runtimePath, true);
            lastWriteUtc = File.GetLastWriteTimeUtc(runtimePath);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to write {p}: {e}", runtimePath, ex);
            throw;
        }
    }

    private void MigrateLegacyFile()
    {
        if (File.Exists(runtimePath) || !File.Exists(legacyPath))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var root = document.RootElement;

            var migrated = new Dictionary<long, UserEntry>();
            foreach (var id in ReadIds(root, "RuntimeAdmins"))
            {
                migrated[id] = new UserEntry { Id = id, Entitlements = [Entitlement.Admin], Topics = [.. NotificationTopic.All] };
            }

            var migratedRemoved = ReadIds(root, "RemovedAdmins").ToHashSet();

            var names = new Dictionary<long, string>();
            if (
                root.TryGetProperty("AdminNames", out var namesElement)
                && namesElement.ValueKind == JsonValueKind.Object
            )
            {
                foreach (var property in namesElement.EnumerateObject())
                {
                    if (long.TryParse(property.Name, out var id) && property.Value.ValueKind == JsonValueKind.String)
                        names[id] = property.Value.GetString() ?? string.Empty;
                }
            }

            foreach (var (id, name) in names)
            {
                if (migrated.TryGetValue(id, out var entry) && !string.IsNullOrWhiteSpace(name))
                    entry.Name = name.Trim();
            }

            lock (sync)
            {
                runtime = migrated;
                removed = migratedRemoved;
                SaveLocked();
            }

            File.Delete(legacyPath);
            logger.LogInformation(
                "migrated {u} runtime admins and {r} removals from {f} into {n}",
                migrated.Count,
                migratedRemoved.Count,
                LegacyFileName,
                RuntimeFileName
            );
        }
        catch (Exception ex)
        {
            logger.LogError("unable to migrate {p}: {e}", legacyPath, ex);
        }
    }

    private static IEnumerable<long> ReadIds(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id) && id > 0)
                yield return id;
        }
    }
}
