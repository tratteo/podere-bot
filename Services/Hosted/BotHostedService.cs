using System.Reflection;
using PodereBot.Lib;
using PodereBot.Lib.Commands;
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
    private readonly List<BotCommand> allCommands;
    private readonly List<BotCommand> publicCommands;
    private readonly HashSet<long> adminScopedChats = [];
    private readonly object adminScopedChatsLock = new();

    public BotHostedService(
        ILogger<BotHostedService> logger,
        IConfiguration configuration,
        IServiceProvider services,
        PendingInputRegistry pendingInputs,
        UserDirectory users
    )
    {
        this.logger = logger;
        this.configuration = configuration;
        this.services = services;
        this.pendingInputs = pendingInputs;
        this.users = users;
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
        var me = await Client.GetMe(cancellationToken: cancellationToken);

        await Client.SetMyDescription($"Che vuoi?", cancellationToken: cancellationToken);
        await Client.SetMyShortDescription($"Che vuoi?", cancellationToken: cancellationToken);
        await Client.SetMyCommands(publicCommands, scope: new BotCommandScopeDefault(), cancellationToken: cancellationToken);
        foreach (var adminId in GetAdminIds())
        {
            await TrySetAdminCommandsScope(adminId, cancellationToken);
        }
        await Client.NotifyOwners("Presente 😼", users, logger);
        Client.OnMessage += OnMessage;
        logger.LogInformation("@{u} is running", me.Username);
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Client.NotifyOwners("Torno a dormire 🌙", users, logger);
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
