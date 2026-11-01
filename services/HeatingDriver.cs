using PodereBot.Lib;
using PodereBot.Services;
using PodereBot.Services.Hosted;

internal class HeatingDriver(
    ILogger<HeatingDriver> logger,
    IPinDriver pinDriver,
    IConfiguration configuration,
    ITemperatureDriver temperatureReader,
    BotHostedService bot,
    Database db
)
{

    private readonly int heatingPin = configuration.GetValue<int>("Pins:Heating");
    private bool temperatureUnavailableNotified = false;
    private readonly TimeSpan readingsTimespan = TimeSpan.FromSeconds(
        configuration.GetValue<int?>("Heating:TempSensorsToleranceSeconds") ?? 60
    );

    public bool IsBoilerActive()
    {
        return pinDriver.DigitalRead(heatingPin) == 1;
    }

    public async Task<float?> GetOperationalTemperature(CancellationToken? cancellationToken = null)
    {
        var readings = temperatureReader.GetExternalTemperatureReadings(readingsTimespan);
        var operationalReadings = readings.Where(r => !r.Informational).ToList();
        var temperatures = operationalReadings.ConvertAll(r => r.Temperature);
        var localTemperature = await temperatureReader.GetLocalTemperature(cancellationToken);
        if (localTemperature != null)
        {
            if (temperatureUnavailableNotified)
            {
                _ = bot.NotifyTopicAsync(NotificationTopic.Technical, "Sensore locale tornato disponibile.");
                temperatureUnavailableNotified = false;
            }
            temperatures.Add((float)localTemperature);
        }
        else if (!temperatureUnavailableNotified)
        {
            _ = bot.NotifyTopicAsync(NotificationTopic.Technical, "Sensore locale non disponibile.");
            temperatureUnavailableNotified = true;
        }
        if (temperatures.Count > 0)
        {
            logger.LogDebug(
                "{c} operational temp readings [avg: {r}, min: {m}, max: {min}]",
                operationalReadings.Count,
                temperatures.Average(),
                temperatures.Min(),
                temperatures.Max()
            );
            return temperatures.Average();
        }
        return null;
    }

    public void SwitchHeating(bool status)
    {
        if (status)
        {
            pinDriver.PinHigh(heatingPin);
            db.Edit(d => d.BoilerActive = true);
        }
        else
        {
            pinDriver.PinLow(heatingPin);
            db.Edit(d => d.BoilerActive = false);
        }
    }
}
