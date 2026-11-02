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
    private long OwnerId => users.AdminIds().FirstOrDefault(Arguments.Message.Chat.Id);

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
        await Arguments.Client.SendMessage(
            OwnerId,
            $"Nuove info 📥\n\n<pre language='json'>{JsonSerializer.Serialize(user, jsonOptions)}</pre>",
            parseMode: ParseMode.Html
        );
        await Arguments.Client.SendMessage(Arguments.Message.Chat.Id, "🟢 Ho inoltrato le info a Trat");

        await DetachEvents();
    }
}
