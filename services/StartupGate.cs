using System.Text.Json;

namespace PodereBot.Services;

/// <summary>
/// Keeps startup notifications quiet until the boot summary has been sent, and suppresses a
/// second boot summary when the service is restarted shortly after the previous one.
/// The Raspberry Pi has no RTC: at boot the clock can be behind the last announce, so a
/// timestamp in the future means "announce", not "stay quiet for hours".
/// </summary>
internal sealed class StartupGate
{
    private const string FileName = "startup.json";
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    private readonly ILogger<StartupGate> logger;
    private readonly string path;
    private volatile bool ready;
    private DateTime? lastAnnounceUtc;

    public bool Ready => ready;

    public bool ShouldAnnounce
    {
        get
        {
            var last = lastAnnounceUtc;
            if (last == null)
                return true;

            var now = DateTime.UtcNow;
            return now < last.Value || now - last.Value > QuietWindow;
        }
    }

    public StartupGate(IHostEnvironment environment, ILogger<StartupGate> logger)
    {
        this.logger = logger;
        path = Path.Join(environment.ContentRootPath, FileName);
        lastAnnounceUtc = ReadLastAnnounce();
    }

    /// <summary>Marks the monitor ready even when Telegram is still unreachable.</summary>
    public void MarkReady() => ready = true;

    /// <summary>Called only after the summary was actually sent.</summary>
    public void MarkAnnounced()
    {
        lastAnnounceUtc = DateTime.UtcNow;
        try
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new Marker { LastAnnounceUtc = lastAnnounceUtc.Value }, jsonOptions) + "\n"
            );
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
