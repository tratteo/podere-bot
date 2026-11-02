using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class RuntimeFileMigrationTests
{
    [Fact]
    public void MigrationMovesTheFileOutOfThePublishFolder()
    {
        using var root = new TempTree();
        var publish = root.Dir("build");
        var content = root.Dir("repo");
        File.WriteAllText(Path.Join(publish, "db.json"), """{"GatesOpenAccessExpirationDate":null}""");

        var migrated = RuntimeFile.Migrate("db.json", content, publish, NullLogger.Instance);

        Assert.True(migrated);
        Assert.True(File.Exists(Path.Join(content, "db.json")));
        Assert.False(File.Exists(Path.Join(publish, "db.json")));
    }

    [Fact]
    public void MigrationNeverOverwritesTheContentRootCopy()
    {
        using var root = new TempTree();
        var publish = root.Dir("build");
        var content = root.Dir("repo");
        File.WriteAllText(Path.Join(publish, "db.json"), """{"source":"publish"}""");
        File.WriteAllText(Path.Join(content, "db.json"), """{"source":"root"}""");

        var migrated = RuntimeFile.Migrate("db.json", content, publish, NullLogger.Instance);

        Assert.False(migrated);
        Assert.Contains("root", File.ReadAllText(Path.Join(content, "db.json")));
    }

    [Fact]
    public void MigrationDoesNothingWithoutASource()
    {
        using var root = new TempTree();
        var publish = root.Dir("build");
        var content = root.Dir("repo");

        var migrated = RuntimeFile.Migrate("db.json", content, publish, NullLogger.Instance);

        Assert.False(migrated);
        Assert.False(File.Exists(Path.Join(content, "db.json")));
    }

    [Fact]
    public void MigrationDoesNothingWhenBothPathsAreTheSame()
    {
        using var root = new TempTree();
        var content = root.Dir("repo");
        File.WriteAllText(Path.Join(content, "db.json"), "{}");

        var migrated = RuntimeFile.Migrate("db.json", content, content, NullLogger.Instance);

        Assert.False(migrated);
        Assert.True(File.Exists(Path.Join(content, "db.json")));
    }

    [Fact]
    public void DatabaseLoadsRuntimeStateFromTheContentRootAfterMigration()
    {
        using var root = new TempTree();
        var publish = root.Dir("build");
        var content = root.Dir("repo");
        File.WriteAllText(
            Path.Join(publish, "db.json"),
            """
            {
              "GatesOpenAccessExpirationDate": "2026-12-01T08:30:00",
              "HeatingProgram": null,
              "GateAccessUsers": [ 11, 22 ]
            }
            """
        );

        var environment = new TestEnvironment { ContentRootPath = content };
        var database = new Database(environment, NullLogger<Database>.Instance, publish);

        Assert.Equal(Path.Join(content, "db.json"), database.Path);
        Assert.False(File.Exists(Path.Join(publish, "db.json")));
        Assert.Equal([11L, 22L], database.Data.GateAccessUsers);
        Assert.Equal(new DateTime(2026, 12, 1, 8, 30, 0), database.Data.GatesOpenAccessExpirationDate);
    }

    [Fact]
    public void DatabaseKeepsWorkingWithoutAnyExistingFile()
    {
        using var root = new TempTree();
        var publish = root.Dir("build");
        var content = root.Dir("repo");

        var environment = new TestEnvironment { ContentRootPath = content };
        var database = new Database(environment, NullLogger<Database>.Instance, publish);

        Assert.Empty(database.Data.GateAccessUsers);
        Assert.Null(database.Data.GatesOpenAccessExpirationDate);
    }

    [Fact]
    public void WritesGoToTheContentRoot()
    {
        using var root = new TempTree();
        var publish = root.Dir("build");
        var content = root.Dir("repo");
        var environment = new TestEnvironment { ContentRootPath = content };
        var database = new Database(environment, NullLogger<Database>.Instance, publish);

        database.Edit(d => d.GateAccessUsers.Add(7));

        Assert.True(File.Exists(Path.Join(content, "db.json")));
        Assert.False(File.Exists(Path.Join(publish, "db.json")));
        Assert.Contains("7", File.ReadAllText(Path.Join(content, "db.json")));
    }

    private sealed class TempTree : IDisposable
    {
        private readonly string path = Path.Join(Path.GetTempPath(), $"poderebot-migrate-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(path);

        public string Dir(string name)
        {
            var full = Path.Join(path, name);
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PodereBot.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
