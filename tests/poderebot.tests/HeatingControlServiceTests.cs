using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class HeatingControlServiceTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), $"poderebot-heating-{Guid.NewGuid():N}");
    private readonly Database db;
    private readonly HeatingControlService service;

    public HeatingControlServiceTests()
    {
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Pins:Heating"] = "18", ["Heating:TempSensorsToleranceSeconds"] = "60" })
            .Build();
        var environment = new TestEnvironment { ContentRootPath = directory };
        db = new Database(environment, NullLogger<Database>.Instance);
        var temperature = new MockTemperatureDriver(db);
        var driver = new HeatingDriver(NullLogger<HeatingDriver>.Instance, new MockPinDriver(NullLogger<MockPinDriver>.Instance), configuration, temperature, db);
        service = new HeatingControlService(driver, db, NullLogger<HeatingControlService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // Temp folder.
        }
    }

    [Fact]
    public void ProgramIsSavedWithHoursMinutesAndTemperature()
    {
        service.SetProgram([new HeatingIntervalInput(6, 30, 9, 0, 20.5)]);

        var program = db.Data.HeatingProgram;
        Assert.NotNull(program);
        var interval = Assert.Single(program!.Intervals);
        Assert.Equal(6, interval.HoursFrom);
        Assert.Equal(30, interval.MinutesFrom);
        Assert.Equal(9, interval.HoursTo);
        Assert.Equal(20.5, interval.Temperature, 3);
    }

    [Fact]
    public void OverlappingIntervalsAreRefused()
    {
        var error = Assert.Throws<PanelValidationException>(() =>
            service.SetProgram([
                new HeatingIntervalInput(6, 0, 9, 0, 20),
                new HeatingIntervalInput(8, 0, 10, 0, 21),
            ])
        );

        Assert.Contains("sovrapp", error.Message);
        Assert.Null(db.Data.HeatingProgram);
    }

    [Fact]
    public void IntervalMustStartBeforeItEnds()
    {
        Assert.Throws<PanelValidationException>(() =>
            service.SetProgram([new HeatingIntervalInput(10, 0, 9, 0, 20)])
        );
    }

    [Fact]
    public void TemperatureMustStayInRange()
    {
        Assert.Throws<PanelValidationException>(() =>
            service.SetProgram([new HeatingIntervalInput(6, 0, 9, 0, 40)])
        );
    }

    [Fact]
    public void ClearingTheProgramRemovesIt()
    {
        service.SetProgram([new HeatingIntervalInput(6, 0, 9, 0, 20)]);
        service.ClearProgram();

        Assert.Null(db.Data.HeatingProgram);
    }

    [Fact]
    public void ManualBoilerSwitchIsRefusedInsideAnActiveInterval()
    {
        service.SetProgram([new HeatingIntervalInput(0, 0, 23, 59, 20)]);

        var error = Assert.Throws<PanelValidationException>(() => service.SetBoiler(true));
        Assert.Contains("Programma in corso", error.Message);
        Assert.False(db.Data.ManualHeatingActive);
    }

    [Fact]
    public void ManualBoilerSwitchWorksWhenTheProgramIsSuspended()
    {
        service.SetProgram([new HeatingIntervalInput(0, 0, 23, 59, 20)]);
        service.SetSuspended(true);

        service.SetBoiler(true);

        Assert.True(db.Data.ManualHeatingActive);
    }

    private sealed class TestEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PodereBot.Tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
