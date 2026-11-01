using PodereBot.Services.Hosted;
using System.Diagnostics;
using System.Globalization;

namespace PodereBot.Services;

internal sealed record HealthReport(bool TelegramOk, bool DatabaseOk, string? Throttled, double? CpuTemperatureCelsius)
{
    public bool Ok => TelegramOk && DatabaseOk;
}

/// <summary>
/// Health signals used by the API and by the systemd watchdog: an HTTP port that answers is not
/// enough, the Telegram connection and the database must be alive too.
/// </summary>
internal sealed class HealthService(Database db, BotHostedService bot)
{
    public HealthReport Check() =>
        new(bot.IsTelegramHealthy, db.TryProbeWritable(), TryGetThrottled(), TryGetCpuTemperature());

    public static double? TryGetCpuTemperature()
    {
        try
        {
            const string path = "/sys/class/thermal/thermal_zone0/temp";
            if (!File.Exists(path))
                return null;
            var raw = File.ReadAllText(path).Trim();
            return long.TryParse(raw, out var milli) ? milli / 1000d : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetThrottled()
    {
        var exe = new[] { "/usr/bin/vcgencmd", "/opt/vc/bin/vcgencmd" }.FirstOrDefault(File.Exists);
        if (exe == null)
            return null;

        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(exe, "get_throttled")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // Ignore.
                }
                return null;
            }

            var value = output.Trim().Split('=').LastOrDefault()?.Trim();
            return Decode(value);
        }
        catch (Exception ex)
        {
            return $"non disponibile ({ex.Message})";
        }
    }

    public static string DecodeThrottled(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return "sconosciuto";

        var normalized = hex.Replace("0x", "").Replace("0X", "");
        if (!long.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
            return "sconosciuto";

        if (flags == 0)
            return "ok";

        var issues = new List<string>();
        if ((flags & 0x1) != 0)
            issues.Add("sottotensione");
        if ((flags & 0x2) != 0)
            issues.Add("frequenza limitata");
        if ((flags & 0x4) != 0)
            issues.Add("throttling");
        if ((flags & 0x8) != 0)
            issues.Add("limite termico");
        if ((flags & 0x10000) != 0)
            issues.Add("sottotensione pregressa");
        if ((flags & 0x20000) != 0)
            issues.Add("frequenza limitata in passato");
        if ((flags & 0x40000) != 0)
            issues.Add("throttling pregresso");
        if ((flags & 0x80000) != 0)
            issues.Add("limite termico pregresso");
        return string.Join(", ", issues);
    }

    private static string Decode(string? value) => DecodeThrottled(value);
}
