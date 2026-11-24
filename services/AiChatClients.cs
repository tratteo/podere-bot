using Microsoft.Extensions.AI;

namespace PodereBot.Services;

/// <summary>
/// One chat client per modality: <c>AI:Model</c> answers text, <c>AI:AudioModel</c> answers voice
/// notes and audio files. The audio client is null when no audio model is configured.
/// </summary>
internal sealed class AiChatClients(IChatClient text, IChatClient? audio)
{
    public IChatClient Text { get; } = text;

    public IChatClient? Audio { get; } = audio;
}
