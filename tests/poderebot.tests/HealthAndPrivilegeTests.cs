using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class HealthAndPrivilegeTests
{
    // ===== Throttled decoding used by the "Salute" section =====

    [Theory]
    [InlineData("0x0", "ok")]
    [InlineData("0X0", "ok")]
    [InlineData("0x1", "sottotensione")]
    [InlineData("0x4", "throttling")]
    [InlineData("0x8", "limite termico")]
    public void ThrottleFlagsAreDecoded(string raw, string expected)
    {
        Assert.Equal(expected, HealthService.DecodeThrottled(raw));
    }

    [Fact]
    public void CombinedThrottleFlagsListEveryIssue()
    {
        // 0x50005 = under-voltage now + throttled now + under-voltage occurred + frequency capped occurred
        var decoded = HealthService.DecodeThrottled("0x50005");

        Assert.Contains("sottotensione", decoded);
        Assert.Contains("throttling", decoded);
        Assert.Contains("pregressa", decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("non-un-numero")]
    public void UnknownThrottleValueIsReportedAsSuch(string? raw)
    {
        Assert.Equal("sconosciuto", HealthService.DecodeThrottled(raw));
    }

    [Fact]
    public void HealthIsOkOnlyWhenTelegramAndDatabaseAreBothOk()
    {
        Assert.True(new HealthReport(true, true, "ok", null).Ok);
        Assert.False(new HealthReport(false, true, "ok", null).Ok);
        Assert.False(new HealthReport(true, false, "ok", null).Ok);
        Assert.False(new HealthReport(false, false, "ok", null).Ok);
    }

    // ===== Rate limit never applies to admin or gate_controller =====

    private static GateAccessService CreateService((long Id, string[] Entitlements)[] users)
    {
        var data = new Dictionary<string, string?>();
        for (var i = 0; i < users.Length; i++)
        {
            data[$"Users:{i}:Id"] = users[i].Id.ToString();
            for (var e = 0; e < users[i].Entitlements.Length; e++)
                data[$"Users:{i}:Entitlements:{e}"] = users[i].Entitlements[e];
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        var contentRoot = Path.Join(Path.GetTempPath(), $"poderebot-priv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        var directory = new UserDirectory(
            configuration,
            new TestEnvironment { ContentRootPath = contentRoot },
            NullLogger<UserDirectory>.Instance
        );
        var db = new Database(new TestEnvironment { ContentRootPath = contentRoot }, NullLogger<Database>.Instance);

        return new GateAccessService(configuration, db, directory, NullLogger<GateAccessService>.Instance);
    }

    [Fact]
    public void AdminAndGateControllerArePrivileged()
    {
        var service = CreateService(
            [(1, [Entitlement.Admin]), (2, [Entitlement.GateController]), (3, [])]
        );

        Assert.True(service.IsPrivileged(1));
        Assert.True(service.IsPrivileged(2));
        Assert.False(service.IsPrivileged(3));
        Assert.False(service.IsPrivileged(999));
    }

    [Fact]
    public void PrivilegedUsersNeverHitTheLimit()
    {
        var service = CreateService([(1, [Entitlement.Admin]), (2, [Entitlement.GateController])]);

        for (var i = 0; i < 50; i++)
        {
            Assert.True(service.TryRegisterOpening(1, out var adminRetry));
            Assert.Equal(TimeSpan.Zero, adminRetry);
            Assert.True(service.TryRegisterOpening(2, out var controllerRetry));
            Assert.Equal(TimeSpan.Zero, controllerRetry);
        }
    }

    [Fact]
    public void NormalUserIsLimited()
    {
        var service = CreateService([(3, [])]);

        // The default limit is three openings per ten minutes.
        Assert.True(service.TryRegisterOpening(3, out _));
        Assert.True(service.TryRegisterOpening(3, out _));
        Assert.True(service.TryRegisterOpening(3, out _));
        Assert.False(service.TryRegisterOpening(3, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
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
