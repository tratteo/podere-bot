using System.Text.Json;

namespace PodereBot.Services;

/// <summary>
/// Keeps startup notifications quiet until the boot summary has been sent, and suppresses a
/// second boot summary when the service is restarted shortly after the previous one.
/// </summary>
internal sealed class StartupGate
{
    private const string FileName = "startup.json";
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    private readonly ILogger<StartupGate> logger;
    private readonly string path;
    private volatile bool ready;

    public bool Ready => ready;
    public bool ShouldAnnounce { get; }

    public StartupGate(IHostEnvironment environment, ILogger<StartupGate> logger)
    {
        this.logger = logger;
        path = Path.Join(environment.ContentRootPath, FileName);
        var last = ReadLastAnnounce();
        ShouldAnnounce = last == null || DateTime.UtcNow - last > QuietWindow;
    }

    public void MarkReady()
    {
        ready = true;
        if (!ShouldAnnounce)
            return;

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new Marker { LastAnnounceUtc = DateTime.UtcNow }, jsonOptions) + "\n");
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to write startup marker: {m}", ex.Message);
        }
    }

    private DateTime? ReadLastAnnounce()
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path))?.LastAnnounceUtc;
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to read startup marker: {m}", ex.Message);
            return null;
        }
    }

    private sealed class Marker
    {
        public DateTime LastAnnounceUtc { get; set; }
    }
}
