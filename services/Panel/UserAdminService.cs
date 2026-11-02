using PodereBot.Services.Hosted;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace PodereBot.Services.Panel;

/// <summary>
/// Keeps the per-chat command scope in sync with the admin role, without letting a Telegram
/// failure break a local change: the panel must work while Telegram is unreachable.
/// </summary>
internal sealed class AdminScopeNotifier(IServiceProvider services, ILogger<AdminScopeNotifier> logger)
{
    public async Task SyncAsync(long id, bool isAdmin)
    {
        var bot = services.GetService<BotHostedService>();
        if (bot == null)
            return;

        try
        {
            if (isAdmin)
            {
                await bot.TrySetAdminCommandsScope(id, CancellationToken.None);
                return;
            }

            try
            {
                await bot.Client.DeleteMyCommands(scope: new BotCommandScopeChat { ChatId = id });
            }
            catch (Exception ex)
            {
                logger.LogWarning("unable to clear the admin command scope for {id}: {m}", id, ex.Message);
            }
            bot.ForgetAdminCommandsScope(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to sync the command scope for {id}: {m}", id, ex.Message);
        }
    }
}

/// <summary>
/// The single place that changes the user list, shared by the Telegram admin panel and the touch
/// panel: last-admin protection, the implicit gate_controller role and the duplicate check live here.
/// </summary>
internal sealed class UserAdminService(
    UserDirectory users,
    AdminScopeNotifier scopes,
    ILogger<UserAdminService> logger
)
{
    public IReadOnlyList<UserEntry> All() => users.All();

    public UserEntry Add(long id, string? name, IEnumerable<string>? entitlements, IEnumerable<string>? topics)
    {
        if (id <= 0)
            throw new PanelValidationException("L'ID Telegram deve essere un numero positivo.");
        if (users.Find(id) != null)
            throw new PanelConflictException($"L'ID {id} è già configurato.");

        var entry = Build(id, name, entitlements, topics, isNew: true);
        try
        {
            users.Upsert(entry);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to add user {id}: {e}", id, ex);
            throw new PanelConflictException("Non riesco a salvare la configurazione.");
        }

        logger.LogInformation("user {id} added from the panel", id);
        return entry;
    }

    public UserEntry Update(long id, string? name, IEnumerable<string>? entitlements, IEnumerable<string>? topics)
    {
        var existing = users.Find(id) ?? throw new PanelValidationException("Questo utente non esiste più.");
        var entry = Build(
            id,
            name,
            entitlements ?? existing.Entitlements,
            topics ?? existing.Topics,
            isNew: false
        );

        var wasAdmin = existing.IsAdmin;
        var isAdmin = entry.IsAdmin;
        if (wasAdmin && !isAdmin && users.AdminIds().Length <= 1)
            throw new PanelConflictException("Non posso togliere i poteri all'ultimo amministratore.");

        try
        {
            users.Upsert(entry);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to update user {id}: {e}", id, ex);
            throw new PanelConflictException("Non riesco a salvare la configurazione.");
        }

        if (wasAdmin != isAdmin)
            _ = scopes.SyncAsync(id, isAdmin);

        return entry;
    }

    public void Remove(long id)
    {
        var user = users.Find(id) ?? throw new PanelValidationException("Questo utente non esiste più.");
        if (user.IsAdmin && users.AdminIds().Length <= 1)
            throw new PanelConflictException("Non posso rimuovere l'ultimo amministratore.");

        try
        {
            users.Remove(id);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to remove user {id}: {e}", id, ex);
            throw new PanelConflictException("Non riesco a salvare la configurazione.");
        }

        if (user.IsAdmin)
            _ = scopes.SyncAsync(id, false);

        logger.LogInformation("user {id} removed from the panel", id);
    }

    private static UserEntry Build(
        long id,
        string? name,
        IEnumerable<string>? entitlements,
        IEnumerable<string>? topics,
        bool isNew
    )
    {
        var normalizedEntitlements = Entitlement.NormalizeAll(entitlements);
        var normalizedTopics = NotificationTopic.NormalizeAll(topics);

        // Admins control the gates by definition: the extra role would only be noise.
        if (normalizedEntitlements.Contains(Entitlement.Admin))
            normalizedEntitlements.RemoveAll(e => e == Entitlement.GateController);

        // A brand-new admin must not start muted: only fill an empty list.
        if (isNew && normalizedEntitlements.Contains(Entitlement.Admin) && normalizedTopics.Count == 0)
            normalizedTopics = [.. NotificationTopic.All];

        return new UserEntry
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Entitlements = normalizedEntitlements,
            Topics = normalizedTopics,
        };
    }
}
