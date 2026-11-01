namespace PodereBot.Services.AiTools;

internal static class AdminAuthorization
{
    public static bool TryAuthorize(IConfiguration configuration)
    {
        return TryAuthorize(configuration, AiChatService.GetCurrentUserId());
    }

    public static bool TryAuthorize(IConfiguration configuration, long? userId)
    {
        if (userId == null)
            return false;
        return AdminList.Get(configuration).Contains(userId.Value);
    }
}
