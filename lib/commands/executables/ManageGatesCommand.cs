using PodereBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/gates", Description = "Abilito o disabilito l'apertura dei cancelli agli utenti 🔐", Admin = true)]
internal class ManageGatesCommand(
    ILogger<ManageGatesCommand> logger,
    Database database,
    IConfiguration configuration,
    UserDirectory users
) : Command(logger, configuration, users)
{
    private readonly Database database = database;

    protected override async Task ExecuteInternal()
    {
        AttachEvents();
        var kbd = new InlineKeyboardMarkup();
        for (var i = 1; i <= 24; i++)
        {
            kbd.AddButton(i.ToString(), EncodeCallbackQueryData(i));
            if (i % 6 == 0)
            {
                kbd.AddNewRow();
            }
        }
        kbd.AddNewRow();
        kbd.AddButton("Blocca", EncodeCallbackQueryData("block"));
        kbd.AddNewRow();
        kbd.AddButton("Chiudi", EncodeCallbackQueryData(-1));
        var status =
            database.Data.GatesOpenAccessExpirationDate == null || database.Data.GatesOpenAccessExpirationDate < DateTime.Now
                ? "🔒 I cancelli sono <b>bloccati</b>"
                : $"🔓 I cancelli sono <b>sbloccati</b> fino al <b>{database.Data.GatesOpenAccessExpirationDate}</b>";
        await Arguments
            .Client.SendMessage(
                Arguments.Message.Chat.Id,
                $"{status}.\n\n🕓 Per quante ore vuoi che i cancelli siano abilitati a tutti?\nSeleziona <b>Blocca</b> per disabilitare l'accesso ai cancelli",
                replyMarkup: kbd,
                parseMode: ParseMode.Html,
                disableNotification: true
            )
            .DeleteOnDetach(this);
    }

    protected override async Task OnCallback(Update update, string callbackData)
    {
        await Arguments.Client.AnswerCallbackQuery(update.CallbackQuery!.Id);
        var date = DateTime.Now;

        if (callbackData == "block")
        {
            var confirmKbd = new InlineKeyboardMarkup()
                .AddButton("✅ Blocca", EncodeCallbackQueryData("confirmblock"))
                .AddButton("❌ Annulla", EncodeCallbackQueryData("cancel"));
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "⚠️ <b>Bloccare i cancelli?</b>\nGli utenti non amministratori non potranno più aprirli.",
                parseMode: ParseMode.Html,
                replyMarkup: confirmKbd,
                disableNotification: true
            );
            return;
        }

        if (callbackData == "confirmblock")
        {
            database.Edit(
                (data) =>
                {
                    data.GatesOpenAccessExpirationDate = null;
                    data.GatesOpenAccessStartDate = null;
                }
            );
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "I cancelli sono <b>bloccati</b>, solo gli amministratori possono utilizzarli.",
                parseMode: ParseMode.Html,
                disableNotification: true
            );
        }
        else if (callbackData == "cancel")
        {
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "Operazione annullata.",
                disableNotification: true
            );
        }
        else if (int.TryParse(callbackData, out var hours) && hours > 0)
        {
            var start = date;
            date = date.AddHours(hours);
            database.Edit(
                (data) =>
                {
                    data.GatesOpenAccessStartDate = start;
                    data.GatesOpenAccessExpirationDate = date;
                }
            );
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                $"I cancelli sono sbloccati fino al <b>{date}</b>",
                parseMode: ParseMode.Html,
                disableNotification: true
            );
        }

        await DetachEvents();
    }
}
