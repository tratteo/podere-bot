namespace PodereBot.Services;

internal class MockTemperatureDriver(Database db) : ITemperatureDriver
{
    private readonly Random random = new();
    private double currentTemp = 18;
    private Dictionary<string, TemperatureReading> readings = [];

    public void PostTemperatureReading(TemperatureReading reading)
    {
        readings[reading.Id] = reading;
    }

    public async Task<float?> GetLocalTemperature(CancellationToken? token = null)
    {
        await Task.Delay(random.Next(250, 1000));
        currentTemp = db.Data.BoilerActive ? currentTemp + random.NextDouble() * 0.2 : currentTemp - random.NextDouble() * 0.2;
        return await Task.FromResult((float?)currentTemp);
    }

    public async Task<LocalTemperatureProbe> ProbeLocalTemperature(CancellationToken? token = null)
    {
        var temperature = await GetLocalTemperature(token);
        return new LocalTemperatureProbe
        {
            Status = LocalTemperatureStatus.Ok,
            Temperature = temperature,
            LastSuccessUtc = DateTime.UtcNow,
            DevicesRootExists = true,
            BusMasterPresent = true,
            OverlayConfigured = true,
            Sensors = ["mock"],
            CrcValid = true,
        };
    }

    public List<TemperatureReading> GetExternalTemperatureReadings(TimeSpan? tolerance = null)
    {
        if (tolerance == null)
        {
            return [.. readings.Values];
        }
        return readings.Values.Where(r => DateTime.UtcNow.Subtract(r.Timestamp).Duration() < tolerance).ToList();
    }
}
