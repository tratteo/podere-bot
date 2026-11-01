using System.ClientModel.Primitives;

namespace PodereBot.Services;

internal sealed class OpenCodeSessionPolicy : PipelinePolicy
{
    private const string HeaderName = "x-opencode-session";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplySession(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex
    )
    {
        ApplySession(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void ApplySession(PipelineMessage message)
    {
        if (OpenCodeSession.Current is { } session)
            message.Request.Headers.Set(HeaderName, session);
    }
}

internal static class OpenCodeSession
{
    private static readonly AsyncLocal<string?> current = new();

    public static string? Current => current.Value;

    public static void Set(string? sessionId) => current.Value = sessionId;
}
