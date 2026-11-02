using System.Net;
using System.Text;

namespace PodereBot.Services.Panel;

/// <summary>
/// Persists every notification before it is sent to Telegram, so the panel keeps the history
/// even when Telegram is unreachable. Panel actions are recorded as audit events too.
/// </summary>
internal sealed class NotificationLog(
    PanelStore store,
    IConfiguration configuration,
    ILogger<NotificationLog> logger
)
{
    public const string PanelTopic = "panel";

    public static readonly IReadOnlyList<string> Topics = ["technical", "heating", "gates", PanelTopic];

    private readonly PanelEventOptions options = OptionsReader.ReadPanelEvents(configuration);

    /// <summary>Records a notification exactly as it was sent, with the markup flattened.</summary>
    public void Record(string topic, string message)
    {
        var text = ToPlainText(message);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return;

        var title = lines[0];
        var detail = lines.Length > 1 ? string.Join(" · ", lines.Skip(1)) : null;
        store.AddEvent(topic, title, detail, DateTime.UtcNow);
    }

    /// <summary>Records an action performed on the panel, with an explicit title and detail.</summary>
    public void RecordPanel(string title, string? detail = null)
    {
        store.AddEvent(PanelTopic, title, detail, DateTime.UtcNow);
        logger.LogDebug("panel audit event: {t}", title);
    }

    public IReadOnlyList<PanelEvent> List(int limit = 50, string? topic = null)
    {
        if (topic != null && !Topics.Contains(topic))
            topic = null;
        return store.Events(limit, topic);
    }

    public void Prune() => store.Prune(OptionsReader.ReadPanelHistory(configuration).RetentionDays, options.RetentionDays, options.MaxEntries);

    /// <summary>Telegram messages use HTML; the panel shows readable text.</summary>
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = new StringBuilder(html.Length);
        var insideTag = false;
        foreach (var c in html)
        {
            if (c == '<')
            {
                insideTag = true;
                continue;
            }
            if (c == '>')
            {
                insideTag = false;
                continue;
            }
            if (!insideTag)
                text.Append(c);
        }

        var decoded = WebUtility.HtmlDecode(text.ToString());
        var lines = decoded
            .Split('\n')
            .Select(line => line.Trim().TrimEnd('_').Trim())
            .Where(line => line.Length > 0);

        return string.Join("\n", lines);
    }
}
