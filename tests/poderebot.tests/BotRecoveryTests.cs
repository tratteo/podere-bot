using PodereBot.Services.Hosted;
using Xunit;

namespace PodereBot.Tests;

public sealed class BotRecoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(6);
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShortBlipDoesNotAnnounce()
    {
        Assert.False(BotHostedService.ShouldAnnounceRecovery(Now.AddMinutes(-2), Now, Timeout));
    }

    [Fact]
    public void OutageAtLeastAsLongAsTheTimeoutAnnounces()
    {
        Assert.True(BotHostedService.ShouldAnnounceRecovery(Now.AddMinutes(-6), Now, Timeout));
        Assert.True(BotHostedService.ShouldAnnounceRecovery(Now.AddHours(-3), Now, Timeout));
    }

    [Fact]
    public void NoKnownFailureDoesNotAnnounce()
    {
        Assert.False(BotHostedService.ShouldAnnounceRecovery(null, Now, Timeout));
    }
}
