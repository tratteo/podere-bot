using System.Globalization;
using System.Net;
using System.Text;
using PodereBot.Services;
using PodereBot.Services.Panel;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PodereBot.Lib.Commands.Abstract;

internal abstract class AbstractOpenGateCommand(
    GateDriver gateDriver,
    Database db,
    IConfiguration configuration,
    ILogger<AbstractOpenGateCommand> logger,
    UserDirectory users,
    GateAccessService gateAccess,
    NotificationLog notificationLog,
    GateId gateId
) : Command(logger, configuration, users)
{
    private readonly GateDriver gateDriver = gateDriver;
    private readonly Database db = db;
    private readonly GateId gateId = gateId;
    protected abstract string GateName { get; }

    private long SenderId => Arguments.Message.From!.Id;

    protected override async Task ExecuteInternal()
    {
        if (users.CanControlGates(SenderId))
        {
            await OpenProcedure();
            return;
        }

        var expiration = db.Data.GatesOpenAccessExpirationDate;
        if (!GateAccessPolicy.IsOpen(expiration, DateTime.Now))
        {
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                "I cancelli sono bloccati al momento ❌",
                disableNotification: true
            );
            return;
        }

        await OpenProcedure();
    }

    private async Task OpenProcedure()
    {
        AttachEvents();
        var msg = new StringBuilder();
        if (!users.CanControlGates(SenderId))
        {
            msg.AppendLine(
                $"""
                ⚠️ Conferma solo se sei di fronte al cancello!
                😠 Non trollate, mi scappano i cani e muoiono spiaccicati in strada! 

                """
            );
        }
        msg.AppendLine($"Confermi di voler aprire il cancello {GateName}?");
        await Arguments
            .Client.SendMessage(
                Arguments.Message.Chat.Id,
                msg.ToString(),
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup()
                    .AddButton("✅", EncodeCallbackQueryData("y"))
                    .AddButton("❌", EncodeCallbackQueryData("n")),
                disableNotification: true
            )
            .DeleteOnDetach(this);
    }

    protected override async Task OnCallback(Update update, string callbackData)
    {
        await Arguments.Client.AnswerCallbackQuery(update.CallbackQuery!.Id);

        if (callbackData == "y")
        {
            var senderId = Arguments.Message.From!.Id;
            if (!gateAccess.TryRegisterOpening(senderId, out var retryAfter))
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
                await Arguments.Client.SendMessage(
                    Arguments.Message.Chat.Id,
                    $"Hai aperto il cancello troppe volte. Riprova tra {minutes} {(minutes == 1 ? "minuto" : "minuti")}.",
                    disableNotification: true
                );
                await DetachEvents();
                return;
            }

            await Arguments.Client.SendChatAction(Arguments.Message.Chat.Id, ChatAction.Typing);
            await gateDriver.Open(gateId);

            // Every opening is an event, whoever did it: the panel history must show it even
            // when Telegram stays silent (admins) or the notification fails.
            var actor = ActorName(senderId);
            var message = $"🔑 <b>{WebUtility.HtmlEncode(actor)}</b> ha aperto il cancello {GateName}";
            notificationLog.Record(NotificationTopic.Gates, message);

            if (!users.IsAdmin(senderId))
            {
                await Arguments.Client.NotifyTopic(NotificationTopic.Gates, message, users, logger);
            }

            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                $"Ho aperto il cancello {GateName} 🐱",
                disableNotification: true
            );
        }

        await DetachEvents();
    }

    private string ActorName(long senderId)
    {
        var stored = users.Find(senderId)?.Name;
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;

        var from = Arguments.Message.From;
        var name = $"{from?.FirstName} {from?.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        if (!string.IsNullOrWhiteSpace(from?.Username))
            return $"@{from.Username}";
        return senderId.ToString(CultureInfo.InvariantCulture);
    }
}
