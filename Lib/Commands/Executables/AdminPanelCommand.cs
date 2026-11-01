using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PodereBot.Services;
using PodereBot.Services.Hosted;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PodereBot.Lib.Commands;

[CommandMetadata(Key = "/adminpanel", Description = "Pannello amministratori 👑", Admin = true)]
internal class AdminPanelCommand(
    ILogger<AdminPanelCommand> logger,
    IConfiguration configuration,
    IHostEnvironment environment,
    BotHostedService bot,
    PendingInputRegistry pendingInputs
) : Command(logger, configuration)
{
    private enum Screen
    {
        Panel,
        Remove,
        Add,
    }

    private const int RequestUsersId = 1;
    private static readonly object configLock = new();
    private readonly ILogger<AdminPanelCommand> logger = logger;
    private readonly IHostEnvironment environment = environment;
    private readonly BotHostedService bot = bot;
    private readonly PendingInputRegistry pendingInputs = pendingInputs;
    private Screen screen = Screen.Panel;
    private long? selectedId;
    private string notice = string.Empty;
    private Message? panel;
    private Message? pickerPrompt;

    protected override Task OnDetach()
    {
        pendingInputs.Unregister(Arguments.Message.Chat.Id);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteInternal()
    {
        AttachEvents();
        panel = await Arguments
            .Client.SendMessage(
                Arguments.Message.Chat.Id,
                "👑 Pannello amministratori",
                parseMode: ParseMode.Html,
                disableNotification: true
            )
            .DeleteOnDetach(this);
        await RenderAsync();
        await ClearReplyKeyboardAsync();
    }

    private async Task ClearReplyKeyboardAsync()
    {
        try
        {
            var cleanup = await Arguments.Client.SendMessage(
                Arguments.Message.Chat.Id,
                ((char)0x2063).ToString(),
                replyMarkup: new ReplyKeyboardRemove(),
                disableNotification: true
            );
            await Arguments.Client.DeleteMessage(cleanup.Chat.Id, cleanup.Id);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "unable to clear reply keyboard");
        }
    }

    private async Task RenderAsync()
    {
        if (panel == null)
            return;

        string text;
        InlineKeyboardMarkup keyboard;
        switch (screen)
        {
            case Screen.Remove:
                (text, keyboard) = await BuildRemoveAsync();
                break;
            case Screen.Add:
                text = BuildAddText();
                keyboard = BuildAddKeyboard();
                break;
            default:
                (text, keyboard) = await BuildPanelAsync();
                break;
        }

        try
        {
            await Arguments.Client.EditMessageText(panel.Chat.Id, panel.Id, text, parseMode: ParseMode.Html, replyMarkup: keyboard);
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to render admin panel: {m}", ex.Message);
        }
    }

    private async Task<(string text, InlineKeyboardMarkup keyboard)> BuildPanelAsync()
    {
        var admins = AdminList.Get(configuration);
        var storedNames = GetStoredNames();
        var names = await ResolveNamesAsync(admins, storedNames);
        var sb = new StringBuilder("👑 <b>Pannello amministratori</b>\n\n");
        if (admins.Length == 0)
        {
            sb.AppendLine("Nessun amministratore.");
        }
        else
        {
            foreach (var id in admins)
                sb.AppendLine($"• {UserLink(id, names[id])} <code>{id}</code>");
        }
        sb.AppendLine();
        sb.AppendLine($"Totale: <b>{admins.Length}</b>");
        AppendNotice(sb);

        var kbd = new InlineKeyboardMarkup();
        foreach (var id in admins)
            kbd.AddButton($"➖ {Shorten(names[id])}", EncodeCallbackQueryData($"ask:{id}")).AddNewRow();
        kbd.AddButton("➕ Aggiungi amministratore", EncodeCallbackQueryData("add")).AddNewRow();
        kbd.AddButton("✖️ Chiudi", EncodeCallbackQueryData("close"));
        return (sb.ToString().TrimEnd(), kbd);
    }

    private async Task<(string text, InlineKeyboardMarkup keyboard)> BuildRemoveAsync()
    {
        var id = selectedId ?? 0;
        var key = id.ToString(CultureInfo.InvariantCulture);
        var storedNames = GetStoredNames();
        var name =
            storedNames.TryGetValue(key, out var stored) && !string.IsNullOrWhiteSpace(stored)
                ? stored
                : await ResolveNameAsync(id);
        var sb = new StringBuilder();
        sb.AppendLine($"Rimuovere {UserLink(id, name)} dagli amministratori?");
        sb.AppendLine($"<code>{id}</code>");
        AppendNotice(sb);

        var kbd = new InlineKeyboardMarkup();
        kbd.AddButton("✅ Rimuovi", EncodeCallbackQueryData("rm")).AddNewRow();
        kbd.AddButton("↩️ Annulla", EncodeCallbackQueryData("back"));
        return (sb.ToString().TrimEnd(), kbd);
    }

    private string BuildAddText()
    {
        var sb = new StringBuilder("➕ <b>Nuovo amministratore</b>\n\n");
        sb.AppendLine("Mandami l'ID Telegram in chat, oppure usa il pulsante qui sotto per sceglierlo dai contatti.");
        if (pickerPrompt != null)
            sb.AppendLine().AppendLine("<i>Pulsante di selezione inviato qui sotto.</i>");
        AppendNotice(sb);
        return sb.ToString().TrimEnd();
    }

    private InlineKeyboardMarkup BuildAddKeyboard()
    {
        var kbd = new InlineKeyboardMarkup();
        kbd.AddButton("✖️ Annulla", EncodeCallbackQueryData("cancelAdd"));
        return kbd;
    }

    private void AppendNotice(StringBuilder sb)
    {
        if (string.IsNullOrEmpty(notice))
            return;
        sb.AppendLine();
        sb.AppendLine(notice);
    }

    protected override async Task OnCallback(Update update, string callbackData)
    {
        await Arguments.Client.AnswerCallbackQuery(update.CallbackQuery!.Id);
        notice = string.Empty;

        var parts = callbackData.Split(':');
        switch (parts[0])
        {
            case "close":
                await DetachEvents();
                return;
            case "add":
                await EnterAddAsync();
                return;
            case "cancelAdd":
                await LeaveAddAsync("🟡 Aggiunta annullata.");
                return;
            case "ask":
                selectedId = long.Parse(parts[1], CultureInfo.InvariantCulture);
                screen = Screen.Remove;
                break;
            case "rm":
                if (selectedId is long id)
                    await RemoveAdminAsync(id);
                return;
            case "back":
                screen = Screen.Panel;
                break;
        }

        await RenderAsync();
    }

    protected override async Task OnMessage(Message message, UpdateType type)
    {
        if (screen != Screen.Add)
            return;
        if (message.Chat.Id != Arguments.Message.Chat.Id || message.Text?.StartsWith('/') == true)
            return;

        var sharedUser = message.UsersShared?.Users.FirstOrDefault();
        long? id = sharedUser?.UserId;
        if (id == null && long.TryParse(message.Text?.Trim(), out var parsed))
            id = parsed;

        if (id == null || id <= 0)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                notice = "🔴 ID non valido.";
                await RenderAsync();
            }
            return;
        }

        await AddAdminAsync(id.Value, BuildDisplayName(sharedUser));
    }

    private async Task EnterAddAsync()
    {
        screen = Screen.Add;
        notice = string.Empty;
        await RenderAsync();

        var keyboard = new ReplyKeyboardMarkup(
            new KeyboardButton[]
            {
                new("📇 Scegli contatto")
                {
                    RequestUsers = new KeyboardButtonRequestUsers(RequestUsersId)
                    {
                        UserIsBot = false,
                        MaxQuantity = 1,
                        RequestName = true,
                        RequestUsername = true,
                    },
                },
            }
        )
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true,
        };
        pickerPrompt = await Arguments.Client.SendMessage(
            Arguments.Message.Chat.Id,
            "Premi 📇 per scegliere un contatto, oppure scrivi l'ID qui.",
            replyMarkup: keyboard,
            disableNotification: true
        );
        pendingInputs.Register(Arguments.Message.Chat.Id);
    }

    private async Task LeaveAddAsync(string message)
    {
        pendingInputs.Unregister(Arguments.Message.Chat.Id);
        if (pickerPrompt != null)
        {
            try
            {
                await Arguments.Client.DeleteMessage(pickerPrompt.Chat.Id, pickerPrompt.Id);
            }
            catch (Exception)
            {
                // The prompt may already be gone.
            }
            pickerPrompt = null;
        }
        await Arguments.Client.SendMessage(
            Arguments.Message.Chat.Id,
            message,
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
            disableNotification: true
        );
        screen = Screen.Panel;
        await RenderAsync();
    }

    private async Task AddAdminAsync(long id, string? displayName)
    {
        if (AdminList.Get(configuration).Contains(id))
        {
            await LeaveAddAsync($"🟡 <code>{id}</code> è già amministratore.");
            return;
        }

        var key = id.ToString(CultureInfo.InvariantCulture);
        if (
            !TryWriteAdmins(
                runtime => runtime.Add(id),
                removed => removed.Remove(id),
                names =>
                {
                    if (!string.IsNullOrWhiteSpace(displayName))
                        names[key] = displayName;
                },
                out var error
            )
        )
        {
            notice = error;
            await RenderAsync();
            return;
        }

        try
        {
            await bot.TrySetAdminCommandsScope(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to set admin commands for {id}: {m}", id, ex.Message);
        }
        await LeaveAddAsync($"🟢 <code>{id}</code> aggiunto agli amministratori.");
    }

    private async Task RemoveAdminAsync(long id)
    {
        var admins = AdminList.Get(configuration);
        if (!admins.Contains(id))
        {
            notice = "🟡 Questo utente non è più amministratore.";
            screen = Screen.Panel;
            await RenderAsync();
            return;
        }
        if (admins.Length <= 1)
        {
            notice = "🔴 Non posso rimuovere l'ultimo amministratore.";
            screen = Screen.Panel;
            await RenderAsync();
            return;
        }

        if (
            !TryWriteAdmins(
                runtime => runtime.Remove(id),
                removed => removed.Add(id),
                names => names.Remove(id.ToString(CultureInfo.InvariantCulture)),
                out var error
            )
        )
        {
            notice = error;
            screen = Screen.Panel;
            await RenderAsync();
            return;
        }

        try
        {
            await bot.Client.DeleteMyCommands(scope: new BotCommandScopeChat() { ChatId = id });
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to clear admin commands for {id}: {m}", id, ex.Message);
        }
        bot.ForgetAdminCommandsScope(id);

        selectedId = null;
        notice = $"🟢 <code>{id}</code> rimosso dagli amministratori.";
        screen = Screen.Panel;
        await RenderAsync();
    }

    private bool TryWriteAdmins(
        Action<List<long>> editRuntime,
        Action<List<long>> editRemoved,
        Action<Dictionary<string, string>> editNames,
        out string error
    )
    {
        var path = Path.Join(environment.ContentRootPath, "admins.json");
        lock (configLock)
        {
            try
            {
                var runtime = (configuration.GetSection("RuntimeAdmins").Get<long[]>() ?? []).Distinct().ToList();
                var removed = (configuration.GetSection("RemovedAdmins").Get<long[]>() ?? []).Distinct().ToList();
                var names = GetStoredNames();
                editRuntime(runtime);
                editRemoved(removed);
                editNames(names);

                var runtimeArray = new JArray();
                foreach (var id in runtime.Distinct())
                    runtimeArray.Add(id);
                var removedArray = new JArray();
                foreach (var id in removed.Distinct())
                    removedArray.Add(id);
                var namesObject = new JObject();
                foreach (var entry in names.OrderBy(e => e.Key))
                    namesObject[entry.Key] = entry.Value;

                var root = new JObject
                {
                    ["RuntimeAdmins"] = runtimeArray,
                    ["RemovedAdmins"] = removedArray,
                    ["AdminNames"] = namesObject,
                };

                using var writer = new StringWriter() { NewLine = "\n" };
                using (
                    var jsonWriter = new JsonTextWriter(writer)
                    {
                        Formatting = Formatting.Indented,
                        Indentation = 2,
                        CloseOutput = false,
                    }
                )
                {
                    root.WriteTo(jsonWriter);
                }
                writer.WriteLine();

                var tmpPath = $"{path}.tmp";
                try
                {
                    File.WriteAllText(tmpPath, writer.ToString());
                    File.Move(tmpPath, path, true);
                }
                finally
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }

                if (configuration is IConfigurationRoot rootConfiguration)
                    rootConfiguration.Reload();

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError("unable to update admins at {p}: {e}", path, ex);
                error = "🔴 Non sono riuscito a salvare la configurazione, guarda i log.";
                return false;
            }
        }
    }

    private async Task<Dictionary<long, string>> ResolveNamesAsync(IEnumerable<long> ids, Dictionary<string, string> storedNames)
    {
        var names = new Dictionary<long, string>();
        foreach (var id in ids)
        {
            var key = id.ToString(CultureInfo.InvariantCulture);
            names[id] =
                storedNames.TryGetValue(key, out var stored) && !string.IsNullOrWhiteSpace(stored)
                    ? stored
                    : await ResolveNameAsync(id);
        }
        return names;
    }

    private Dictionary<string, string> GetStoredNames() =>
        configuration.GetSection("AdminNames").Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();

    private static string? BuildDisplayName(SharedUser? user)
    {
        if (user == null)
            return null;
        var name = $"{user.FirstName} {user.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return string.IsNullOrWhiteSpace(user.Username) ? null : $"@{user.Username}";
    }

    private async Task<string> ResolveNameAsync(long id)
    {
        try
        {
            var chat = await Arguments.Client.GetChat(id);
            var name = $"{chat.FirstName} {chat.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(chat.Username))
                name = $"@{chat.Username}";
            return string.IsNullOrWhiteSpace(name) ? id.ToString(CultureInfo.InvariantCulture) : name;
        }
        catch (Exception)
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string UserLink(long id, string name) =>
        $"<a href=\"tg://user?id={id}\">{WebUtility.HtmlEncode(name)}</a>";

    private static string Shorten(string name) => name.Length <= 28 ? name : $"{name[..28]}…";
}
