using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

/// <summary>
/// Guards the migration contract: the ids that were admins before the refactor must still be
/// admins, resolved from the real <c>appsettings.json</c> seed shipped with the application.
/// </summary>
public sealed class SeedConfigurationTests
{
    private static readonly long[] legacyAdminIds = [962154266, 7485996294, 529803876, 697244957];

    private static IConfiguration LoadShippedConfiguration()
    {
        var assembly = typeof(DatabaseSchema).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("appsettings.json"));

        Assert.NotNull(resource);

        using var stream = assembly.GetManifestResourceStream(resource!)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var path = Path.Join(Path.GetTempPath(), $"poderebot-appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            return new ConfigurationBuilder().AddJsonFile(path).Build();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyAdminsAreStillAdmins()
    {
        using var root = new TempRoot();
        var users = new UserDirectory(LoadShippedConfiguration(), root, NullLogger<UserDirectory>.Instance);

        foreach (var id in legacyAdminIds)
        {
            Assert.True(users.IsAdmin(id), $"user {id} is no longer an admin");
            Assert.True(users.CanControlGates(id), $"user {id} can no longer open the gates");
            foreach (var topic in NotificationTopic.All)
                Assert.True(users.IsSubscribed(id, topic), $"user {id} is not subscribed to {topic}");
        }

        Assert.Equal(legacyAdminIds.OrderBy(id => id), users.AdminIds().OrderBy(id => id));
    }

    [Fact]
    public void SeedCarriesNoLeftoverAdminsKey()
    {
        var configuration = LoadShippedConfiguration();

        Assert.Empty(configuration.GetSection("Admins").GetChildren());
        Assert.Equal(legacyAdminIds.Length, configuration.GetSection("Users").GetChildren().Count());
    }

    private sealed class TempRoot : IHostEnvironment, IDisposable
    {
        private readonly string path = Path.Join(Path.GetTempPath(), $"poderebot-seed-{Guid.NewGuid():N}");

        public TempRoot()
        {
            Directory.CreateDirectory(path);
            ContentRootPath = path;
        }

        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PodereBot.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

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
}
