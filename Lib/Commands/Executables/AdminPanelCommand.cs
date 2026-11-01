using System.Globalization;
using System.Net;
using System.Text;
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
    BotHostedService bot,
    PendingInputRegistry pendingInputs,
    UserDirectory users
) : Command(logger, configuration, users)
{
    private enum Screen
    {
        Panel,
        User,
        Remove,
        Add,
    }

    private const int RequestUsersId = 1;
    private readonly ILogger<AdminPanelCommand> logger = logger;
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
            case Screen.User:
                (text, keyboard) = await BuildUserAsync(selectedId ?? 0);
                break;
            case Screen.Remove:
                (text, keyboard) = await BuildRemoveAsync(selectedId ?? 0);
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
        var all = users.All();
        var names = await ResolveNamesAsync(all);
        var sb = new StringBuilder("👑 <b>Pannello utenti</b>\n\n");
        if (all.Count == 0)
        {
            sb.AppendLine("Nessun utente configurato.");
        }
        else
        {
            foreach (var user in all)
            {
                sb.AppendLine($"• {UserLink(user.Id, names[user.Id])} <code>{user.Id}</code>");
                var badges = BuildBadges(user);
                if (badges.Length > 0)
                    sb.AppendLine($"  {badges}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Totale: <b>{all.Count}</b> utenti, <b>{all.Count(u => u.IsAdmin)}</b> admin");
        AppendNotice(sb);

        var kbd = new InlineKeyboardMarkup();
        foreach (var user in all)
            kbd.AddButton($"✏️ {Shorten(names[user.Id])}", EncodeCallbackQueryData($"u:{user.Id}")).AddNewRow();
        kbd.AddButton("➕ Aggiungi utente", EncodeCallbackQueryData("add")).AddNewRow();
        kbd.AddButton("✖️ Chiudi", EncodeCallbackQueryData("close"));
        return (sb.ToString().TrimEnd(), kbd);
    }

    private async Task<(string text, InlineKeyboardMarkup keyboard)> BuildUserAsync(long id)
    {
        var user = users.Find(id);
        if (user == null)
        {
            notice = "🟡 Questo utente non esiste più.";
            return await BuildPanelAsync();
        }

        var name = await ResolveNameAsync(user);
        var sb = new StringBuilder();
        sb.AppendLine($"👤 {UserLink(user.Id, name)}");
        sb.AppendLine($"<code>{user.Id}</code>");
        sb.AppendLine();
        sb.AppendLine("<b>Permessi</b>");
        sb.AppendLine($"• {ToggleLabel("admin", "👑", user.IsAdmin)}");
        sb.AppendLine($"• {ToggleLabel("gate_controller", "🔑", user.Entitlements.Contains(Entitlement.GateController))}");
        sb.AppendLine();
        sb.AppendLine("<b>Topic</b>");
        foreach (var topic in NotificationTopic.All)
            sb.AppendLine($"• {ToggleLabel(topic, "#", user.IsSubscribed(topic))}");
        AppendNotice(sb);

        var kbd = new InlineKeyboardMarkup();
        foreach (var entitlement in Entitlement.All)
            kbd.AddButton(ToggleButton(entitlement, user.Entitlements.Contains(entitlement)), EncodeCallbackQueryData($"te:{id}:{entitlement}")).AddNewRow();
        foreach (var topic in NotificationTopic.All)
            kbd.AddButton(ToggleButton(topic, user.IsSubscribed(topic)), EncodeCallbackQueryData($"tt:{id}:{topic}")).AddNewRow();
        kbd.AddButton("🗑️ Rimuovi utente", EncodeCallbackQueryData($"ask:{id}")).AddNewRow();
        kbd.AddButton("↩️ Indietro", EncodeCallbackQueryData("back"));
        return (sb.ToString().TrimEnd(), kbd);
    }

    private async Task<(string text, InlineKeyboardMarkup keyboard)> BuildRemoveAsync(long id)
    {
        var user = users.Find(id);
        if (user == null)
        {
            notice = "🟡 Questo utente non esiste più.";
            return await BuildPanelAsync();
        }

        var name = await ResolveNameAsync(user);
        var sb = new StringBuilder();
        sb.AppendLine($"Rimuovere {UserLink(user.Id, name)}?");
        sb.AppendLine($"<code>{user.Id}</code>");
        if (user.IsAdmin && users.AdminIds().Length <= 1)
            sb.AppendLine();
        sb.AppendLine("Perde tutti i permessi e le iscrizioni ai topic.");
        AppendNotice(sb);

        var kbd = new InlineKeyboardMarkup();
        kbd.AddButton("✅ Rimuovi", EncodeCallbackQueryData($"rm:{id}")).AddNewRow();
        kbd.AddButton("↩️ Annulla", EncodeCallbackQueryData($"u:{id}"));
        return (sb.ToString().TrimEnd(), kbd);
    }

    private string BuildAddText()
    {
        var sb = new StringBuilder("➕ <b>Nuovo utente</b>\n\n");
        sb.AppendLine("Mandami l'ID Telegram in chat, oppure usa il pulsante qui sotto per sceglierlo dai contatti.");
        sb.AppendLine("L'utente viene creato senza permessi: li scegli nella schermata successiva.");
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

    private static string ToggleLabel(string name, string icon, bool active) =>
        $"{icon} <code>{name}</code> {(active ? "✅" : "➖")}";

    private static string ToggleButton(string name, bool active) => $"{(active ? "✅" : "➖")} {name}";

    private static string BuildBadges(UserEntry user)
    {
        var parts = new List<string>();
        if (user.IsAdmin)
            parts.Add("👑 admin");
        if (user.Entitlements.Contains(Entitlement.GateController))
            parts.Add("🔑 gate_controller");
        foreach (var topic in user.Topics)
            parts.Add($"#{topic}");
        return string.Join(" · ", parts);
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
            case "u":
                selectedId = long.Parse(parts[1], CultureInfo.InvariantCulture);
                screen = Screen.User;
                break;
            case "ask":
                selectedId = long.Parse(parts[1], CultureInfo.InvariantCulture);
                screen = Screen.Remove;
                break;
            case "rm":
                if (selectedId is long removeId)
                    await RemoveUserAsync(removeId);
                return;
            case "te":
                if (long.TryParse(parts[1], CultureInfo.InvariantCulture, out var entId) && parts.Length > 2)
                {
                    await ToggleEntitlementAsync(entId, parts[2]);
                    return;
                }
                break;
            case "tt":
                if (long.TryParse(parts[1], CultureInfo.InvariantCulture, out var topicId) && parts.Length > 2)
                {
                    await ToggleTopicAsync(topicId, parts[2]);
                    return;
                }
                break;
            case "back":
                selectedId = null;
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

        await AddUserAsync(id.Value, BuildDisplayName(sharedUser));
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

    private async Task AddUserAsync(long id, string? displayName)
    {
        if (users.Find(id) != null)
        {
            await LeaveAddAsync($"🟡 <code>{id}</code> è già configurato.");
            return;
        }

        try
        {
            users.Upsert(new UserEntry { Id = id, Name = displayName });
        }
        catch (Exception ex)
        {
            logger.LogError("unable to add user {id}: {e}", id, ex);
            notice = "🔴 Non sono riuscito a salvare la configurazione, guarda i log.";
            await RenderAsync();
            return;
        }

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
            $"🟢 <code>{id}</code> aggiunto. Scegli ora i permessi.",
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
            disableNotification: true
        );

        selectedId = id;
        screen = Screen.User;
        await RenderAsync();
    }

    private async Task ToggleEntitlementAsync(long id, string entitlement)
    {
        var user = users.Find(id);
        if (user == null)
        {
            notice = "🟡 Questo utente non esiste più.";
            screen = Screen.Panel;
            await RenderAsync();
            return;
        }

        var normalized = Entitlement.Normalize(entitlement);
        if (normalized == null)
        {
            notice = "🔴 Permesso sconosciuto.";
            await RenderAsync();
            return;
        }

        var granted = user.Entitlements.Contains(normalized);
        if (granted && normalized == Entitlement.Admin && users.AdminIds().Length <= 1)
        {
            notice = "🔴 Non posso togliere i poteri all'ultimo amministratore.";
            await RenderAsync();
            return;
        }

        if (granted)
            user.Entitlements.RemoveAll(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase));
        else
            user.Entitlements.Add(normalized);

        if (!TrySave(user))
            return;

        if (normalized == Entitlement.Admin)
        {
            if (granted)
                await ForgetAdminScopeAsync(id);
            else
                await TrySetAdminScopeAsync(id);
        }

        notice = granted ? $"🟢 <code>{normalized}</code> rimosso." : $"🟢 <code>{normalized}</code> aggiunto.";
        await RenderAsync();
    }

    private async Task ToggleTopicAsync(long id, string topic)
    {
        var user = users.Find(id);
        if (user == null)
        {
            notice = "🟡 Questo utente non esiste più.";
            screen = Screen.Panel;
            await RenderAsync();
            return;
        }

        var normalized = NotificationTopic.Normalize(topic);
        if (normalized == null)
        {
            notice = "🔴 Topic sconosciuto.";
            await RenderAsync();
            return;
        }

        var subscribed = user.Topics.Contains(normalized);
        if (subscribed)
            user.Topics.RemoveAll(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase));
        else
            user.Topics.Add(normalized);

        if (!TrySave(user))
            return;

        notice = subscribed ? $"🟢 Iscrizione a <code>{normalized}</code> rimossa." : $"🟢 Iscritto a <code>{normalized}</code>.";
        await RenderAsync();
    }

    private async Task RemoveUserAsync(long id)
    {
        var user = users.Find(id);
        if (user == null)
        {
            notice = "🟡 Questo utente non esiste più.";
            screen = Screen.Panel;
            await RenderAsync();
            return;
        }

        if (user.IsAdmin && users.AdminIds().Length <= 1)
        {
            notice = "🔴 Non posso rimuovere l'ultimo amministratore.";
            screen = Screen.User;
            await RenderAsync();
            return;
        }

        try
        {
            users.Remove(id);
        }
        catch (Exception ex)
        {
            logger.LogError("unable to remove user {id}: {e}", id, ex);
            notice = "🔴 Non sono riuscito a salvare la configurazione, guarda i log.";
            screen = Screen.User;
            await RenderAsync();
            return;
        }

        if (user.IsAdmin)
            await ForgetAdminScopeAsync(id);

        selectedId = null;
        notice = $"🟢 <code>{id}</code> rimosso.";
        screen = Screen.Panel;
        await RenderAsync();
    }

    private bool TrySave(UserEntry user)
    {
        try
        {
            users.Upsert(user);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("unable to save user {id}: {e}", user.Id, ex);
            notice = "🔴 Non sono riuscito a salvare la configurazione, guarda i log.";
            return false;
        }
    }

    private async Task TrySetAdminScopeAsync(long id)
    {
        try
        {
            await bot.TrySetAdminCommandsScope(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to set admin commands for {id}: {m}", id, ex.Message);
        }
    }

    private async Task ForgetAdminScopeAsync(long id)
    {
        try
        {
            await bot.Client.DeleteMyCommands(scope: new BotCommandScopeChat() { ChatId = id });
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to clear admin commands for {id}: {m}", id, ex.Message);
        }
        bot.ForgetAdminCommandsScope(id);
    }

    private async Task<Dictionary<long, string>> ResolveNamesAsync(IReadOnlyList<UserEntry> entries)
    {
        var names = new Dictionary<long, string>();
        foreach (var entry in entries)
            names[entry.Id] = await ResolveNameAsync(entry);
        return names;
    }

    private static string? BuildDisplayName(SharedUser? user)
    {
        if (user == null)
            return null;
        var name = $"{user.FirstName} {user.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return string.IsNullOrWhiteSpace(user.Username) ? null : $"@{user.Username}";
    }

    private async Task<string> ResolveNameAsync(UserEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name))
            return entry.Name;

        try
        {
            var chat = await Arguments.Client.GetChat(entry.Id);
            var name = $"{chat.FirstName} {chat.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(chat.Username))
                name = $"@{chat.Username}";
            return string.IsNullOrWhiteSpace(name) ? entry.Id.ToString(CultureInfo.InvariantCulture) : name;
        }
        catch (Exception)
        {
            return entry.Id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string UserLink(long id, string name) =>
        $"<a href=\"tg://user?id={id}\">{WebUtility.HtmlEncode(name)}</a>";

    private static string Shorten(string name) => name.Length <= 28 ? name : $"{name[..28]}…";
}
