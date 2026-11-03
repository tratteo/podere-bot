namespace PodereBot.Services;

/// <summary>
/// Keeps startup notifications quiet until the bot has finished its Telegram setup.
/// The boot message itself is sent before this flag is raised.
/// </summary>
internal sealed class StartupGate
{
    private volatile bool ready;

    public bool Ready => ready;

    /// <summary>Marks the monitor ready even when Telegram is still unreachable.</summary>
    public void MarkReady() => ready = true;
}
