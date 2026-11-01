using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/myinfo", Description = "Ti mando le info Telegram del tuo profilo 🔎")]
internal class UserInfoCommand(IConfiguration configuration, ILogger<UserInfoCommand> logger)
    : Command(logger, configuration)
{
    private User? user;

    protected override async Task ExecuteInternal()
    {
        AttachEvents();
        user = Arguments.Message.From;
        var serialized = JsonConvert.SerializeObject(Arguments.Message.From, Formatting.Indented);
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
            962154266,
            $"Nuove info 📥\n\n<pre language='json'>{JsonConvert.SerializeObject(user, Formatting.Indented)}</pre>",
            parseMode: ParseMode.Html
        );
        await Arguments.Client.SendMessage(Arguments.Message.Chat.Id, "🟢 Ho inoltrato le info a Trat");

        await DetachEvents();
    }
}
