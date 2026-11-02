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
    private readonly HomeOptions home = OptionsReader.ReadHome(configuration);

    protected override async Task ExecuteInternal()
    {
        await Arguments.Client.SendVenue(Arguments.Message.Chat.Id, home.Latitude, home.Longitude, home.Name, home.Address);
    }
}
