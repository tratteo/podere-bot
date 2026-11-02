using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class PanelDeviceServiceTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), $"poderebot-device-{Guid.NewGuid():N}");
    private readonly PanelStore store;
    private readonly PanelDeviceService devices;

    public PanelDeviceServiceTests()
    {
        Directory.CreateDirectory(directory);
        store = new PanelStore(Path.Join(directory, "panel.db"), NullLogger<PanelStore>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Panel:LanHost"] = "poderebot.local",
                    ["Panel:LanPort"] = "5052",
                    ["Panel:PairingMinutes"] = "10",
                    ["Panel:SessionDays"] = "30",
                }
            )
            .Build();

        devices = new PanelDeviceService(store, configuration, NullLogger<PanelDeviceService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // Temp folder.
        }
    }

    [Fact]
    public void PairingLinkIsSingleUse()
    {
        var ticket = devices.CreatePairing("iPad cucina", "kiosk");
        var token = Token(ticket.Url);

        var first = devices.TryRedeem(token, out var error);
        Assert.Null(error);
        Assert.NotNull(first);
        Assert.Equal("iPad cucina", first!.Name);

        var second = devices.TryRedeem(token, out var secondError);
        Assert.Null(second);
        Assert.NotNull(secondError);
    }

    [Fact]
    public void PairingReturnsAQrCodeAndAStableUrl()
    {
        var ticket = devices.CreatePairing("Pixel", "kiosk");

        Assert.StartsWith("http://poderebot.local:5052/panel/pair?token=", ticket.Url);
        Assert.Contains("<svg", ticket.QrSvg);
        Assert.True(ticket.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public void DeviceNamesAreUnique()
    {
        devices.CreatePairing("iPad", "kiosk");

        var error = Assert.Throws<PanelConflictException>(() => devices.CreatePairing("ipad", "kiosk"));
        Assert.Contains("già un dispositivo", error.Message);
    }

    [Fact]
    public void EmptyNameIsRejected()
    {
        Assert.Throws<PanelValidationException>(() => devices.CreatePairing("   ", "kiosk"));
    }

    [Fact]
    public void ExpiredLinkIsRejected()
    {
        var ticket = devices.CreatePairing("Tablet", "kiosk");
        var token = Token(ticket.Url);

        using (var connection = store.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE panel_devices SET pairing_expires_at = 0 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", ticket.Id);
            command.ExecuteNonQuery();
        }

        Assert.Null(devices.TryRedeem(token, out var error));
        Assert.Contains("scaduto", error);
    }

    [Fact]
    public void AuthenticateResolvesAValidKeyAndRejectsItAfterRevoke()
    {
        var ticket = devices.CreatePairing("Telefono", "kiosk");
        var paired = devices.TryRedeem(Token(ticket.Url), out _)!;

        var session = devices.Authenticate(paired.Key);
        Assert.NotNull(session);
        Assert.Equal("Telefono", session!.Name);
        Assert.Equal("active", devices.List().Single().State);

        devices.Revoke(paired.Id);

        Assert.Null(devices.Authenticate(paired.Key));
        Assert.Equal("revoked", devices.List().Single().State);
    }

    [Fact]
    public void PendingDeviceShowsTheExpiryAndCanGetANewLink()
    {
        var ticket = devices.CreatePairing("Tablet", "kiosk");
        var device = devices.List().Single();
        Assert.Equal("pending", device.State);
        Assert.NotNull(device.PairingExpiresAt);

        var refreshed = devices.RefreshPairing(ticket.Id, "kiosk");
        Assert.Equal("Tablet", refreshed.Name);
        Assert.Null(devices.TryRedeem(Token(ticket.Url), out _));
        Assert.NotNull(devices.TryRedeem(Token(refreshed.Url), out _));
    }

    private static string Token(string? url)
    {
        Assert.NotNull(url);
        var query = new Uri(url!).Query.TrimStart('?');
        var value = query.Split('&').FirstOrDefault(part => part.StartsWith("token=", StringComparison.Ordinal));
        Assert.NotNull(value);
        return Uri.UnescapeDataString(value!["token=".Length..]);
    }
}
