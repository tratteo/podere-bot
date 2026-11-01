using System.Diagnostics;
using System.Globalization;
using System.Text;
using PodereBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/status", Description = "Ti mando le statistiche del sistema")]
internal class StatusCommand(
    ILogger<StartCommand> logger,
    IConfiguration configuration,
    UsageTracker usageTracker,
    UserDirectory users
) : Command(logger, configuration, users)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");
    private const int UsageDays = 7;
    private const int BudgetCells = 10;
    private const string CpuTemperaturePath = "/sys/class/thermal/thermal_zone0/temp";

    protected override async Task ExecuteInternal()
    {
        using var process = Process.GetCurrentProcess();
        var uptime = DateTime.Now - process.StartTime;
        var usage = usageTracker.GetLastDays(UsageDays);
        var weeklyBudget = configuration.GetValue<double?>("AI:WeeklyBudgetUsd") ?? 30;
        var percent = weeklyBudget > 0 ? usage.CostUsd / weeklyBudget * 100 : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"{StatusDot(percent, weeklyBudget)} <b>PodereBot</b>");
        sb.AppendLine();
        sb.AppendLine("<b>Sistema</b>");
        sb.AppendLine($"<pre>{BuildSystemBlock(process, uptime)}</pre>");
        sb.AppendLine();
        sb.AppendLine($"<b>OpenCode Go · ultimi {UsageDays} giorni</b>");

        if (usage.Calls == 0)
        {
            sb.AppendLine("Nessuna chiamata registrata");
        }
        else
        {
            var totalTokens = usage.InputTokens + usage.OutputTokens;
            sb.AppendLine(BuildBudgetBlock(usage.CostUsd, weeklyBudget, percent, usage.Calls));
            sb.AppendLine($"{FormatTokens(totalTokens)} token · {FormatTokens(usage.CachedInputTokens)} da cache");
        }

        await Arguments.Client.SendMessage(
            Arguments.Message.Chat.Id,
            sb.ToString(),
            parseMode: ParseMode.Html,
            disableNotification: true
        );
    }

    private static string BuildSystemBlock(Process process, TimeSpan uptime)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("Attivo da", FormatUptime(uptime)),
            ("Memoria", FormatMegabytes(process.PrivateMemorySize64)),
        };

        var disk = TryGetDiskSpace();
        if (disk != null)
            rows.Add(disk.Value);

        var temperature = TryGetCpuTemperature();
        if (temperature != null)
            rows.Add(("CPU", $"{temperature.Value.ToString("0.0", Culture)} °C"));

        var width = rows.Max(r => r.Label.Length);
        var sb = new StringBuilder();
        foreach (var (label, value) in rows)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(label.PadRight(width)).Append("  ").Append(value);
        }
        return sb.ToString();
    }

    private static string BuildBudgetBlock(double cost, double budget, double percent, long calls)
    {
        var lines = new List<string>();
        if (budget > 0 && percent >= 1)
        {
            var marker = percent >= 100 ? "⚠️ " : "";
            lines.Add($"{marker}{BuildBudgetBar(percent)} <b>{percent.ToString("0", Culture)}%</b> del budget");
        }

        var spent = budget > 0 ? $"{FormatCost(cost)} di {FormatCost(budget)}" : $"{FormatCost(cost)} spesi";
        lines.Add($"{spent} · <b>{FormatCount(calls)}</b> chiamate");
        return string.Join('\n', lines);
    }

    private static string BuildBudgetBar(double percent)
    {
        var ratio = Math.Clamp(percent / 100, 0, 1);
        var filled = (int)Math.Round(ratio * BudgetCells);
        return new string('█', filled) + new string('░', BudgetCells - filled);
    }

    private static string StatusDot(double percent, double budget)
    {
        if (budget <= 0)
            return "🟢";
        if (percent >= 85)
            return "🔴";
        if (percent >= 60)
            return "🟠";
        return "🟢";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{uptime.Days}g {uptime.Hours:00}h {uptime.Minutes:00}m";
        if (uptime.TotalHours >= 1)
            return $"{uptime.Hours}h {uptime.Minutes:00}m";
        if (uptime.TotalMinutes >= 1)
            return $"{uptime.Minutes}m";
        return $"{uptime.Seconds}s";
    }

    private static string FormatMegabytes(long bytes) => $"{(bytes / 1e6).ToString("0.0", Culture)} MB";

    private static string FormatCount(long value) => value.ToString("N0", Culture);

    private static string FormatCost(double value)
    {
        if (value <= 0)
            return "$0,00";
        if (value < 0.01)
            return "&lt;$0,01";
        return $"${value.ToString("0.00", Culture)}";
    }

    private static string FormatTokens(long value)
    {
        if (value >= 1_000_000)
            return $"{(value / 1_000_000d).ToString("0.#", Culture)}M";
        if (value >= 1_000)
            return $"{(value / 1_000d).ToString("0.#", Culture)}k";
        return value.ToString("0", Culture);
    }

    private static (string Label, string Value)? TryGetDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(root))
                return null;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return null;

            var volume = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(volume))
                volume = root;

            var free = drive.AvailableFreeSpace / 1e9;
            var total = drive.TotalSize / 1e9;
            var value = $"{free.ToString("0.0", Culture)} / {total.ToString("0.0", Culture)} GB";
            return ($"Disco {volume}", value);
        }
        catch
        {
            return null;
        }
    }

    private static double? TryGetCpuTemperature()
    {
        try
        {
            if (!File.Exists(CpuTemperaturePath))
                return null;

            var raw = File.ReadAllText(CpuTemperaturePath).Trim();
            return long.TryParse(raw, out var milli) ? milli / 1000d : null;
        }
        catch
        {
            return null;
        }
    }
}
