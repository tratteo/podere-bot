using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class GateAccessTests
{
    [Fact]
    public void SharedWindowIsOpenOnlyBeforeExpiry()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local);

        Assert.True(GateAccessPolicy.IsOpen(now.AddMinutes(1), now));
        Assert.False(GateAccessPolicy.IsOpen(now, now));
        Assert.False(GateAccessPolicy.IsOpen(now.AddMinutes(-1), now));
        Assert.False(GateAccessPolicy.IsOpen(null, now));
    }

    [Fact]
    public void SharedWindowExpiresOnceTheDeadlinePasses()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local);

        Assert.True(GateAccessPolicy.ShouldExpire(now.AddMinutes(-1), now));
        Assert.True(GateAccessPolicy.ShouldExpire(now, now));
        Assert.False(GateAccessPolicy.ShouldExpire(now.AddMinutes(1), now));
        Assert.False(GateAccessPolicy.ShouldExpire(null, now));
    }

    [Fact]
    public void LimiterAllowsUpToTheConfiguredNumberOfOpenings()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new GateRateLimiter(new GateRateLimitOptions(3, 10), () => now);

        Assert.True(limiter.TryConsume(1, out _));
        Assert.True(limiter.TryConsume(1, out _));
        Assert.True(limiter.TryConsume(1, out _));
        Assert.False(limiter.TryConsume(1, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void LimiterIsPerUser()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new GateRateLimiter(new GateRateLimitOptions(1, 10), () => now);

        Assert.True(limiter.TryConsume(1, out _));
        Assert.False(limiter.TryConsume(1, out _));
        Assert.True(limiter.TryConsume(2, out _));
    }

    [Fact]
    public void LimiterWindowSlides()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new GateRateLimiter(new GateRateLimitOptions(2, 10), () => now);

        Assert.True(limiter.TryConsume(1, out _));
        now = now.AddMinutes(5);
        Assert.True(limiter.TryConsume(1, out _));
        Assert.False(limiter.TryConsume(1, out _));

        now = now.AddMinutes(6);
        Assert.True(limiter.TryConsume(1, out _));
    }

    [Fact]
    public void RetryAfterShrinksAsTheWindowMoves()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new GateRateLimiter(new GateRateLimitOptions(1, 10), () => now);

        Assert.True(limiter.TryConsume(1, out _));
        Assert.False(limiter.TryConsume(1, out var first));
        now = now.AddMinutes(8);
        Assert.False(limiter.TryConsume(1, out var second));
        Assert.True(second < first);
    }

    [Fact]
    public void DefaultRateLimitIsThreeOpeningsInTenMinutes()
    {
        Assert.Equal(3, GateRateLimitOptions.Default.MaxOpenings);
        Assert.Equal(10, GateRateLimitOptions.Default.WindowMinutes);
    }
}
