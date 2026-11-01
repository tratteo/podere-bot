namespace PodereBot.Services;

public enum GateId
{
    automatic,
    pedestrian
}

internal class GateDriver(ILogger<GateDriver> logger, IPinDriver pinDriver, IConfiguration configuration)
{
    const int IMPULSE_DURATION_MS = 600;

    private readonly ILogger logger = logger;
    private readonly IPinDriver pinDriver = pinDriver;
    private readonly IConfiguration configuration = configuration;

    public async Task ToggleLights()
    {
        int? pin = configuration.GetValue<int>("Pins:GatesLight");
        logger.LogDebug("serial pin: {p}", pin);
        pinDriver.PinHigh(pin);
        await Task.Delay(IMPULSE_DURATION_MS);
        pinDriver.PinLow(pin);
    }

    public async Task Open(GateId gate)
    {
        int? pin = null;
        switch (gate)
        {
            case GateId.automatic:
                pin = configuration.GetValue<int>("Pins:AutomaticGate");
                break;
            case GateId.pedestrian:
                pin = configuration.GetValue<int>("Pins:PedestrianGate");
                break;
        }
        pinDriver.PinHigh(pin);
        await Task.Delay(IMPULSE_DURATION_MS);
        pinDriver.PinLow(pin);
    }
}
