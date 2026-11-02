using PodereBot.Services;
using Telegram.Bot;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/gatelight", Description = "Accendo/spengo le luci dei cancelli 💡", Admin = true)]
internal class ToggleGatesLightCommand(
    GateDriver gateDriver,
    Database db,
    IConfiguration configuration,
    ILogger<ToggleGatesLightCommand> logger,
    UserDirectory users
) : Command(logger, configuration, users)
{
    private readonly GateDriver gateDriver = gateDriver;
    private readonly Database db = db;

    protected override async Task ExecuteInternal()
    {
        await gateDriver.ToggleLights();
        await Arguments.Client.SendMessage(
            Arguments.Message.Chat.Id,
            "Ho acceso o spento le luci del cancello. Io non posso sapere in che stato sono, vai a guardare 😿",
            disableNotification: true
        );
    }
}
