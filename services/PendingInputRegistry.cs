namespace PodereBot.Services;

internal class PendingInputRegistry
{
    private readonly HashSet<long> chats = [];
    private readonly object sync = new();

    public void Register(long chatId)
    {
        lock (sync)
        {
            chats.Add(chatId);
        }
    }

    public void Unregister(long chatId)
    {
        lock (sync)
        {
            chats.Remove(chatId);
        }
    }

    public bool IsWaiting(long chatId)
    {
        lock (sync)
        {
            return chats.Contains(chatId);
        }
    }
}
