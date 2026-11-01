using System.Reflection;
using PodereBot.Lib.Api;
using PodereBot.Lib.Commands;
using PodereBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Lib;

public static class Extensions
{
    public static bool IsLocal(this HttpRequest req)
    {
        var connection = req.HttpContext.Connection;
        if (connection.RemoteIpAddress == null)
            return false;

        var bytes = connection.RemoteIpAddress.GetAddressBytes();
        if (bytes.Length < 4)
            return false;

        return (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 127 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 1);
    }

    internal static void UseApiEndpoints(this WebApplication app)
    {
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (!typeof(IEndpoint).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
                continue;
            var endpoint = Activator.CreateInstance(type) as IEndpoint;
            endpoint?.MapEndpoint(app);
        }
    }

    internal static async Task NotifyUsers(this TelegramBotClient client, string message, List<long> ids, ILogger? logger = null)
    {
        try
        {
            await Task.WhenAll(ids.ConvertAll(i => client.SendMessage(i, message, parseMode: ParseMode.Html)));
        }
        catch (Exception ex)
        {
            logger?.LogError("error notifying users {e}", ex);
        }
    }
    internal static Task NotifyOwners(
        this TelegramBotClient client,
        string message,
        UserDirectory users,
        ILogger? logger = null
    )
    {
        var ids = users.AdminIds();
        return ids.Length == 0 ? Task.CompletedTask : client.NotifyUsers(message, [.. ids], logger);
    }

    internal static async Task<Message> CleanupOnDetach(this Task<Message> msgTask, Command command)
    {
        var msg = await msgTask;
        command.AddToCleanupMd(msg);
        return msg;
    }

    internal static async Task<Message> DeleteOnDetach(this Task<Message> msgTask, Command command)
    {
        var msg = await msgTask;
        command.AddToRemove(msg);
        return msg;
    }

    internal static string Humanize(this DateTime? input)
    {
        if (input == null) return "";
        TimeSpan ts = (TimeSpan)(DateTime.UtcNow - input);
        double delta = Math.Abs(ts.TotalSeconds);

        if (delta < 1)
            return "proprio ora";

        if (delta < 60)
            return ts.Seconds == 1 ? "un secondo fa" : ts.Seconds + " secondi fa";

        if (delta < 120)
            return "un minuto fa";

        if (delta < 2700) // 45 minuti
            return ts.Minutes + " minuti fa";

        if (delta < 5400) // 90 minuti
            return "un'ora fa";

        if (delta < 86400) // 24 ore
            return ts.Hours + " ore fa";

        if (delta < 172800) // 48 ore
            return "ieri";

        if (delta < 2592000) // 30 giorni
            return ts.Days + " giorni fa";

        if (delta < 31104000) // 12 mesi
        {
            int months = Convert.ToInt32(Math.Floor((double)ts.Days / 30));
            return months <= 1 ? "un mese fa" : months + " mesi fa";
        }

        int years = Convert.ToInt32(Math.Floor((double)ts.Days / 365));
        return years <= 1 ? "un anno fa" : years + " anni fa";
    }

    internal static List<CommandRegistryKey> GetCommands(this Assembly assembly)
    {
        var commands = new List<CommandRegistryKey>();
        foreach (var type in assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<CommandMetadataAttribute>();
            if (attribute == null || !type.IsSubclassOf(typeof(Command)) || type.IsAbstract)
                continue;
            commands.Add(new CommandRegistryKey(attribute, type));
        }
        return commands;
    }

    internal static void AddCommands(this IServiceCollection services)
    {
        var commands = Assembly.GetExecutingAssembly().GetCommands();
        foreach (var c in commands)
        {
            services.AddTransient(c.commandType);
        }
    }

    internal static ReactionTypeEmoji RandomEmoji(this Message emoji)
    {
        List<string> available =
        [
            "👍",
            "❤",
            "🔥",
            "🥰",
            "👏",
            "😁",
            "🤔",
            "🤯",
            "😱",
            "🎉",
            "🤩",
            "🙏",
            "👌",
            "🤡",
            "🥱",
            "🥴",
            "😍",
            "❤‍🔥",
            "🌭",
            "💯",
            "🤣",
            "⚡",
            "🍌",
            "🏆",
            "🍾",
            "😈",
            "🤓",
            "👻",
            "👀",
            "🎃",
            "🙈",
            "😇",
            "🤝",
            "🤗",
            "🫡",
            "🎅",
            "🤪",
            "🗿",
            "🙉",
            "😘",
            "🙊",
            "😎",
            "👾"
        ];
        Random random = new();
        return new ReactionTypeEmoji() { Emoji = available[random.Next(0, available.Count)] };
    }
}
