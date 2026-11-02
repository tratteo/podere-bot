using PodereBot.Lib.Commands.Abstract;
using PodereBot.Services;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/openauto", Description = "Ti apro il cancello automatico (forse 😼)")]
internal class OpenAutomaticGateCommand(
    GateDriver gateDriver,
    Database db,
    IConfiguration configuration,
    ILogger<OpenAutomaticGateCommand> logger,
    UserDirectory users,
    GateAccessService gateAccess,
    PodereBot.Services.Panel.NotificationLog notificationLog
) : AbstractOpenGateCommand(gateDriver, db, configuration, logger, users, gateAccess, notificationLog, GateId.automatic)
{
    protected override string GateName => "automatico";
}
