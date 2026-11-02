using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class StartupGateTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), $"poderebot-startup-{Guid.NewGuid():N}");
    private readonly string path;

    public StartupGateTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Join(directory, "startup.json");
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
    public void MissingMarkerAnnounces()
    {
        Assert.True(Create().ShouldAnnounce);
    }

    [Fact]
    public void RecentMarkerSuppressesTheSummary()
    {
        WriteMarker(DateTime.UtcNow.AddMinutes(-2));
        Assert.False(Create().ShouldAnnounce);
    }

    [Fact]
    public void OldMarkerAnnouncesAgain()
    {
        WriteMarker(DateTime.UtcNow.AddMinutes(-11));
        Assert.True(Create().ShouldAnnounce);
    }

    [Fact]
    public void MarkerInTheFutureAnnouncesBecauseTheClockCanBeBehind()
    {
        // The Pi has no RTC: at boot the clock can predate the last announce.
        WriteMarker(DateTime.UtcNow.AddMinutes(5));
        Assert.True(Create().ShouldAnnounce);
    }

    [Fact]
    public void MarkReadyDoesNotWriteTheMarkerAndMarkAnnouncedDoes()
    {
        var gate = Create();

        gate.MarkReady();
        Assert.True(gate.Ready);
        Assert.False(File.Exists(path));

        gate.MarkAnnounced();
        Assert.True(File.Exists(path));
        Assert.False(gate.ShouldAnnounce);
    }

    private void WriteMarker(DateTime value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { LastAnnounceUtc = value }));

    private StartupGate Create() =>
        new(new TestEnvironment { ContentRootPath = directory }, NullLogger<StartupGate>.Instance);

    private sealed class TestEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PodereBot.Tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
