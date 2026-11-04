using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using QRCoder;

namespace PodereBot.Services.Panel;

/// <summary>
/// Device access for the LAN panel. The name is a label chosen by an admin; the identity is the
/// key handed to the device once, through a single-use pairing link that is shown as a QR code.
/// Client IP addresses and hostnames are never used as authorisation.
/// </summary>
internal sealed class PanelDeviceService(
    PanelStore store,
    IConfiguration configuration,
    ILogger<PanelDeviceService> logger
)
{
    public const string CookieName = "poderebot_panel";

    // A paired panel polls every few seconds: rewriting the stamp on every request would keep the
    // SQLite write-ahead log busy and wear the SD card. Five minutes is enough for the device list.
    private const long SeenIntervalSeconds = 300;

    private readonly PanelOptions options = OptionsReader.ReadPanel(configuration);

    public PanelOptions Options => options;

    public IReadOnlyList<PanelDeviceInfo> List()
    {
        var result = new List<PanelDeviceInfo>();
        if (!store.Available)
            return result;

        try
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, pairing_hash, pairing_expires_at, created_at, created_by, last_seen_at, revoked_at
                FROM panel_devices
                WHERE revoked_at IS NULL
                ORDER BY created_at DESC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pairing = !reader.IsDBNull(2);
                var pairingExpires = reader.IsDBNull(3) ? (DateTime?)null : FromUnix(reader.GetInt64(3));
                result.Add(
                    new PanelDeviceInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        DeriveState(pairing, pairingExpires, reader.IsDBNull(6) ? null : FromUnix(reader.GetInt64(6)), !reader.IsDBNull(7)),
                        FromUnix(reader.GetInt64(4)),
                        reader.IsDBNull(6) ? null : FromUnix(reader.GetInt64(6)),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        pairingExpires
                    )
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to list panel devices: {m}", ex.Message);
        }
        return result;
    }

    /// <summary>Creates a device and its single-use link. The raw link is returned only here.</summary>
    public PairingTicket CreatePairing(string name, string createdBy)
    {
        var cleanName = ValidateName(name);
        var host = RequireHost();
        var token = NewSecret();
        var expiresAt = DateTime.UtcNow.AddMinutes(options.PairingMinutes);
        var id = Guid.NewGuid().ToString("n");

        try
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO panel_devices(id, name, key_hash, pairing_hash, pairing_expires_at, created_at, created_by, last_seen_at, revoked_at)
                VALUES($id, $name, NULL, $pairing, $expires, $created, $by, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", cleanName);
            command.Parameters.AddWithValue("$pairing", Hash(token));
            command.Parameters.AddWithValue("$expires", new DateTimeOffset(expiresAt).ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$by", createdBy);
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new PanelConflictException($"Esiste già un dispositivo chiamato \"{cleanName}\".");
        }
        catch (Exception ex)
        {
            logger.LogError("unable to create a panel device: {e}", ex);
            throw new PanelConflictException("Non riesco a salvare il dispositivo.");
        }

        logger.LogInformation("pairing link created for {n} by {b}", cleanName, createdBy);
        return BuildTicket(id, cleanName, host, token, expiresAt);
    }

    /// <summary>Issues a new link for a device that has not been paired yet; the old link dies.</summary>
    public PairingTicket RefreshPairing(string id, string createdBy)
    {
        var device = List().FirstOrDefault(d => d.Id == id);
        if (device == null)
            throw new PanelValidationException("Dispositivo inesistente.");
        if (device.State == "active")
            throw new PanelConflictException("Il dispositivo è già attivo: revocalo per crearne uno nuovo.");

        var host = RequireHost();
        var token = NewSecret();
        var expiresAt = DateTime.UtcNow.AddMinutes(options.PairingMinutes);
        using (var connection = store.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE panel_devices
                SET pairing_hash = $pairing, pairing_expires_at = $expires, created_by = $by, revoked_at = NULL
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$pairing", Hash(token));
            command.Parameters.AddWithValue("$expires", new DateTimeOffset(expiresAt).ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$by", createdBy);
            command.ExecuteNonQuery();
        }

        return BuildTicket(id, device.Name, host, token, expiresAt);
    }

    /// <summary>Redeems a single-use link: the device key is generated now and returned only now.</summary>
    public PairedDevice? TryRedeem(string token, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Link di pairing non valido.";
            return null;
        }

        var hash = Hash(token);
        string? id = null;
        string? name = null;
        DateTime? expires = null;

        using (var connection = store.Open())
        {
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT id, name, pairing_expires_at
                FROM panel_devices
                WHERE pairing_hash = $pairing AND revoked_at IS NULL;
                """;
            select.Parameters.AddWithValue("$pairing", hash);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                id = reader.GetString(0);
                name = reader.GetString(1);
                expires = reader.IsDBNull(2) ? null : FromUnix(reader.GetInt64(2));
            }
        }

        if (id == null || name == null)
        {
            error = "Questo link non è più valido.";
            return null;
        }

        if (expires == null || expires <= DateTime.UtcNow)
        {
            error = "Questo link è scaduto: generane uno nuovo dal pannello.";
            return null;
        }

        var key = NewSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var connection = store.Open())
        using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE panel_devices
                SET key_hash = $key, pairing_hash = NULL, pairing_expires_at = NULL, last_seen_at = $now
                WHERE id = $id AND pairing_hash = $pairing AND revoked_at IS NULL;
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$pairing", hash);
            update.Parameters.AddWithValue("$key", Hash(key));
            update.Parameters.AddWithValue("$now", now);
            if (update.ExecuteNonQuery() != 1)
            {
                error = "Questo link è già stato usato.";
                return null;
            }
        }

        logger.LogInformation("panel device {n} paired", name);
        return new PairedDevice(id, name, key);
    }

    /// <summary>Resolves the cookie value to a device, in constant time against the stored hashes.</summary>
    public PanelDeviceSession? Authenticate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !store.Available)
            return null;

        var candidate = HexToBytes(Hash(key));
        try
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, key_hash, last_seen_at
                FROM panel_devices
                WHERE revoked_at IS NULL AND key_hash IS NOT NULL;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var stored = HexToBytes(reader.GetString(2));
                if (stored.Length != candidate.Length || !CryptographicOperations.FixedTimeEquals(stored, candidate))
                    continue;

                var id = reader.GetString(0);
                Touch(id);
                return new PanelDeviceSession(id, reader.GetString(1));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("unable to authenticate a panel device: {m}", ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Revoking deletes the device: the cookie stops matching, the list stays clean and the name
    /// becomes available again. The audit event keeps the trace of who removed it.
    /// </summary>
    public void Revoke(string id)
    {
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM panel_devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new PanelValidationException("Dispositivo inesistente.");
    }

    public void MarkSeen(string id) => Touch(id);

    private void Touch(string id)
    {
        try
        {
            using var connection = store.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE panel_devices SET last_seen_at = $now "
                + "WHERE id = $id AND (last_seen_at IS NULL OR last_seen_at < $now - $interval);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$interval", SeenIntervalSeconds);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            logger.LogDebug("unable to update the last seen stamp: {m}", ex.Message);
        }
    }

    private PairingTicket BuildTicket(string id, string name, string host, string token, DateTime expiresAt)
    {
        var url = $"http://{host}:{options.LanPort}/panel/pair?token={Uri.EscapeDataString(token)}";
        return new PairingTicket(id, name, url, BuildQr(url), expiresAt);
    }

    /// <summary>The QR must point at a stable origin: a reserved address or a local name.</summary>
    private string RequireHost()
    {
        var host = ResolveHost();
        if (host != null)
            return host;

        throw new PanelConflictException(
            "Nessun indirizzo LAN disponibile. Imposta Panel:LanHost (IP riservato o poderebot.local) e riprova."
        );
    }

    private static string BuildQr(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var svg = new SvgQRCode(data);
        return svg.GetGraphic(8, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>The QR must point at a stable origin: a reserved address or a local name.</summary>
    private string? ResolveHost()
    {
        if (!string.IsNullOrWhiteSpace(options.LanHost))
            return options.LanHost;

        try
        {
            var candidates = NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Where(a => !IPAddress.IsLoopback(a))
                .Where(a => !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .OrderByDescending(Score)
                .ToList();

            return candidates.FirstOrDefault()?.ToString();
        }
        catch (Exception ex)
        {
            logger.LogDebug("unable to detect the LAN address: {m}", ex.Message);
            return null;
        }
    }

    /// <summary>Home LANs are usually 192.168.x; virtual adapters often sit on 172.16/12.</summary>
    private static int Score(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168)
            return 3;
        if (bytes[0] == 10)
            return 2;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            return 1;
        return 0;
    }

    private static string ValidateName(string? name)
    {
        var clean = name?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
            throw new PanelValidationException("Il nome del dispositivo è obbligatorio.");
        if (clean.Length > 60)
            throw new PanelValidationException("Il nome del dispositivo è troppo lungo (massimo 60 caratteri).");
        return clean;
    }

    private static string DeriveState(bool pairing, DateTime? pairingExpires, DateTime? lastSeen, bool revoked)
    {
        if (revoked)
            return "revoked";
        if (pairing)
            return pairingExpires == null || pairingExpires > DateTime.UtcNow ? "pending" : "expired";
        if (lastSeen != null)
            return "active";
        return "expired";
    }

    private static string NewSecret() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static byte[] HexToBytes(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
}
