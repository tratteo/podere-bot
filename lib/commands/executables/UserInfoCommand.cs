using PodereBot.Services;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/myinfo", Description = "Ti mando le info Telegram del tuo profilo 🔎")]
internal class UserInfoCommand(
    IConfiguration configuration,
    ILogger<UserInfoCommand> logger,
    UserDirectory users
) : Command(logger, configuration, users)
{
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private User? user;

    /// <summary>
    /// The forward target never depends on who sent the command: a normal user used to compute
    /// a zero id and the forward silently failed. Configured owner first, then the first admin.
    /// </summary>
    private long OwnerId
    {
        get
        {
            var configured = configuration.GetValue<long?>("Telegram:OwnerId") ?? 0;
            return configured > 0 ? configured : users.AdminIds().FirstOrDefault();
        }
    }

    protected override async Task ExecuteInternal()
    {
        AttachEvents();
        user = Arguments.Message.From;
        var serialized = JsonSerializer.Serialize(Arguments.Message.From, jsonOptions);
        var kbd = new InlineKeyboardMarkup().AddButton("Manda a Trat 💬", EncodeCallbackQueryData("send"));
        await Arguments
            .Client.SendMessage(
                Arguments.Message.Chat.Id,
                $"<pre language='json'>{serialized}</pre>",
                parseMode: ParseMode.Html,
                replyMarkup: kbd,
                disableNotification: true
            )
            .CleanupOnDetach(this);
    }

    protected override async Task OnCallback(Update update, string callbackData)
    {
        var owner = OwnerId;
        if (owner <= 0)
        {
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "🔴 Nessun destinatario configurato: avvisa un amministratore.",
                disableNotification: true
            );
            await DetachEvents();
            return;
        }

        var payload = user ?? update.CallbackQuery?.From;
        try
        {
            await Arguments.Client.SendMessage(
                owner,
                $"Nuove info 📥\n\n<pre language='json'>{JsonSerializer.Serialize(payload, jsonOptions)}</pre>",
                parseMode: ParseMode.Html
            );
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "🟢 Ho inoltrato le info a Trat",
                disableNotification: true
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to forward user info to {owner}: {message}", owner, ex.Message);
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "🔴 Non sono riuscito a inoltrare le info. Riprova tra poco.",
                disableNotification: true
            );
        }

        await DetachEvents();
    }
}
