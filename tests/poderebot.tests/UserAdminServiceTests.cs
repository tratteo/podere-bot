using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class UserAdminServiceTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), $"poderebot-users-{Guid.NewGuid():N}");
    private readonly UserAdminService service;

    public UserAdminServiceTests()
    {
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Users:0:Id"] = "1",
                    ["Users:0:Entitlements:0"] = "admin",
                    ["Users:0:Topics:0"] = "technical",
                }
            )
            .Build();

        var environment = new TestEnvironment { ContentRootPath = directory };
        var users = new UserDirectory(configuration, environment, NullLogger<UserDirectory>.Instance);
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopes = new AdminScopeNotifier(provider, NullLogger<AdminScopeNotifier>.Instance);
        service = new UserAdminService(users, scopes, NullLogger<UserAdminService>.Instance);
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
    public void DuplicateIdIsRefused()
    {
        var error = Assert.Throws<PanelConflictException>(() => service.Add(1, "Doppio", null, null));
        Assert.Contains("già configurato", error.Message);
    }

    [Fact]
    public void AdminGetsGateControllerImplicitlyAndAllTopics()
    {
        var entry = service.Add(2, "Anna", ["admin", "gate_controller"], null);

        Assert.True(entry.IsAdmin);
        Assert.True(entry.CanControlGates);
        Assert.DoesNotContain(Entitlement.GateController, entry.Entitlements);
        Assert.Equal(NotificationTopic.All, entry.Topics);
    }

    [Fact]
    public void LastAdminCannotBeDemoted()
    {
        var error = Assert.Throws<PanelConflictException>(() => service.Update(1, null, [], null));
        Assert.Contains("ultimo amministratore", error.Message);
    }

    [Fact]
    public void LastAdminCannotBeRemoved()
    {
        var error = Assert.Throws<PanelConflictException>(() => service.Remove(1));
        Assert.Contains("ultimo amministratore", error.Message);
    }

    [Fact]
    public void DemotingAnAdminIsAllowedWhenAnotherAdminExists()
    {
        service.Add(2, "Anna", ["admin"], ["heating"]);

        service.Update(1, null, [], ["technical"]);

        var user = service.All().Single(u => u.Id == 1);
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public void NullFieldsKeepTheCurrentValuesOnUpdate()
    {
        service.Add(2, "Luca", ["gate_controller"], ["gates"]);

        service.Update(2, "Luca Rossi", null, null);

        var user = service.All().Single(u => u.Id == 2);
        Assert.Equal("Luca Rossi", user.Name);
        Assert.Contains(Entitlement.GateController, user.Entitlements);
        Assert.Equal(["gates"], user.Topics);
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
