namespace PodereBot.Services.Panel;

/// <summary>The panel acts as an admin device; it has a synthetic identity that is never a chat id.</summary>
internal static class PanelIdentity
{
    public const long UserId = 0;
    public const string Name = "Pannello";
}

/// <summary>A request conflicts with the current state (duplicate name, last admin, ...).</summary>
internal sealed class PanelConflictException(string message) : Exception(message);

/// <summary>The request payload is invalid; the message is shown to the user as is.</summary>
internal sealed class PanelValidationException(string message) : Exception(message);

internal sealed record PanelDeviceInfo(
    string Id,
    string Name,
    string State,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    string? CreatedBy,
    DateTime? PairingExpiresAt
);

/// <summary>The one-time link returned by a pairing request. The QR is only ever shown once.</summary>
internal sealed record PairingTicket(
    string Id,
    string Name,
    string? Url,
    string? QrSvg,
    DateTime ExpiresAt
);

internal sealed record PairedDevice(string Id, string Name, string Key);

internal sealed record PanelDeviceSession(string Id, string Name)
{
    public static readonly PanelDeviceSession Kiosk = new("kiosk", PanelIdentity.Name);
}
