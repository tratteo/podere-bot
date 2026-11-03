namespace PodereBot.Services.Panel;

internal sealed record PanelAlertDto(string Id, string Title, string Detail, string Target);

internal sealed record PanelHealthDto(
    bool Ok,
    bool CoreOk,
    bool TelegramOk,
    bool DatabaseOk,
    bool PanelStoreOk,
    string? Throttled,
    double? CpuTemperatureCelsius
);

internal sealed record PanelIntervalDto(int From, int To, double Temperature);

internal sealed record PanelProgramDto(bool Suspended, bool HasProgram, IReadOnlyList<PanelIntervalDto> Intervals);

internal sealed record PanelHeatingDto(
    double? Temperature,
    bool BoilerActive,
    double? BoilerOnMinutes,
    bool Manual,
    PanelProgramDto Program,
    bool AntifreezeEnabled,
    double AntifreezeThreshold,
    double BandMin,
    double BandMax
);

internal sealed record PanelGateEventDto(string Title, string? Detail, DateTime At);

internal sealed record PanelGatesDto(
    bool Open,
    DateTime? OpenedAt,
    DateTime? ExpiresAt,
    int RateMax,
    int RateWindow,
    IReadOnlyList<long> AccessUsers,
    IReadOnlyList<PanelGateEventDto> Recent
);

internal sealed record PanelSensorDto(
    string Id,
    string Location,
    double Temperature,
    DateTime Timestamp,
    bool Informational,
    bool Fresh,
    string? Status,
    string? Error
);

internal sealed record PanelTemperaturesDto(
    double? Local,
    string LocalStatus,
    double? Operational,
    DateTime UpdatedAt,
    IReadOnlyList<PanelSensorDto> Sensors,
    IReadOnlyList<double> Trend
);

internal sealed record PanelUserDto(
    long Id,
    string? Name,
    IReadOnlyList<string> Entitlements,
    IReadOnlyList<string> Topics,
    bool IsAdmin,
    bool IsGateController,
    bool RuntimeOnly
);

internal sealed record PanelDeviceDto(
    string Id,
    string Name,
    string State,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    string? CreatedBy,
    DateTime? PairingExpiresAt
);

internal sealed record PanelUsageDto(double CostUsd, double WeeklyBudgetUsd, long Calls, long Tokens, long CachedTokens);

internal sealed record PanelEventDto(long Id, string Topic, string Title, string? Detail, DateTime At);

internal sealed record PanelUpdateDto(
    string? Result,
    string? FromSha,
    string? ToSha,
    DateTime? LastRunUtc,
    string? Reason,
    DateTime? BuiltAtUtc
);

internal sealed record PanelWeatherDto(
    double Temperature,
    int Code,
    bool IsDay,
    int? Humidity,
    double? WindKmh,
    double? Min,
    double? Max,
    DateTime? Sunrise,
    DateTime? Sunset,
    DateTime UpdatedAt
);

internal sealed record PanelSystemDto(long UptimeSeconds, long MemoryMb, string Environment, string Version);

internal sealed record PanelSessionDto(string Mode, string? DeviceName, string? DeviceId, string Version);

internal sealed record PanelOverviewDto(
    DateTime ServerTime,
    PanelSessionDto Session,
    PanelHealthDto Health,
    PanelHeatingDto Heating,
    PanelGatesDto Gates,
    PanelTemperaturesDto Temperatures,
    IReadOnlyList<PanelUserDto> Users,
    IReadOnlyList<PanelDeviceDto> Devices,
    PanelUsageDto Usage,
    IReadOnlyList<PanelAlertDto> Alerts,
    IReadOnlyList<PanelEventDto> Events,
    PanelUpdateDto Update,
    PanelSystemDto System,
    PanelWeatherDto? Weather
);

internal sealed record PanelWriteResult(bool Ok, PanelOverviewDto Overview);
