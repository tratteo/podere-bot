namespace PodereBot.Services;

internal static class AdminList
{
    public static long[] Get(IConfiguration configuration)
    {
        var seed = configuration.GetSection("Admins").Get<long[]>() ?? [];
        var runtime = configuration.GetSection("RuntimeAdmins").Get<long[]>() ?? [];
        var removed = configuration.GetSection("RemovedAdmins").Get<long[]>() ?? [];
        return [.. seed.Union(runtime).Except(removed)];
    }
}
