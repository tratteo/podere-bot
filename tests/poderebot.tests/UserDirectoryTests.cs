using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class UserDirectoryTests
{
    private static IConfiguration BuildConfig(params (long Id, string[] Entitlements, string[] Topics)[] users)
    {
        var data = new Dictionary<string, string?>();
        for (var i = 0; i < users.Length; i++)
        {
            data[$"Users:{i}:Id"] = users[i].Id.ToString(CultureInfo.InvariantCulture);
            for (var e = 0; e < users[i].Entitlements.Length; e++)
                data[$"Users:{i}:Entitlements:{e}"] = users[i].Entitlements[e];
            for (var t = 0; t < users[i].Topics.Length; t++)
                data[$"Users:{i}:Topics:{t}"] = users[i].Topics[t];
        }
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static UserDirectory Create(IConfiguration configuration, TempRoot root) =>
        new(configuration, root.Environment, NullLogger<UserDirectory>.Instance);

    [Fact]
    public void SeedUsersAreResolved()
    {
        using var root = new TempRoot();
        var users = Create(BuildConfig((1, [Entitlement.Admin], [])), root);

        Assert.True(users.IsAdmin(1));
        Assert.True(users.CanControlGates(1));
        Assert.Equal([1L], users.AdminIds());
    }

    [Fact]
    public void UnknownUserIsANormalUser()
    {
        using var root = new TempRoot();
        var users = Create(BuildConfig((1, [Entitlement.Admin], [])), root);

        Assert.Null(users.Find(999));
        Assert.False(users.IsAdmin(999));
        Assert.False(users.CanControlGates(999));
        Assert.False(users.IsSubscribed(999, NotificationTopic.Gates));
    }

    [Fact]
    public void AdminTopicsAreExplicit()
    {
        using var root = new TempRoot();
        var users = Create(
            BuildConfig(
                (1, [Entitlement.Admin], [NotificationTopic.Heating]),
                (2, [Entitlement.Admin], [])
            ),
            root
        );

        Assert.True(users.IsSubscribed(1, NotificationTopic.Heating));
        Assert.False(users.IsSubscribed(1, NotificationTopic.Gates));
        Assert.False(users.IsSubscribed(1, NotificationTopic.Technical));
        Assert.False(users.IsSubscribed(2, NotificationTopic.Heating));
    }

    [Fact]
    public void GateControllerCanOpenGatesButIsNotAdmin()
    {
        using var root = new TempRoot();
        var users = Create(BuildConfig((2, [Entitlement.GateController], [])), root);

        Assert.True(users.CanControlGates(2));
        Assert.False(users.IsAdmin(2));
        Assert.Empty(users.AdminIds());
    }

    [Fact]
    public void NormalUserIsNotSubscribedToTopics()
    {
        using var root = new TempRoot();
        var users = Create(BuildConfig((3, [], [])), root);

        Assert.NotNull(users.Find(3));
        Assert.False(users.IsAdmin(3));
        Assert.False(users.CanControlGates(3));
        Assert.False(users.IsSubscribed(3, NotificationTopic.Heating));
    }

    [Fact]
    public void RuntimeOverrideWinsOverSeed()
    {
        using var root = new TempRoot();
        var users = Create(BuildConfig((1, [Entitlement.GateController], [])), root);

        users.Upsert(new UserEntry { Id = 1, Entitlements = [Entitlement.Admin], Topics = [NotificationTopic.Heating] });

        Assert.True(users.IsAdmin(1));
        Assert.Contains(NotificationTopic.Heating, users.Find(1)!.Topics);
    }

    [Fact]
    public void RemovedSeedUserIsHidden()
    {
        using var root = new TempRoot();
        var users = Create(BuildConfig((1, [Entitlement.Admin], []), (2, [Entitlement.Admin], [])), root);

        Assert.True(users.Remove(1));

        Assert.Null(users.Find(1));
        Assert.Equal([2L], users.AdminIds());
    }

    [Fact]
    public void RuntimeUsersSurviveARestart()
    {
        using var root = new TempRoot();
        var configuration = BuildConfig((1, [Entitlement.Admin], []));

        var first = Create(configuration, root);
        first.Upsert(new UserEntry { Id = 7, Entitlements = [Entitlement.GateController], Topics = [NotificationTopic.Gates] });

        var second = Create(configuration, root);

        Assert.True(second.CanControlGates(7));
        Assert.Equal([NotificationTopic.Gates], second.Find(7)!.Topics);
    }

    [Fact]
    public void SubscribersFollowExplicitTopics()
    {
        using var root = new TempRoot();
        var users = Create(
            BuildConfig(
                (1, [Entitlement.Admin], [NotificationTopic.Heating]),
                (2, [], [NotificationTopic.Heating]),
                (3, [], [NotificationTopic.Gates])
            ),
            root
        );

        var heating = users.Subscribers(NotificationTopic.Heating);
        var technical = users.Subscribers(NotificationTopic.Technical);

        Assert.Equal([1L, 2L], heating);
        Assert.Empty(technical);
    }

    [Fact]
    public void LegacyAdminTopicsAreMaterialisedOnce()
    {
        using var root = new TempRoot();
        var path = Path.Join(root.Environment.ContentRootPath, UserDirectory.RuntimeFileName);
        File.WriteAllText(
            path,
            """
            {
              "Users": [
                { "Id": 1, "Entitlements": ["admin"], "Topics": [] }
              ],
              "Removed": []
            }
            """
        );

        var first = Create(BuildConfig(), root);

        foreach (var topic in NotificationTopic.All)
            Assert.True(first.IsSubscribed(1, topic));

        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal(UsersFile.CurrentVersion, document.RootElement.GetProperty("Version").GetInt32());

        first.Upsert(
            new UserEntry
            {
                Id = 1,
                Entitlements = [Entitlement.Admin],
                Topics = [NotificationTopic.Heating],
            }
        );

        var second = Create(BuildConfig(), root);

        Assert.True(second.IsSubscribed(1, NotificationTopic.Heating));
        Assert.False(second.IsSubscribed(1, NotificationTopic.Gates));
        Assert.False(second.IsSubscribed(1, NotificationTopic.Technical));
    }

    [Fact]
    public void LegacyAdminsFileIsMigratedAndRemoved()
    {
        using var root = new TempRoot();
        File.WriteAllText(
            Path.Join(root.Environment.ContentRootPath, UserDirectory.LegacyFileName),
            """
            {
              "RuntimeAdmins": [ 10, 11 ],
              "RemovedAdmins": [ 12 ],
              "AdminNames": { "10": "Anna" }
            }
            """
        );

        var users = Create(BuildConfig((12, [Entitlement.Admin], [])), root);

        Assert.True(users.IsAdmin(10));
        Assert.True(users.IsAdmin(11));
        Assert.Null(users.Find(12));
        Assert.Equal("Anna", users.Find(10)!.Name);

        Assert.False(File.Exists(Path.Join(root.Environment.ContentRootPath, UserDirectory.LegacyFileName)));
        Assert.True(File.Exists(Path.Join(root.Environment.ContentRootPath, UserDirectory.RuntimeFileName)));
    }

    private sealed class TempRoot : IDisposable
    {
        private readonly string path = Path.Join(Path.GetTempPath(), $"poderebot-tests-{Guid.NewGuid():N}");

        public TempRoot()
        {
            Directory.CreateDirectory(path);
            Environment = new TestEnvironment { ContentRootPath = path };
        }

        public TestEnvironment Environment { get; }

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
