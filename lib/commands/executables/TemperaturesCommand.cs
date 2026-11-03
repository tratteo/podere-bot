using System.Globalization;
using System.Text;
using PodereBot.Services;
using PodereBot.Services.Panel;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/temperatures", Description = "Ti mando tutte le temperature dei sensori della casa 🌡️")]
internal class TemperaturesCommand(
    ILogger<StartCommand> logger,
    IConfiguration configuration,
    ITemperatureDriver temperatureDriver,
    UserDirectory users,
    WeatherService weather
) : Command(logger, configuration, users)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");
    private readonly System.Timers.Timer refreshTimer = new(TimeSpan.FromSeconds(5));
    private readonly InlineKeyboardMarkup kbd = new();

    protected override Task OnDetach()
    {
        refreshTimer.Stop();
        return Task.CompletedTask;
    }

    protected override async Task ExecuteInternal()
    {
        AttachEvents();
        await Arguments.Client.SendChatAction(Arguments.Message.Chat.Id, ChatAction.Typing);
        kbd.AddButton("Chiudi", EncodeCallbackQueryData("cancel"));
        var message = await Arguments
            .Client.SendMessage(
                Arguments.Message.Chat.Id,
                "🌡️ <b>Temperature</b>\n\nSto leggendo i sensori…",
                replyMarkup: kbd,
                parseMode: ParseMode.Html,
                disableNotification: true
            )
            .DeleteOnDetach(this);
        await UpdateStatusMessage(message);
        refreshTimer.Elapsed += (_, _) => _ = UpdateStatusMessage(message);
        refreshTimer.Start();
    }

    private async Task UpdateStatusMessage(Message? message)
    {
        if (message == null)
            return;

        try
        {
            await Arguments.Client.EditMessageText(
                message.Chat.Id,
                message.Id,
                await BuildMessage(),
                replyMarkup: kbd,
                parseMode: ParseMode.Html
            );
        }
        catch (Exception) { }
    }

    private async Task<string> BuildMessage()
    {
        var probe = await temperatureDriver.ProbeLocalTemperature();
        var externals = temperatureDriver.GetExternalTemperatureReadings();

        var sb = new StringBuilder();
        sb.AppendLine(probe.Status == LocalTemperatureStatus.Ok ? "🟢 <b>Temperature</b>" : "🔴 <b>Temperature</b>");
        sb.AppendLine();
        sb.AppendLine($"🏠 <b>Host</b> · {FormatTemperature(probe.Temperature)}");
        var outdoor = await weather.GetAsync(TimeSpan.FromSeconds(3));
        var humidity = outdoor?.Humidity is int relativeHumidity ? $" · {relativeHumidity}% UR" : "";
        sb.AppendLine(
            outdoor != null
                ? $"🌤️ <b>Esterno (Open-Meteo)</b> · {outdoor.Temperature.ToString("0.0", Culture)} °C{humidity}"
                : "🌤️ <b>Esterno (Open-Meteo)</b> · <i>non disponibile</i>"
        );
        if (externals.Count == 0)
            sb.AppendLine("📡 <i>Nessun sensore remoto.</i>");
        foreach (var reading in externals)
        {
            var age = FormatAge(DateTime.UtcNow - reading.Timestamp);
            sb.AppendLine($"📡 <b>{Escape(reading.Location)}</b> · {FormatTemperature(reading.Temperature)} <i>({age})</i>");
        }

        if (probe.Status != LocalTemperatureStatus.Ok)
        {
            sb.AppendLine();
            sb.AppendLine("⚠️ <b>Sensore host non disponibile</b>");
            foreach (var (label, value) in BuildDiagnosticsRows(probe))
                sb.AppendLine($"• {label} · {value}");
            sb.AppendLine();
            sb.AppendLine($"<i>{Describe(probe)}</i>");
            if (!string.IsNullOrWhiteSpace(probe.Error))
                sb.AppendLine($"<i>{Escape(probe.Error)}</i>");
            if (!string.IsNullOrWhiteSpace(probe.KernelHint))
                sb.AppendLine($"<i>kernel: {Escape(probe.KernelHint)}</i>");
        }

        return sb.ToString();
    }

    private static List<(string Label, string Value)> BuildDiagnosticsRows(LocalTemperatureProbe probe) =>
    [
        ("Bus 1-Wire", DescribeBus(probe)),
        ("Sensori", probe.Sensors.Count.ToString("0", Culture)),
        ("Overlay", DescribeOverlay(probe.OverlayConfigured)),
        ("Ultima OK", probe.LastSuccessUtc.HasValue ? probe.LastSuccessUtc.Humanize() : "mai"),
        ("Tentativi", probe.ConsecutiveFailures.ToString("0", Culture)),
        ("Causa", DescribeCause(probe)),
    ];

    private static string DescribeBus(LocalTemperatureProbe probe)
    {
        if (!probe.DevicesRootExists)
            return "assente";
        return probe.BusMasterPresent ? "ok" : "senza master";
    }

    private static string DescribeOverlay(bool? configured) =>
        configured switch
        {
            true => "configurato",
            false => "assente",
            _ => "sconosciuto",
        };

    private static string DescribeCause(LocalTemperatureProbe probe) =>
        probe.Status switch
        {
            LocalTemperatureStatus.DevicesRootMissing => "configurazione",
            LocalTemperatureStatus.BusMasterMissing => "configurazione",
            LocalTemperatureStatus.NoSensors => "hardware",
            LocalTemperatureStatus.CrcError => "segnale",
            LocalTemperatureStatus.ReadError => "hardware",
            LocalTemperatureStatus.ParseError => "firmware",
            _ => "-",
        };

    private static string Describe(LocalTemperatureProbe probe) =>
        probe.Status switch
        {
            LocalTemperatureStatus.DevicesRootMissing =>
                "Bus 1-Wire assente: manca dtoverlay=w1-gpio in config.txt o il modulo non è caricato.",
            LocalTemperatureStatus.BusMasterMissing => "Bus presente ma master 1-Wire non registrato: driver non caricato.",
            LocalTemperatureStatus.NoSensors =>
                "Bus attivo ma nessun sensore risponde: controlla cablaggio, alimentazione e pull-up sul GPIO4.",
            LocalTemperatureStatus.CrcError =>
                "Il sensore risponde ma il CRC fallisce: alimentazione parassita o cavo troppo lungo.",
            LocalTemperatureStatus.ReadError =>
                "Il nodo del sensore è sparito durante la lettura: il kernel lo ha rimosso dal bus.",
            LocalTemperatureStatus.ParseError => "Il sensore ha risposto con un formato inatteso.",
            _ => "Sensore disponibile.",
        };

    private static string FormatTemperature(float? temperature) =>
        temperature.HasValue ? $"{temperature.Value.ToString("0.0", Culture)} °C" : "n/d";

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 90)
            return "ora";
        if (age.TotalMinutes < 90)
            return $"{age.Minutes}m";
        if (age.TotalHours < 36)
            return $"{age.Hours}h";
        return $"{age.Days}g";
    }

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    protected override async Task OnMessage(Message message, UpdateType type)
    {
        await DetachEvents();
    }

    protected override async Task OnUpdate(Update update)
    {
        await DetachEvents();
    }
}
