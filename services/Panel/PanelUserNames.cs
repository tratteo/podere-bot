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

    public async Task<string?> ResolveAsync(long id, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(id, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Name;

        var name = await FetchAsync(id, cancellationToken);
        cache[id] = new Entry(name, DateTime.UtcNow.Add(name == null ? FailureTtl : SuccessTtl));
        return name;
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
