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
    private readonly StartupGate startup;
    private readonly NotificationLog log;
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
    private DateTime? telegramDownSinceUtc;
    private volatile bool initialized;
    private volatile bool summarySent;

    public BotHostedService(
        ILogger<BotHostedService> logger,
        IConfiguration configuration,
        IServiceProvider services,
        PendingInputRegistry pendingInputs,
        UserDirectory users,
        StartupGate startup,
        NotificationLog log
    )
    {
        this.logger = logger;
        this.configuration = configuration;
        this.services = services;
        this.pendingInputs = pendingInputs;
        this.users = users;
        this.startup = startup;
        this.log = log;
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

        // Say hello immediately, before the slower Telegram setup.
        await TrySendStartupSummaryAsync();

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
            telegramDownSinceUtc = null;
            initialized = true;
            logger.LogInformation("@{u} is running", me.Username);
        }
        catch (Exception ex)
        {
            telegramDownSinceUtc ??= DateTime.UtcNow;
            logger.LogWarning("telegram is not reachable yet, retrying: {m}", ex.Message);
        }
        finally
        {
            initializationLock.Release();
        }

        if (initialized)
            await TrySendStartupSummaryAsync();
    }

    private async Task TrySendStartupSummaryAsync()
    {
        if (summarySent)
            return;

        try
        {
            await SendStartupSummaryAsync();
            summarySent = true;
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

    private Task SendStartupSummaryAsync() =>
        NotifyTopicAsync(NotificationTopic.Technical, "Ce sto");
    public DateTime? LastTelegramContactUtc => lastTelegramContactUtc;

    public bool IsTelegramHealthy =>
        lastTelegramContactUtc.HasValue && DateTime.UtcNow - lastTelegramContactUtc.Value < heartbeatTimeout;

    private async Task PingAsync()
    {
        try
        {
            await Client.GetMe();
            var wasDown = ShouldAnnounceRecovery(telegramDownSinceUtc, DateTime.UtcNow, heartbeatTimeout);
            telegramDownSinceUtc = null;
            lastTelegramContactUtc = DateTime.UtcNow;

            if (!initialized)
                await TryInitializeAsync(CancellationToken.None);
            else if (wasDown)
                await SendRecoverySummaryAsync();
            else
                await TrySendStartupSummaryAsync();
        }
        catch (Exception ex)
        {
            telegramDownSinceUtc ??= DateTime.UtcNow;
            logger.LogWarning("telegram heartbeat failed: {m}", ex.Message);
        }
    }

    /// <summary>Only a real outage should re-announce the bot; a short blip must stay quiet.</summary>
    internal static bool ShouldAnnounceRecovery(DateTime? downSinceUtc, DateTime nowUtc, TimeSpan timeout) =>
        downSinceUtc is DateTime since && nowUtc - since >= timeout;

    /// <summary>After a real outage, send the boot summary again so the chat knows the bot is back.</summary>
    private async Task SendRecoverySummaryAsync()
    {
        try
        {
            logger.LogInformation("telegram is back, sending the summary");
            await SendStartupSummaryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to send the recovery summary: {m}", ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (startedUtc != default && DateTime.UtcNow - startedUtc > TimeSpan.FromMinutes(10))
                await NotifyTopicAsync(NotificationTopic.Technical, "me so scocciato, me dò");
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
