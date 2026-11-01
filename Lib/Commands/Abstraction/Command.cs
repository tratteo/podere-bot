using PodereBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Lib.Commands;

internal readonly struct CommandArguments
{
    public readonly TelegramBotClient Client { get; init; }

    public readonly Message Message { get; init; }

    public readonly bool Admin { get; init; }
}

internal abstract class Command(ILogger<Command> logger, IConfiguration configuration, UserDirectory users)
{
    protected readonly string instanceId = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    private readonly ILogger<Command> logger = logger;
    protected readonly IConfiguration configuration = configuration;
    protected readonly UserDirectory users = users;
    private static readonly string callbackDataSeparator = "♦";
    private readonly List<Message> messagesToDelete = [];
    private readonly List<Message> messagesToCleanupMd = [];
    protected CommandArguments Arguments { get; private set; }

    public async Task Execute(CommandArguments arguments)
    {
        if (!await HandleAuthorization(arguments))
            return;

        Arguments = arguments;

        await ExecuteInternal();

        await arguments.Client.SetMessageReaction(
            arguments.Message.Chat.Id,
            arguments.Message.MessageId,
            [arguments.Message.RandomEmoji()]
        );
    }

    public void AddToRemove(Message? message)
    {
        if (message != null)
        {
            messagesToDelete.Add(message);
        }
    }

    public void AddToCleanupMd(Message? message)
    {
        if (message != null)
        {
            messagesToCleanupMd.Add(message);
        }
    }

    protected void AttachEvents()
    {
        Arguments.Client.OnUpdate += OnUpdateInternal;
        Arguments.Client.OnMessage += OnMessage;
    }

    protected virtual Task OnDetach()
    {
        return Task.CompletedTask;
    }

    protected async Task DetachEvents()
    {
        Arguments.Client.OnUpdate -= OnUpdateInternal;
        Arguments.Client.OnMessage -= OnMessage;
        foreach (var message in messagesToDelete)
        {
            await Arguments.Client.DeleteMessage(message.Chat.Id, message.MessageId);
        }
        foreach (var message in messagesToCleanupMd)
        {
            await Arguments.Client.EditMessageReplyMarkup(message.Chat.Id, message.MessageId);
        }
        await OnDetach();
    }

    private async Task<bool> HandleAuthorization(CommandArguments arguments)
    {
        if (!arguments.Admin)
            return true;

        if (!users.IsAdmin(arguments.Message.From!.Id))
        {
            await arguments.Client.SendChatAction(arguments.Message.Chat.Id, ChatAction.ChooseSticker);
            await arguments.Client.SendMessage(arguments.Message.Chat.Id, "Non hai abbastanza poteri canide 🐶", disableNotification: true);
            return false;
        }
        return true;
    }

    protected string EncodeCallbackQueryData(object data)
    {
        var encoded = $"{instanceId}{callbackDataSeparator}{Convert.ToString(data)}";
        return encoded;
    }

    protected bool DecodeCallbackQueryData(string? data, out string? value)
    {
        value = null;
        if (data == null)
            return false;
        var splits = data.Split(callbackDataSeparator);
        if (splits.Length <= 1)
            return false;

        var id = splits[0];
        if (id != instanceId)
            return false;
        value = string.Join(callbackDataSeparator, splits.Skip(1));
        return value != null;
    }

    protected abstract Task ExecuteInternal();

    private async Task OnUpdateInternal(Update update)
    {
        await OnUpdate(update);
        if (!DecodeCallbackQueryData(update.CallbackQuery?.Data, out var data))
            return;
        await OnCallback(update, data!);
    }

    protected virtual Task OnMessage(Message message, UpdateType type)
    {
        return Task.CompletedTask;
    }

    protected virtual Task OnUpdate(Update update)
    {
        return Task.CompletedTask;
    }

    protected virtual Task OnCallback(Update update, string callbackData)
    {
        return Task.CompletedTask;
    }
}
