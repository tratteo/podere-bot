internal record TemperatureReading
{
    public required float Temperature { get; init; }

    public required DateTime Timestamp { get; init; }

    public required string Id { get; init; }
    public required string Location { get; init; }
    public bool Informational { get; init; }
}

internal enum LocalTemperatureStatus
{
    Ok,
    DevicesRootMissing,
    BusMasterMissing,
    NoSensors,
    CrcError,
    ParseError,
    ReadError,
}

internal sealed record LocalTemperatureProbe
{
    public required LocalTemperatureStatus Status { get; init; }
    public float? Temperature { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool DevicesRootExists { get; init; }
    public bool BusMasterPresent { get; init; }
    public bool? OverlayConfigured { get; init; }
    public IReadOnlyList<string> Sensors { get; init; } = [];
    public bool? CrcValid { get; init; }
    public string? KernelHint { get; init; }
    public string? Error { get; init; }
}

internal interface ITemperatureDriver
{
    public Task<float?> GetLocalTemperature(CancellationToken? token = null);
    public Task<LocalTemperatureProbe> ProbeLocalTemperature(CancellationToken? token = null);
    public List<TemperatureReading> GetExternalTemperatureReadings(TimeSpan? tolerance = null);
    public void PostTemperatureReading(TemperatureReading reading);
}
