using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using PodereBot.Services;

namespace PodereBot.Lib.Api;

internal record GetStatusResponse
{
    public TimeSpan RunningTime { get; init; }
    public long MemoryKB { get; init; }
}

internal record PostTemperatureBody
{
    [Required]
    public float? Temperature { get; init; }

    [Required]
    public long? Timestamp { get; init; }

    [Required]
    public string? SensorId { get; init; }
    public string? BoardId { get; init; }

    [Required]
    public string? Location { get; init; }
    public string? Com { get; init; }

    public bool Informational { get; init; }
}

internal record TemperatureDto
{
    public required string Id { get; init; }
    public required string Location { get; init; }
    public required double Temperature { get; init; }
    public required DateTime Timestamp { get; init; }
    public bool Informational { get; init; }
}

internal record GetTemperaturesResponse
{
    public double? Local { get; init; }
    public string LocalStatus { get; init; } = "";
    public double? Operational { get; init; }
    public IReadOnlyList<TemperatureDto> External { get; init; } = [];
}

internal record HeatingIntervalDto
{
    public int HoursFrom { get; init; }
    public int MinutesFrom { get; init; }
    public int HoursTo { get; init; }
    public int MinutesTo { get; init; }
    public double Temperature { get; init; }
}

internal record GetHeatingResponse
{
    public double? Temperature { get; init; }
    public bool BoilerActive { get; init; }
    public bool ManualHeatingActive { get; init; }
    public bool ProgramSuspended { get; init; }
    public bool HasProgram { get; init; }
    public IReadOnlyList<HeatingIntervalDto> Intervals { get; init; } = [];
    public bool AntifreezeEnabled { get; init; }
    public double AntifreezeThresholdCelsius { get; init; }
}

internal record GetGatesResponse
{
    public DateTime? AccessExpiresAt { get; init; }
    public bool AccessOpen { get; init; }
    public IReadOnlyList<long> AccessUsers { get; init; } = [];
    public int RateLimitMaxOpenings { get; init; }
    public int RateLimitWindowMinutes { get; init; }
}

internal record GetHealthResponse
{
    public bool Ok { get; init; }
    public bool TelegramOk { get; init; }
    public bool DatabaseOk { get; init; }
    public string? Throttled { get; init; }
    public double? CpuTemperatureCelsius { get; init; }
}

internal class Api : IEndpoint
{
    public static DateTime FromUnixTimestamp(double timestamp)
    {
        DateTime origin = new(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        return origin.AddSeconds(timestamp);
    }

    public static async Task<Results<Ok, BadRequest>> PostTemperature(
        PostTemperatureBody body,
        ILogger<Program> logger,
        ITemperatureDriver temperatureDriver
    )
    {
        temperatureDriver.PostTemperatureReading(
            new TemperatureReading()
            {
                Id = body.SensorId ?? "",
                Temperature = (float)body.Temperature!,
                Timestamp = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds((int)body.Timestamp!),
                Location = body.Location!,
                Informational = body.Informational,
            }
        );

        await Task.CompletedTask;
        return TypedResults.Ok();
    }

    public static async Task<Results<Ok<GetStatusResponse>, BadRequest>> GetStatus(ILogger<Program> logger)
    {
        await Task.CompletedTask;
        Process currentProc = Process.GetCurrentProcess();
        var runningTime = DateTime.Now - currentProc.StartTime;
        var memory = currentProc.PrivateMemorySize64 / 1024;
        return TypedResults.Ok(new GetStatusResponse() { MemoryKB = memory, RunningTime = runningTime });
    }

    public static IResult GetHealth(HealthService health)
    {
        var report = health.Check();
        var body = new GetHealthResponse
        {
            Ok = report.Ok,
            TelegramOk = report.TelegramOk,
            DatabaseOk = report.DatabaseOk,
            Throttled = report.Throttled,
            CpuTemperatureCelsius = report.CpuTemperatureCelsius,
        };
        return TypedResults.Json(body, statusCode: report.Ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Ok<GetTemperaturesResponse>> GetTemperatures(ITemperatureDriver temperatureDriver)
    {
        var probe = await temperatureDriver.ProbeLocalTemperature();
        var readings = temperatureDriver.GetExternalTemperatureReadings();

        var operational = readings.Where(r => !r.Informational).Select(r => (double)r.Temperature).ToList();
        if (probe.Temperature.HasValue)
            operational.Add(probe.Temperature.Value);

        return TypedResults.Ok(
            new GetTemperaturesResponse
            {
                Local = probe.Temperature,
                LocalStatus = probe.Status.ToString(),
                Operational = operational.Count > 0 ? operational.Average() : null,
                External = readings
                    .Select(r => new TemperatureDto
                    {
                        Id = r.Id,
                        Location = r.Location,
                        Temperature = r.Temperature,
                        Timestamp = r.Timestamp,
                        Informational = r.Informational,
                    })
                    .ToList(),
            }
        );
    }

    public static async Task<Ok<GetHeatingResponse>> GetHeating(
        HeatingDriver heatingDriver,
        Database db,
        IConfiguration configuration
    )
    {
        var options = OptionsReader.ReadAntifreeze(configuration);
        var data = db.Data;
        var program = data.HeatingProgram;

        return TypedResults.Ok(
            new GetHeatingResponse
            {
                Temperature = await heatingDriver.GetOperationalTemperature(),
                BoilerActive = heatingDriver.IsBoilerActive(),
                ManualHeatingActive = data.ManualHeatingActive,
                ProgramSuspended = program?.IsSuspended ?? false,
                HasProgram = program != null,
                Intervals =
                    program?.Intervals
                        .Select(i => new HeatingIntervalDto
                        {
                            HoursFrom = i.HoursFrom,
                            MinutesFrom = i.MinutesFrom,
                            HoursTo = i.HoursTo,
                            MinutesTo = i.MinutesTo,
                            Temperature = i.Temperature,
                        })
                        .ToList()
                    ?? [],
                AntifreezeEnabled = options.Enabled,
                AntifreezeThresholdCelsius = options.ThresholdCelsius,
            }
        );
    }

    public static Ok<GetGatesResponse> GetGates(Database db, GateAccessService gateAccess)
    {
        var data = db.Data;
        var expiration = data.GatesOpenAccessExpirationDate;
        return TypedResults.Ok(
            new GetGatesResponse
            {
                AccessExpiresAt = expiration,
                AccessOpen = GateAccessPolicy.IsOpen(expiration, DateTime.Now),
                AccessUsers = data.GateAccessUsers,
                RateLimitMaxOpenings = gateAccess.Options.MaxOpenings,
                RateLimitWindowMinutes = gateAccess.Options.WindowMinutes,
            }
        );
    }

    public void MapEndpoint(WebApplication app)
    {
        var group = app.MapGroup("api");
        group
            .MapPost("/temperature", PostTemperature)
            .WithName("temperature")
            .WithDescription("Post a temperature sensor reading")
            .Validate<PostTemperatureBody>();

        group.MapGet("/status", GetStatus).WithName("status").WithDescription("Ping the status of the application");

        group
            .MapGet("/health", GetHealth)
            .WithName("health")
            .WithDescription("Deep health check: Telegram connection and database writability");

        group
            .MapGet("/temperatures", GetTemperatures)
            .WithName("temperatures")
            .WithDescription("Local probe and external temperature readings");

        group.MapGet("/heating", GetHeating).WithName("heating").WithDescription("Heating state and program");

        group.MapGet("/gates", GetGates).WithName("gates").WithDescription("Gate access window and rate limit");
    }
}
