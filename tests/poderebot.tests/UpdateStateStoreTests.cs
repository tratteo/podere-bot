using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class UpdateStateStoreTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), $"poderebot-update-{Guid.NewGuid():N}");
    private readonly UpdateStateStore store;

    public UpdateStateStoreTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Join(directory, "update-state.json"),
            """
            {
              "lastRunUtc": "2026-11-03T09:42:55Z",
              "result": "updated",
              "fromSha": "aaaaaaa1111111111111111111111111111111111",
              "toSha": "74730cc1b5501d01e9ac264b809d6ceb613156ac",
              "failedSha": "",
              "reason": "applied 74730cc"
            }
            """
        );
        store = new UpdateStateStore(
            new TestEnvironment { ContentRootPath = directory },
            NullLogger<UpdateStateStore>.Instance
        );
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
    public void ReadsTheCamelCaseStateWrittenByTheUpdater()
    {
        var state = store.Read();

        Assert.Equal("updated", state.Result);
        Assert.Equal("74730cc", state.ToSha);
        Assert.Equal("aaaaaaa", state.FromSha);
        Assert.NotNull(state.LastRunUtc);
        Assert.Equal("applied 74730cc", state.Reason);
    }

    [Fact]
    public void ReadsTheBuildTimeFromVersionJsonInTheContentRoot()
    {
        File.WriteAllText(
            Path.Join(directory, "version.json"),
            """{"sha":"74730cc1b5501d01e9ac264b809d6ceb613156ac","builtAtUtc":"2026-11-03T10:41:00Z","source":"github"}"""
        );

        var state = store.Read();

        Assert.NotNull(state.BuiltAtUtc);
        Assert.Equal(DateTimeKind.Utc, state.BuiltAtUtc!.Value.Kind);
    }

    [Fact]
    public void VersionIsThePulledSha()
    {
        Assert.Equal("74730cc", store.Version);
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
