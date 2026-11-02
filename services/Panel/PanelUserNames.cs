using System.Collections.Concurrent;
using PodereBot.Services.Hosted;
using Telegram.Bot;

namespace PodereBot.Services.Panel;

/// <summary>
/// Resolves the display name of a configured user from Telegram, once, with a small cache.
/// Names stored by an admin in users.json win; this only fills the gaps for seed users.
/// </summary>
internal sealed class PanelUserNames(BotHostedService bot, ILogger<PanelUserNames> logger)
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<long, Entry> cache = new();

    /// <summary>
    /// Never blocks a request on Telegram: a cache miss returns null immediately and the name is
    /// fetched in the background, so a user toggle or a poll is not slowed down by GetChat.
    /// </summary>
    public Task<string?> ResolveAsync(long id, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        if (cache.TryGetValue(id, out var cached))
        {
            if (cached.ExpiresAt > now)
                return Task.FromResult(cached.Name);

            cache[id] = new Entry(cached.Name, now.Add(FailureTtl));
            _ = WarmAsync(id);
            return Task.FromResult(cached.Name);
        }

        if (cache.TryAdd(id, new Entry(null, now.Add(FailureTtl))))
            _ = WarmAsync(id);
        return Task.FromResult<string?>(null);
    }

    private async Task WarmAsync(long id)
    {
        var name = await FetchAsync(id, CancellationToken.None);
        cache[id] = new Entry(name, DateTime.UtcNow.Add(name == null ? FailureTtl : SuccessTtl));
    }

    private async Task<string?> FetchAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var chat = await bot.Client.GetChat(id, cancellationToken: timeout.Token);
            var name = $"{chat.FirstName} {chat.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(chat.Username))
                name = $"@{chat.Username}";
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            logger.LogDebug("unable to resolve the Telegram name of {id}: {m}", id, ex.Message);
            return null;
        }
    }

    private sealed record Entry(string? Name, DateTime ExpiresAt);
}
