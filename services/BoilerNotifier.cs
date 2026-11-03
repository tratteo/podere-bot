using PodereBot.Services.Hosted;

namespace PodereBot.Services;

/// <summary>
/// Announces a boiler switch from the panel or from Telegram and remembers when it did, so the
/// monitor does not send the same change again when it samples the pin a few seconds later.
/// </summary>
internal sealed class BoilerNotifier(BotHostedService bot)
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(90);

    private readonly object gate = new();
    private bool? lastState;
    private DateTime lastUtc = DateTime.MinValue;

    public async Task NotifyAsync(bool on)
    {
        lock (gate)
        {
            lastState = on;
            lastUtc = DateTime.UtcNow;
        }

        await bot.NotifyTopicAsync(
            NotificationTopic.Heating,
            on ? "🔥 <b>Caldaia accesa</b>." : "❄️ <b>Caldaia spenta</b>."
        );
    }

    /// <summary>True when the same state was announced just now, so the monitor can stay quiet.</summary>
    public bool WasJustNotified(bool on)
    {
        lock (gate)
            return lastState == on && DateTime.UtcNow - lastUtc <= DedupWindow;
    }
}
