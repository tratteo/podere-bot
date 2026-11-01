using PodereBot.Lib.Commands.Abstract;
using PodereBot.Services;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/openped", Description = "Ti apro il cancello pedonale (forse 😼)")]
internal class OpenPedestrianGateCommand(
    GateDriver gateDriver,
    Database db,
    IConfiguration configuration,
    ILogger<OpenPedestrianGateCommand> logger,
    UserDirectory users
) : AbstractOpenGateCommand(gateDriver, db, configuration, logger, users, GateId.pedestrian)
{
    protected override string GateName => "pedonale";
}
