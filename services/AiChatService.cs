using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PodereBot.Services;

internal class AiChatService(
    AiChatClients chatClients,
    AiTools.AiToolRegistry toolRegistry,
    UsageTracker usageTracker,
    AudioTranscoder audioTranscoder,
    IConfiguration configuration,
    ILogger<AiChatService> logger
)
{
    // Ten minutes of speech keeps one message from burning the weekly budget; Telegram caps bot
    // downloads at 20 MB anyway.
    private const int MaxAudioSeconds = 600;

    private static readonly AsyncLocal<long?> currentUserId = new();

    internal static long? GetCurrentUserId() => currentUserId.Value;

    public async Task ProcessMessageAsync(ITelegramBotClient botClient, Message message)
    {
        var audio = AudioSource.From(message);
        if (message.Text == null && audio == null)
            return;

        currentUserId.Value = message.From?.Id;
        OpenCodeSession.Set($"poderebot-{message.Chat.Id}");
        try
        {
            var chatClient = await SelectChatClientAsync(botClient, message, audio);
            if (chatClient == null)
                return;

            var fullSystemPrompt = BuildSystemPrompt(message);

            // Voice shows record-voice while it is downloaded and converted, typing while the answer
            // is written; the text path only shows typing.
            var userContents =
                audio == null
                    ? await BuildUserContentsAsync(botClient, message, null)
                    : await WithChatActionAsync(
                        botClient,
                        message.Chat.Id,
                        ChatAction.RecordVoice,
                        () => BuildUserContentsAsync(botClient, message, audio)
                    );

            var response = await WithChatActionAsync(
                botClient,
                message.Chat.Id,
                ChatAction.Typing,
                () =>
                    chatClient.GetResponseAsync(
                        [
                            new ChatMessage(ChatRole.System, fullSystemPrompt),
                            new ChatMessage(ChatRole.User, userContents),
                        ],
                        new ChatOptions
                        {
                            Tools = [.. toolRegistry.AllAiFunctions],
                            // The audio model advertises no effort levels, so the audio request leaves it out.
                            Reasoning = audio == null ? new ReasoningOptions { Effort = GetReasoningEffort() } : null,
                        }
                    )
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

    /// <summary>
    /// Runs <paramref name="work"/> with a chat action visible for its whole duration: Telegram
    /// drops an action after five seconds, so it is sent again every four until the work ends.
    /// </summary>
    private async Task<T> WithChatActionAsync<T>(ITelegramBotClient botClient, long chatId, ChatAction action, Func<Task<T>> work)
    {
        using var cancellation = new CancellationTokenSource();
        var keepAlive = KeepChatActionAsync(botClient, chatId, action, cancellation.Token);
        try
        {
            return await work();
        }
        finally
        {
            await cancellation.CancelAsync();
            await keepAlive;
        }
    }

    private async Task KeepChatActionAsync(
        ITelegramBotClient botClient,
        long chatId,
        ChatAction action,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await botClient.SendChatAction(chatId, action, cancellationToken: cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await botClient.SendChatAction(chatId, action, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The status is cosmetic: a failed refresh must never break the answer.
            logger.LogDebug(ex, "unable to refresh the {action} chat action", action);
        }
    }

    private async Task<IChatClient?> SelectChatClientAsync(ITelegramBotClient botClient, Message message, AudioSource? audio)
    {
        if (audio == null)
            return chatClients.Text;

        if (chatClients.Audio == null)
        {
            logger.LogWarning("audio message received but AI:AudioModel is not configured");
            await botClient.SendMessage(message.Chat.Id, "I vocali non sono configurati.");
            return null;
        }

        if (audio.Duration > MaxAudioSeconds)
        {
            await botClient.SendMessage(message.Chat.Id, "Vocale troppo lungo, mandalo più corto.");
            return null;
        }

        return chatClients.Audio;
    }

    private async Task<List<AIContent>> BuildUserContentsAsync(
        ITelegramBotClient botClient,
        Message message,
        AudioSource? audio
    )
    {
        var contents = new List<AIContent>();
        // An audio file can carry a caption; a photo caption stays ignored, because the photo itself is.
        var text = audio == null ? message.Text : message.Text ?? message.Caption;
        if (!string.IsNullOrWhiteSpace(text))
            contents.Add(new TextContent(text));
        if (audio != null)
        {
            contents.Add(
                new TextContent("L'utente ha mandato un vocale: ascoltalo e rispondi come a un messaggio scritto.")
            );
            using var stream = new MemoryStream();
            await botClient.GetInfoAndDownloadFile(audio.FileId, stream);
            var mp3 = await audioTranscoder.ToMp3Async(stream.ToArray(), CancellationToken.None);
            contents.Add(new DataContent(mp3, "audio/mpeg"));
        }
        return contents;
    }

    private string BuildSystemPrompt(Message message)
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
        return fullSystemPrompt;
    }

    private ReasoningEffort GetReasoningEffort()
    {
        var value = configuration.GetSection("AI")["ReasoningEffort"];
        return Enum.TryParse<ReasoningEffort>(value, ignoreCase: true, out var effort) ? effort : ReasoningEffort.Low;
    }

    private sealed record AudioSource(string FileId, int Duration)
    {
        internal static AudioSource? From(Message message) =>
            message.Voice is { } voice ? new AudioSource(voice.FileId, voice.Duration)
            : message.Audio is { } audioFile ? new AudioSource(audioFile.FileId, audioFile.Duration)
            : null;
    }
}
