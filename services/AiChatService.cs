using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Services;

internal class AiChatService(
    IChatClient chatClient,
    AiTools.AiToolRegistry toolRegistry,
    UsageTracker usageTracker,
    IConfiguration configuration,
    ILogger<AiChatService> logger
)
{
    private static readonly AsyncLocal<long?> currentUserId = new();

    internal static long? GetCurrentUserId() => currentUserId.Value;

    public async Task ProcessMessageAsync(ITelegramBotClient botClient, Message message)
    {
        if (message.Text == null)
            return;

        currentUserId.Value = message.From?.Id;
        OpenCodeSession.Set($"poderebot-{message.Chat.Id}");
        try
        {
            var systemPrompt = configuration.GetSection("AI")["SystemPrompt"] ?? "";
            var userId = message.From?.Id ?? 0;
            var fullSystemPrompt = $"{systemPrompt}\n\nID utente Telegram corrente: {userId}";
            if (message.From != null)
            {
                var displayName = $"{message.From.FirstName} {message.From.LastName}".Trim();
                if (!string.IsNullOrWhiteSpace(displayName))
                    fullSystemPrompt += $"\nNome visualizzato: {displayName}";
            }
            if (message.From?.Username != null)
                fullSystemPrompt += $"\nUsername: @{message.From.Username}";

            await botClient.SendChatAction(message.Chat.Id, ChatAction.Typing);

            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, fullSystemPrompt),
                    new ChatMessage(ChatRole.User, message.Text)
                ],
                new ChatOptions
                {
                    Tools = [.. toolRegistry.AllAiFunctions],
                    Reasoning = new ReasoningOptions { Effort = GetReasoningEffort() }
                }
            );

            var responseText = response.Text;
            usageTracker.Record(response);
            if (responseText != null)
            {
                await botClient.SendMessage(message.Chat.Id, responseText, parseMode: ParseMode.Html);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI chat error for user {userId}", message.From?.Id);
            await botClient.SendMessage(message.Chat.Id, "Non ho potuto elaborare la richiesta. Qualcosa è andato storto.");
        }
        finally
        {
            currentUserId.Value = null;
            OpenCodeSession.Set(null);
        }
    }

    private ReasoningEffort GetReasoningEffort()
    {
        var value = configuration.GetSection("AI")["ReasoningEffort"];
        return Enum.TryParse<ReasoningEffort>(value, ignoreCase: true, out var effort) ? effort : ReasoningEffort.Low;
    }
}
