using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PodereBot.Services;

internal class OneWireEmbeddedTemperatureDriver(ILogger<OneWireEmbeddedTemperatureDriver> logger, IConfiguration configuration)
    : ITemperatureDriver
{
    private static readonly Regex SensorDirectoryPattern = new(@"^28-[0-9a-f]+$", RegexOptions.IgnoreCase);
    private static readonly Regex TemperaturePattern = new(@"t=(-?\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex OverlayPattern = new(@"^\s*dtoverlay=w1-gpio", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly string[] KernelLogPaths = ["/var/log/kern.log", "/var/log/messages"];
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private const int DmesgTimeoutMs = 2000;
    private const int KernelHintMaxLength = 96;

    private readonly ILogger logger = logger;
    private readonly string oneWireDevicesRoot = configuration.GetValue<string>("OneWire:DevicesRoot") ?? "/sys/bus/w1/devices";
    private readonly string configTxtPath = configuration.GetValue<string>("OneWire:ConfigTxt") ?? "/boot/firmware/config.txt";
    private readonly Dictionary<string, TemperatureReading> readings = [];
    private readonly object probeLock = new();

    private DateTime? lastSuccessUtc;
    private int consecutiveFailures;
    private bool? cachedOverlay;
    private DateTime overlayExpiry = DateTime.MinValue;
    private string? cachedKernelHint;
    private DateTime kernelHintExpiry = DateTime.MinValue;

    public void PostTemperatureReading(TemperatureReading reading)
    {
        readings[reading.Id] = reading;
    }

    public async Task<float?> GetLocalTemperature(CancellationToken? token = null) => (await ProbeLocalTemperature(token)).Temperature;

    public async Task<LocalTemperatureProbe> ProbeLocalTemperature(CancellationToken? token = null)
    {
        if (!Directory.Exists(oneWireDevicesRoot))
        {
            logger.LogWarning("unable to locate one wire devices root: {r}", oneWireDevicesRoot);
            return Complete(
                new LocalTemperatureProbe
                {
                    Status = LocalTemperatureStatus.DevicesRootMissing,
                    DevicesRootExists = false,
                    OverlayConfigured = ReadOverlayConfigured(),
                    Error = $"{oneWireDevicesRoot} assente",
                }
            );
        }

        var directories = Directory.GetDirectories(oneWireDevicesRoot);
        var busMasterPresent = directories.Any(d => Path.GetFileName(d).StartsWith("w1_bus_master", StringComparison.OrdinalIgnoreCase));
        var sensors = directories.Where(d => SensorDirectoryPattern.IsMatch(Path.GetFileName(d))).ToList();
        var sensorNames = sensors.ConvertAll(d => Path.GetFileName(d));

        if (!busMasterPresent)
        {
            logger.LogWarning("one wire bus master not present");
            return Complete(
                new LocalTemperatureProbe
                {
                    Status = LocalTemperatureStatus.BusMasterMissing,
                    DevicesRootExists = true,
                    OverlayConfigured = ReadOverlayConfigured(),
                    Sensors = sensorNames,
                    Error = "nessun w1_bus_master",
                }
            );
        }

        if (sensors.Count == 0)
        {
            logger.LogWarning("no one wire sensors found");
            return Complete(
                new LocalTemperatureProbe
                {
                    Status = LocalTemperatureStatus.NoSensors,
                    DevicesRootExists = true,
                    BusMasterPresent = true,
                    OverlayConfigured = ReadOverlayConfigured(),
                }
            );
        }

        var sensorFile = Path.Join(sensors[0], "w1_slave");
        string source;
        try
        {
            source = await File.ReadAllTextAsync(sensorFile, token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to read {p}: {m}", sensorFile, ex.Message);
            return Complete(
                new LocalTemperatureProbe
                {
                    Status = LocalTemperatureStatus.ReadError,
                    DevicesRootExists = true,
                    BusMasterPresent = true,
                    OverlayConfigured = ReadOverlayConfigured(),
                    Sensors = sensorNames,
                    Error = ex.Message,
                }
            );
        }

        var crcValid = source.Split('\n')[0].TrimEnd('\r').EndsWith("YES", StringComparison.OrdinalIgnoreCase);
        var match = TemperaturePattern.Match(source);
        if (!match.Success || !float.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            logger.LogWarning("error parsing w1_slave file");
            return Complete(
                new LocalTemperatureProbe
                {
                    Status = LocalTemperatureStatus.ParseError,
                    DevicesRootExists = true,
                    BusMasterPresent = true,
                    OverlayConfigured = ReadOverlayConfigured(),
                    Sensors = sensorNames,
                    CrcValid = crcValid,
                    Error = Truncate(source),
                }
            );
        }

        if (!crcValid)
        {
            logger.LogWarning("crc error reading {s}", sensorNames[0]);
            return Complete(
                new LocalTemperatureProbe
                {
                    Status = LocalTemperatureStatus.CrcError,
                    DevicesRootExists = true,
                    BusMasterPresent = true,
                    OverlayConfigured = ReadOverlayConfigured(),
                    Sensors = sensorNames,
                    CrcValid = false,
                }
            );
        }

        return Complete(
            new LocalTemperatureProbe
            {
                Status = LocalTemperatureStatus.Ok,
                Temperature = raw / 1000f,
                DevicesRootExists = true,
                BusMasterPresent = true,
                OverlayConfigured = ReadOverlayConfigured(),
                Sensors = sensorNames,
                CrcValid = true,
            }
        );
    }

    public List<TemperatureReading> GetExternalTemperatureReadings(TimeSpan? tolerance = null)
    {
        if (tolerance == null)
        {
            return [.. readings.Values];
        }
        return readings.Values.Where(r => DateTime.UtcNow.Subtract(r.Timestamp).Duration() < tolerance).ToList();
    }

    private LocalTemperatureProbe Complete(LocalTemperatureProbe probe)
    {
        var hint = probe.Status == LocalTemperatureStatus.Ok ? null : TryReadKernelHint();
        DateTime? last;
        int failures;
        lock (probeLock)
        {
            if (probe.Status == LocalTemperatureStatus.Ok)
            {
                lastSuccessUtc = DateTime.UtcNow;
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
            }

            last = lastSuccessUtc;
            failures = consecutiveFailures;
        }

        return probe with
        {
            LastSuccessUtc = last,
            ConsecutiveFailures = failures,
            KernelHint = hint,
        };
    }

    private bool? ReadOverlayConfigured()
    {
        lock (probeLock)
        {
            if (DateTime.UtcNow < overlayExpiry)
                return cachedOverlay;
        }

        bool? value = null;
        try
        {
            if (File.Exists(configTxtPath))
                value = OverlayPattern.IsMatch(File.ReadAllText(configTxtPath));
        }
        catch
        {
            value = null;
        }

        lock (probeLock)
        {
            cachedOverlay = value;
            overlayExpiry = DateTime.UtcNow.Add(CacheLifetime);
        }
        return value;
    }

    private string? TryReadKernelHint()
    {
        lock (probeLock)
        {
            if (DateTime.UtcNow < kernelHintExpiry)
                return cachedKernelHint;
        }

        var hint = ReadKernelHint();
        lock (probeLock)
        {
            cachedKernelHint = hint;
            kernelHintExpiry = DateTime.UtcNow.Add(CacheLifetime);
        }
        return hint;
    }

    private static string? ReadKernelHint()
    {
        foreach (var path in KernelLogPaths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                var hint = ExtractHint(File.ReadLines(path));
                if (hint != null)
                    return hint;
            }
            catch
            {
                // log non leggibile: si passa al successivo
            }
        }
        return ReadDmesgHint();
    }

    private static string? ReadDmesgHint()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dmesg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!process.Start())
                return null;

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(DmesgTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // processo gia terminato
                }
                return null;
            }

            return ExtractHint(output.GetAwaiter().GetResult().Split('\n'));
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractHint(IEnumerable<string> lines)
    {
        string? hint = null;
        foreach (var line in lines)
        {
            if (
                line.Contains("w1_", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ds18b20", StringComparison.OrdinalIgnoreCase)
                || line.Contains("onewire", StringComparison.OrdinalIgnoreCase)
            )
                hint = line.Trim();
        }
        return hint == null ? null : Truncate(hint);
    }

    private static string Truncate(string value) => value.Length > KernelHintMaxLength ? $"{value[..KernelHintMaxLength]}…" : value;
}
