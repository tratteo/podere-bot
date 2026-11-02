using System.Net;
using System.Reflection;
using System.Text;
using PodereBot.Lib;
using PodereBot.Lib.Commands;
using PodereBot.Lib.Common;
using PodereBot.Services.Panel;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Services.Hosted;

internal class BotHostedService : IHostedService
{
    private readonly ILogger logger;
    private readonly IServiceProvider services;
    private readonly CancellationTokenSource cancellationToken = new();
    public TelegramBotClient Client { get; init; }
    private readonly List<CommandRegistryKey> commandEntries;
    private readonly IConfiguration configuration;
    private readonly PendingInputRegistry pendingInputs;
    private readonly UserDirectory users;
    private readonly ITemperatureDriver temperatureReader;
    private readonly HeatingDriver heatingDriver;
    private readonly Database db;
    private readonly StartupGate startup;
    private readonly NotificationLog log;
    private readonly TimeSpan sensorTolerance;
    private DateTime startedUtc;
    private readonly List<BotCommand> allCommands;
    private readonly List<BotCommand> publicCommands;
    private readonly HashSet<long> adminScopedChats = [];
    private readonly object adminScopedChatsLock = new();
    private readonly TimeSpan heartbeatInterval;
    private readonly TimeSpan heartbeatTimeout;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private Timer? heartbeat;
    private DateTime? lastTelegramContactUtc;
    private volatile bool initialized;
    private volatile bool summarySent;

    public BotHostedService(
        ILogger<BotHostedService> logger,
        IConfiguration configuration,
        IServiceProvider services,
        PendingInputRegistry pendingInputs,
        UserDirectory users,
        ITemperatureDriver temperatureReader,
        HeatingDriver heatingDriver,
        Database db,
        StartupGate startup,
        NotificationLog log
    )
    {
        this.logger = logger;
        this.configuration = configuration;
        this.services = services;
        this.pendingInputs = pendingInputs;
        this.users = users;
        this.temperatureReader = temperatureReader;
        this.heatingDriver = heatingDriver;
        this.db = db;
        this.startup = startup;
        this.log = log;
        sensorTolerance = TimeSpan.FromSeconds(
            Math.Max(10, configuration.GetValue<int?>("Heating:TempSensorsToleranceSeconds") ?? 60)
        );
        heartbeatInterval = TimeSpan.FromSeconds(
            Math.Max(30, configuration.GetValue<int?>("Telegram:HeartbeatSeconds") ?? 120)
        );
        heartbeatTimeout = heartbeatInterval * 3;
        Client = new TelegramBotClient(configuration.GetValue<string>("TELEGRAM_API_KEY")!, cancellationToken: cancellationToken.Token);
        commandEntries = Assembly.GetExecutingAssembly().GetCommands();
        allCommands = commandEntries.ConvertAll(c => new BotCommand() { Command = c.metadata.Key, Description = c.metadata.Description });
        publicCommands = commandEntries
            .FindAll(c => !c.metadata.Admin)
            .ConvertAll(c => new BotCommand() { Command = c.metadata.Key, Description = c.metadata.Description });
        logger.LogInformation("registered {c} commands", commandEntries.Count);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Register the handler first: messages must be processed even when Telegram recovers
        // after the host is already serving the panel and the REST API.
        Client.OnMessage += OnMessage;
        startedUtc = DateTime.UtcNow;
        heartbeat = new Timer(_ => _ = PingAsync(), null, heartbeatInterval, heartbeatInterval);

        await TryInitializeAsync(cancellationToken);

        // The panel and the local controls must work even if the very first contact fails.
        startup.MarkReady();
    }

    /// <summary>
    /// One-time Telegram setup. A failure is logged and retried by the heartbeat: it must never
    /// take the host down, because the bot owns the local panel and the REST API.
    /// </summary>
    private async Task TryInitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized)
            return;

        await initializationLock.WaitAsync(CancellationToken.None);
        try
        {
            if (initialized)
                return;

            var me = await Client.GetMe(cancellationToken: cancellationToken);
            await Client.SetMyDescription($"Che vuoi?", cancellationToken: cancellationToken);
            await Client.SetMyShortDescription($"Che vuoi?", cancellationToken: cancellationToken);
            await Client.SetMyCommands(publicCommands, scope: new BotCommandScopeDefault(), cancellationToken: cancellationToken);
            foreach (var adminId in GetAdminIds())
            {
                await TrySetAdminCommandsScope(adminId, cancellationToken);
            }

            lastTelegramContactUtc = DateTime.UtcNow;
            initialized = true;
            logger.LogInformation("@{u} is running", me.Username);
        }
        catch (Exception ex)
        {
            logger.LogWarning("telegram is not reachable yet, retrying: {m}", ex.Message);
        }
        finally
        {
            initializationLock.Release();
        }

        if (initialized)
            await TrySendStartupSummaryAsync(cancellationToken);
    }

    private async Task TrySendStartupSummaryAsync(CancellationToken cancellationToken)
    {
        if (summarySent || !startup.ShouldAnnounce)
            return;

        try
        {
            await SendStartupSummaryAsync(cancellationToken);
            summarySent = true;
            startup.MarkAnnounced();
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to send the startup summary: {m}", ex.Message);
        }
    }

    private long[] GetAdminIds() => users.AdminIds();

    internal async Task TrySetAdminCommandsScope(long chatId, CancellationToken cancellationToken)
    {
        lock (adminScopedChatsLock)
        {
            if (!adminScopedChats.Add(chatId))
                return;
        }

        try
        {
            await Client.SetMyCommands(
                allCommands,
                scope: new BotCommandScopeChat() { ChatId = chatId },
                cancellationToken: cancellationToken
            );
        }
        catch (ApiRequestException ex)
        {
            lock (adminScopedChatsLock)
                adminScopedChats.Remove(chatId);
            logger.LogWarning("unable to set admin command scope for chat {c}: {m}", chatId, ex.Message);
        }
    }

    internal void ForgetAdminCommandsScope(long chatId)
    {
        lock (adminScopedChatsLock)
        {
            adminScopedChats.Remove(chatId);
        }
    }

    internal async Task NotifyTopicAsync(string topic, string message)
    {
        // Recorded before the send: the panel keeps the history even while Telegram is down.
        log.Record(topic, message);

        var ids = users.Subscribers(topic);
        if (ids.Length == 0)
        {
            logger.LogTrace("no subscribers for topic {t}", topic);
            return;
        }
        await SendToUsersAsync(ids, message);
    }

    internal async Task NotifyUsersAsync(IEnumerable<long> ids, string topic, string message)
    {
        log.Record(topic, message);
        await SendToUsersAsync(ids, message);
    }

    private async Task SendToUsersAsync(IEnumerable<long> ids, string message)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
            return;
        await Client.NotifyUsers(message, list, logger);
    }

    private async Task SendStartupSummaryAsync(CancellationToken cancellationToken)
    {
        var data = db.Data;
        var probe = await temperatureReader.ProbeLocalTemperature(cancellationToken);
        var external = temperatureReader
            .GetExternalTemperatureReadings()
            .Where(r => !r.Informational)
            .ToList();
        var fresh = temperatureReader
            .GetExternalTemperatureReadings(sensorTolerance)
            .Where(r => !r.Informational)
            .ToList();
        var freshIds = fresh.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = external.Where(r => !freshIds.Contains(r.Id)).ToList();
        var boiler = heatingDriver.IsBoilerActive();

        var operationalTemperatures = fresh.Select(r => r.Temperature).ToList();
        if (probe.Temperature != null)
            operationalTemperatures.Add(probe.Temperature.Value);
        float? operational = operationalTemperatures.Count > 0 ? operationalTemperatures.Average() : null;

        var sb = new StringBuilder();
        sb.AppendLine("🤖 <b>Presente</b>");
        sb.AppendLine($"<i>{DateTime.Now:dd/MM · HH:mm}</i>");
        sb.AppendLine();
        sb.AppendLine($"🔥 Caldaia: <b>{(boiler ? "accesa" : "spenta")}</b>");
        sb.AppendLine($"🌡️ Operativa: <b>{(operational != null ? $"{operational.Value:0.0} °C" : "n/d")}</b>");
        if (probe.Status == LocalTemperatureStatus.Ok && probe.Temperature != null)
            sb.AppendLine($"🌡️ Locale: <b>{probe.Temperature.Value:0.0} °C</b>");
        else
            sb.AppendLine($"🌡️ Locale: <b>non disponibile</b> <i>({TemperatureCause(probe.Status)})</i>");
        sb.AppendLine($"📡 Esterni: {DescribeExternal(external.Count, fresh.Count)}");
        sb.AppendLine($"📅 Programma: {DescribeProgram(data.HeatingProgram)}");
        sb.AppendLine($"🔐 Cancelli: {DescribeGates(data.GatesOpenAccessExpirationDate)}");

        var warnings = new List<string>();
        if (probe.Status != LocalTemperatureStatus.Ok)
            warnings.Add($"Sensore locale non disponibile: {TemperatureCause(probe.Status)}.");
        foreach (var reading in stale)
            warnings.Add($"Sensore {WebUtility.HtmlEncode(reading.Location)} fermo da {FormatAge(DateTime.UtcNow - reading.Timestamp)}.");
        if (data.HeatingProgram?.IsSuspended == true)
            warnings.Add("Programma di riscaldamento sospeso.");
        if (data.GatesOpenAccessExpirationDate is DateTime expiration && expiration > DateTime.Now)
            warnings.Add($"Finestra cancelli aperta fino alle {expiration:HH:mm}.");
        if (operational != null)
        {
            var antifreeze = OptionsReader.ReadAntifreeze(configuration);
            var band = OptionsReader.ReadTemperatureBand(configuration);
            if (antifreeze.Enabled && HeatingPolicies.AntifreezeShouldEngage(operational.Value, antifreeze))
                warnings.Add($"Antigelo attivo: {operational.Value:0.0} °C sotto la soglia di {antifreeze.ThresholdCelsius:0.0} °C.");
            if (HeatingPolicies.IsOutOfBand(operational.Value, band))
                warnings.Add($"Temperatura operativa fuori banda: {operational.Value:0.0} °C.");
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>⚠️ Attenzione</b>");
            foreach (var warning in warnings)
                sb.AppendLine($"• {warning}");
        }

        await NotifyTopicAsync(NotificationTopic.Technical, sb.ToString().TrimEnd());
    }

    private static string DescribeExternal(int total, int fresh)
    {
        if (total == 0)
            return "nessun sensore";
        if (fresh == total)
            return $"{total} attivi";
        return $"{fresh}/{total} attivi";
    }

    private static string DescribeProgram(HeatingProgram? program)
    {
        if (program == null)
            return "nessuno";
        if (program.IsSuspended)
            return "sospeso";
        var current = program.GetCurrentInterval();
        if (current != null)
            return $"in corso {FormatTime(current.FromTimestamp)}–{FormatTime(current.ToTimestamp)} @ {current.Temperature:0.#}°";
        var next = program.GetNextInterval();
        if (next != null)
            return $"prossimo {FormatTime(next.FromTimestamp)}–{FormatTime(next.ToTimestamp)} @ {next.Temperature:0.#}°";
        return "nessun intervallo attivo";
    }

    private static string DescribeGates(DateTime? expiration)
    {
        if (expiration is DateTime value && value > DateTime.Now)
            return $"aperta fino alle {value:HH:mm}";
        return "chiusa";
    }

    private static string TemperatureCause(LocalTemperatureStatus status) =>
        status switch
        {
            LocalTemperatureStatus.DevicesRootMissing => "configurazione",
            LocalTemperatureStatus.BusMasterMissing => "configurazione",
            LocalTemperatureStatus.NoSensors => "hardware",
            LocalTemperatureStatus.CrcError => "segnale",
            LocalTemperatureStatus.ReadError => "hardware",
            LocalTemperatureStatus.ParseError => "firmware",
            _ => "ok",
        };

    private static string FormatTime(int seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 90)
            return $"{age.TotalMinutes:0} min";
        if (age.TotalHours < 36)
            return $"{age.TotalHours:0} h";
        return $"{age.TotalDays:0} g";
    }

    public DateTime? LastTelegramContactUtc => lastTelegramContactUtc;

    public bool IsTelegramHealthy =>
        lastTelegramContactUtc.HasValue && DateTime.UtcNow - lastTelegramContactUtc.Value < heartbeatTimeout;

    private async Task PingAsync()
    {
        try
        {
            await Client.GetMe();
            lastTelegramContactUtc = DateTime.UtcNow;

            if (!initialized)
                await TryInitializeAsync(CancellationToken.None);
            else
                await TrySendStartupSummaryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("telegram heartbeat failed: {m}", ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (startedUtc != default && DateTime.UtcNow - startedUtc > TimeSpan.FromMinutes(10))
                await NotifyTopicAsync(NotificationTopic.Technical, "PodereBot offline.");
        }
        catch (Exception ex)
        {
            logger.LogDebug("unable to send the offline notice: {m}", ex.Message);
        }

        heartbeat?.Dispose();
        this.cancellationToken.Cancel();
        await Task.CompletedTask;
    }

    private async Task OnMessage(Message msg, UpdateType type)
    {
        if (DateTime.Now.ToUniversalTime() - msg.Date.ToUniversalTime() > TimeSpan.FromSeconds(30))
        {
            return;
        }
        logger.LogInformation("Received {type} '{t}' in {c}", type, msg.Text, msg.Chat);
        // logger.LogDebug("raw message: {r}", JsonConvert.SerializeObject(msg));
        if (msg.Chat.Type == ChatType.Private && msg.From != null && GetAdminIds().Contains(msg.From.Id))
        {
            await TrySetAdminCommandsScope(msg.Chat.Id, this.cancellationToken.Token);
        }
        var match = commandEntries.FirstOrDefault(c => c.metadata.Key == msg.Text);
        if (match != null)
        {
            try
            {
                var cmdService = (Command?)services.GetService(match.commandType);
                if (cmdService == null)
                {
                    logger.LogWarning("unable to retrieve command service of type {t}", match.commandType);
                    return;
                }

                await cmdService.Execute(
                    new CommandArguments()
                    {
                        Client = Client,
                        Message = msg,
                        Admin = match.metadata.Admin
                    }
                );
            }
            catch (Exception ex)
            {
                logger.LogError("error executing command [{c}]: {e}", match.metadata.Key, ex);
            }
        }
        else if (!pendingInputs.IsWaiting(msg.Chat.Id))
        {
            var aiChat = services.GetRequiredService<AiChatService>();
            await aiChat.ProcessMessageAsync(Client, msg);
        }
    }
}
