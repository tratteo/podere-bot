namespace PodereBot.Services;

/// <summary>Pure rules for the shared gate access window.</summary>
internal static class GateAccessPolicy
{
    public static bool IsOpen(DateTime? expiration, DateTime now) => expiration != null && expiration > now;

    public static bool ShouldExpire(DateTime? expiration, DateTime now) => expiration != null && expiration <= now;
}

/// <summary>
/// Sliding-window limiter. Time is injectable so the behaviour is testable without waiting.
/// </summary>
internal sealed class GateRateLimiter
{
    private readonly GateRateLimitOptions options;
    private readonly Func<DateTime> clock;
    private readonly object sync = new();
    private readonly Dictionary<long, List<DateTime>> openings = [];

    public GateRateLimiter(GateRateLimitOptions options, Func<DateTime>? clock = null)
    {
        this.options = options;
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool TryConsume(long userId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;

        var window = TimeSpan.FromMinutes(Math.Max(1, options.WindowMinutes));
        var max = Math.Max(1, options.MaxOpenings);
        var now = clock();

        lock (sync)
        {
            if (!openings.TryGetValue(userId, out var stamps))
            {
                stamps = [];
                openings[userId] = stamps;
            }

            stamps.RemoveAll(stamp => now - stamp >= window);

            if (stamps.Count >= max)
            {
                retryAfter = window - (now - stamps[0]);
                return false;
            }

            stamps.Add(now);
            return true;
        }
    }
}
