using PodereBot.Services.Hosted;
using PodereBot.Services.Panel;

namespace PodereBot.Services;

/// <summary>
/// Enforces the gate opening rate limit for unprivileged users and records who used the gates
/// while a shared access window was open, so the expiry notice can reach them.
/// </summary>
internal sealed class GateAccessService(
    IConfiguration configuration,
    Database db,
    UserDirectory users,
    ILogger<GateAccessService> logger
)
{
    private readonly GateRateLimitOptions options = OptionsReader.ReadGateRateLimit(configuration);
    private readonly GateRateLimiter limiter = new(OptionsReader.ReadGateRateLimit(configuration));

    public GateRateLimitOptions Options => options;

    public bool IsPrivileged(long userId) => users.CanControlGates(userId);

    /// <summary>
    /// Admins and gate controllers are never limited. Everyone else may open the gates at most
    /// <see cref="GateRateLimitOptions.MaxOpenings"/> times per <see cref="GateRateLimitOptions.WindowMinutes"/>.
    /// </summary>
    public bool TryRegisterOpening(long userId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;

        if (IsPrivileged(userId))
            return true;

        if (!limiter.TryConsume(userId, out retryAfter))
            return false;

        RecordUse(userId);
        return true;
    }

    public IReadOnlyList<long> AccessLog() => db.Data.GateAccessUsers;

    /// <summary>Removes the access log and clears the shared window.</summary>
    public void CloseWindow()
    {
        db.Edit(d =>
        {
            d.GatesOpenAccessExpirationDate = null;
            d.GateAccessUsers = [];
        });
    }

    private void RecordUse(long userId)
    {
        if (IsPrivileged(userId))
            return;

        if (!GateAccessPolicy.IsOpen(db.Data.GatesOpenAccessExpirationDate, DateTime.Now))
            return;

        db.Edit(d =>
        {
            if (!d.GateAccessUsers.Contains(userId))
                d.GateAccessUsers.Add(userId);
        });
        logger.LogDebug("recorded gate use by {id} inside the shared window", userId);
    }

    /// <summary>
    /// The panel is not a chat: its synthetic id is recorded for the panel log but it must never
    /// receive a notification, so the expiry daemon ignores it.
    /// </summary>
    public void RecordPanelUse()
    {
        if (!GateAccessPolicy.IsOpen(db.Data.GatesOpenAccessExpirationDate, DateTime.Now))
            return;

        db.Edit(d =>
        {
            if (!d.GateAccessUsers.Contains(PanelIdentity.UserId))
                d.GateAccessUsers.Add(PanelIdentity.UserId);
        });
    }
}

/// <summary>Notifies the users who had access when the shared window expires, then closes it.</summary>
internal sealed class GateAccessExpiryDaemon(
    IConfiguration configuration,
    Database db,
    GateAccessService gateAccess,
    BotHostedService bot,
    ILogger<GateAccessExpiryDaemon> logger
) : BackgroundService
{
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(
        Math.Max(5, configuration.GetValue<int?>("Gates:ExpiryPollSeconds") ?? 30)
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(pollInterval);
        do
        {
            try
            {
                var expiration = db.Data.GatesOpenAccessExpirationDate;
                if (!GateAccessPolicy.ShouldExpire(expiration, DateTime.Now))
                    continue;

                var hadAccess = gateAccess.AccessLog().Where(id => id > 0).ToList();
                gateAccess.CloseWindow();

                if (hadAccess.Count > 0)
                {
                    await bot.NotifyUsersAsync(
                        hadAccess,
                        NotificationTopic.Gates,
                        "🔒 L'accesso ai cancelli è <b>scaduto</b>. Se ti serve di nuovo, chiedilo a un amministratore."
                    );
                }
                else
                {
                    await bot.NotifyTopicAsync(
                        NotificationTopic.Gates,
                        "🔒 L'accesso ai cancelli è <b>scaduto</b> e nessuno ne ha usufruito."
                    );
                }

                logger.LogInformation("gate access window expired, {c} users notified", hadAccess.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning("gate expiry check failed: {m}", ex.Message);
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
