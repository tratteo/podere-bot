using PodereBot.Services;
using Telegram.Bot;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/sendpos", Description = "Ti mando la posizione di casa 📍")]
internal class SendPositionCommand(
    ILogger<SendPositionCommand> logger,
    IConfiguration configuration,
    UserDirectory users
) : Command(logger, configuration, users)
{
    protected override async Task ExecuteInternal()
    {
        await Arguments.Client.SendVenue(
            Arguments.Message.Chat.Id,
            41.4986180363405,
            12.797875896442752,
            "Podere 739 (canne libere 🪴)",
            "Via del Valloncello 16"
        );
    }
}
