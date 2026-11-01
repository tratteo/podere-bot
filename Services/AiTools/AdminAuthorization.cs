namespace PodereBot.Services.AiTools;

internal static class AdminAuthorization
{
    public static bool TryAuthorize(UserDirectory users) => TryAuthorize(users, AiChatService.GetCurrentUserId());

    public static bool TryAuthorize(UserDirectory users, long? userId)
    {
        if (userId == null)
            return false;
        return users.IsAdmin(userId.Value);
    }

    /// <summary>
    /// Gate opening is allowed for admins, for gate controllers, and for anyone while the
    /// shared access window is open. External (MCP) calls have no user identity and are denied.
    /// </summary>
    public static bool TryAuthorizeGateOpening(UserDirectory users, Database db)
    {
        var userId = AiChatService.GetCurrentUserId();
        if (userId == null)
            return false;
        if (users.CanControlGates(userId.Value))
            return true;

        var expiration = db.Data.GatesOpenAccessExpirationDate;
        return expiration != null && DateTime.Now < expiration;
    }
}
