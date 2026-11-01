using System.Text;
using PodereBot.Services;
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
        if (expiration == null || DateTime.Now >= expiration)
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
            await Arguments.Client.SendChatAction(Arguments.Message.Chat.Id, ChatAction.Typing);
            await gateDriver.Open(gateId);
            await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                $"Ho aperto il cancello {GateName} 🐱",
                disableNotification: true
            );
            if (Arguments.Message.From != null && !users.CanControlGates(Arguments.Message.From.Id))
            {
                var name = Arguments.Message.From.FirstName;
                if (!string.IsNullOrEmpty(Arguments.Message.From.LastName))
                {
                    name += $" {Arguments.Message.From.LastName}";
                }
                await Arguments.Client.NotifyOwners($"🔑 <b>{name}</b> ha aperto il cancello {GateName}", users, logger);
            }
        }

        await DetachEvents();
    }
}
