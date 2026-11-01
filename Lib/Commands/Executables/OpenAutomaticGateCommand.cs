using PodereBot.Lib.Commands.Abstract;
using PodereBot.Services;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/openauto", Description = "Ti apro il cancello automatico (forse 😼)")]
internal class OpenAutomaticGateCommand(
    GateDriver gateDriver,
    Database db,
    IConfiguration configuration,
    ILogger<OpenAutomaticGateCommand> logger,
    UserDirectory users
) : AbstractOpenGateCommand(gateDriver, db, configuration, logger, users, GateId.automatic)
{
    protected override string GateName => "automatico";
}
