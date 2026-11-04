using System.Globalization;
using PodereBot.Services;
using PodereBot.Services.Panel;

namespace PodereBot.Lib.Api;

internal static class PanelContext
{
    public const string SessionItem = "panel.session";
    public const string ModeItem = "panel.mode";

    public static PanelDeviceSession? Session(HttpContext context) =>
        context.Items[SessionItem] as PanelDeviceSession;

    public static string Mode(HttpContext context) =>
        context.Items[ModeItem] as string ?? (context.Connection.LocalPort == PanelOptions.KioskPort ? "kiosk" : "lan");

    public static string Actor(HttpContext context) => Session(context)?.Name ?? "kiosk";
}

internal record PanelIntervalBody
{
    public int From { get; init; }
    public int To { get; init; }
    public double Temperature { get; init; }
}

internal record PanelProgramBody
{
    public List<PanelIntervalBody>? Intervals { get; init; }
}

internal record PanelGateBody
{
    public string? Gate { get; init; }
}

internal record PanelWindowBody
{
    public int Hours { get; init; }
}

internal record PanelSuspendedBody
{
    public bool Suspended { get; init; }
}

internal record PanelUserBody
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public List<string>? Entitlements { get; init; }
    public List<string>? Topics { get; init; }
}

internal record PanelUserPatchBody
{
    public string? Name { get; init; }
    public List<string>? Entitlements { get; init; }
    public List<string>? Topics { get; init; }
}

internal record PanelDeviceBody
{
    public string? Name { get; init; }
}

internal record PanelPairBody
{
    public string? Token { get; init; }
}

/// <summary>
/// The panel API. It is served on the kiosk loopback port with the machine token and on the LAN
/// port with the device session cookie; the middleware decides which one applies.
/// </summary>
internal class PanelApi : IEndpoint
{
    public void MapEndpoint(WebApplication app)
    {
        var group = app.MapGroup("panel/api");

        group.MapGet("/overview", GetOverview).WithName("panel-overview");
        group.MapGet("/history", GetHistory).WithName("panel-history");
        group.MapGet("/events", GetEvents).WithName("panel-events");

        group.MapPost("/heating/on", (HttpContext c, HeatingControlService s, PanelSnapshotService snap, BoilerNotifier notifier) => Write(c, snap, () =>
        {
            s.SetBoiler(true);
            _ = notifier.NotifyAsync(true);
            return Task.CompletedTask;
        }, "Caldaia accesa"));
        group.MapPost("/heating/off", (HttpContext c, HeatingControlService s, PanelSnapshotService snap, BoilerNotifier notifier) => Write(c, snap, () =>
        {
            s.SetBoiler(false);
            _ = notifier.NotifyAsync(false);
            return Task.CompletedTask;
        }, "Caldaia spenta"));
        group.MapPut("/heating/program", (PanelProgramBody body, HttpContext c, HeatingControlService s, PanelSnapshotService snap) =>
            Write(c, snap, () =>
            {
                s.SetProgram(ToIntervals(body));
                return Task.CompletedTask;
            }, "Programma aggiornato")
        );
        group.MapDelete("/heating/program", (HttpContext c, HeatingControlService s, PanelSnapshotService snap) => Write(c, snap, () =>
        {
            s.ClearProgram();
            return Task.CompletedTask;
        }, "Programma rimosso"));
        group.MapPut("/heating/suspended", (PanelSuspendedBody body, HttpContext c, HeatingControlService s, PanelSnapshotService snap) =>
            Write(c, snap, () =>
            {
                s.SetSuspended(body.Suspended);
                return Task.CompletedTask;
            }, body.Suspended ? "Programma sospeso" : "Programma ripreso")
        );

        group.MapPost("/gates/open", (PanelGateBody body, HttpContext c, GateControlService s, PanelSnapshotService snap) =>
            Write(c, snap, async () =>
            {
                var gate = body.Gate?.Trim().ToLowerInvariant() switch
                {
                    "automatic" => GateId.automatic,
                    "pedestrian" => GateId.pedestrian,
                    _ => throw new PanelValidationException("Cancello non riconosciuto."),
                };
                await s.OpenAsync(gate, PanelContext.Actor(c));
            })
        );
        group.MapPut("/gates/window", (PanelWindowBody body, HttpContext c, GateControlService s, PanelSnapshotService snap) =>
            Write(c, snap, () =>
            {
                s.OpenWindow(body.Hours, PanelContext.Actor(c));
                return Task.CompletedTask;
            })
        );
        group.MapDelete("/gates/window", (HttpContext c, GateControlService s, PanelSnapshotService snap) => Write(c, snap, () =>
        {
            s.CloseWindow(PanelContext.Actor(c));
            return Task.CompletedTask;
        }));
        group.MapPost("/gates/lights", (HttpContext c, GateControlService s, PanelSnapshotService snap) => Write(c, snap, () =>
            s.ToggleLightsAsync(PanelContext.Actor(c))
        ));

        group.MapPost("/users", (PanelUserBody body, HttpContext c, UserAdminService s, PanelSnapshotService snap) =>
            Write(c, snap, () =>
            {
                s.Add(body.Id, body.Name, body.Entitlements, body.Topics);
                return Task.CompletedTask;
            }, $"Utente {body.Id} aggiunto")
        );
        group.MapPatch("/users/{id:long}", (long id, PanelUserPatchBody body, HttpContext c, UserAdminService s, PanelSnapshotService snap) =>
            Write(c, snap, () =>
            {
                s.Update(id, body.Name, body.Entitlements, body.Topics);
                return Task.CompletedTask;
            }, $"Utente {id} aggiornato")
        );
        group.MapDelete("/users/{id:long}", (long id, HttpContext c, UserAdminService s, PanelSnapshotService snap) =>
            Write(c, snap, () =>
            {
                s.Remove(id);
                return Task.CompletedTask;
            }, $"Utente {id} rimosso")
        );

        group.MapPost("/devices/pair", CreateDevice);
        group.MapPost("/devices/{id}/pair", RefreshDevice);
        group.MapGet("/devices", (HttpContext c, PanelDeviceService devices) => Results.Ok(new { devices = devices.List() }));
        group.MapDelete("/devices/{id}", RevokeDevice);

        group.MapPost("/pair", Pair).AllowAnonymous();
        group.MapPost("/logout", Logout).AllowAnonymous();
        group.MapGet("/whoami", WhoAmI).AllowAnonymous();
    }

    private static async Task<IResult> GetOverview(HttpContext context, PanelSnapshotService snapshot)
    {
        var overview = await snapshot.BuildAsync(PanelContext.Mode(context), PanelContext.Session(context));
        return Results.Ok(overview);
    }

    private static IResult GetHistory(string? range, string? from, string? to, PanelStore store)
    {
        try
        {
            var (start, end) = TemperatureHistory.Resolve(range, ParseLocal(from), ParseLocal(to), DateTime.Now);
            var rows = store.Hourly(start.ToUniversalTime(), end.ToUniversalTime().AddSeconds(1));
            var sensors = store.SensorHourly(start.ToUniversalTime(), end.ToUniversalTime().AddSeconds(1));
            var boiler = store.BoilerHourly(start.ToUniversalTime(), end.ToUniversalTime().AddSeconds(1));
            var result = TemperatureHistory.Build(
                rows,
                sensors,
                boiler,
                string.IsNullOrWhiteSpace(range) ? "today" : range!,
                start,
                end
            );
            return Results.Ok(result);
        }
        catch (PanelValidationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult GetEvents(int? limit, string? topic, NotificationLog log) =>
        Results.Ok(new { events = log.List(Math.Clamp(limit ?? 50, 1, 500), topic) });

    private static async Task<IResult> Write(
        HttpContext context,
        PanelSnapshotService snapshot,
        Func<Task> action,
        string? audit = null
    )
    {
        try
        {
            await action();
            if (audit != null)
                context.RequestServices.GetRequiredService<NotificationLog>().RecordPanel(audit, PanelContext.Actor(context));

            var overview = await snapshot.BuildAsync(PanelContext.Mode(context), PanelContext.Session(context));
            return Results.Ok(new PanelWriteResult(true, overview));
        }
        catch (PanelConflictException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (PanelValidationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = $"Errore interno: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult CreateDevice(HttpContext context, PanelDeviceBody body, PanelDeviceService devices, NotificationLog log)
    {
        try
        {
            var actor = PanelContext.Actor(context);
            var ticket = devices.CreatePairing(body.Name ?? string.Empty, actor);
            log.RecordPanel($"Dispositivo \"{ticket.Name}\" creato", $"da {actor}");
            return Results.Ok(ticket);
        }
        catch (PanelConflictException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (PanelValidationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult RefreshDevice(string id, HttpContext context, PanelDeviceService devices, NotificationLog log)
    {
        try
        {
            var actor = PanelContext.Actor(context);
            var ticket = devices.RefreshPairing(id, actor);
            log.RecordPanel($"Nuovo link per \"{ticket.Name}\"", $"da {actor}");
            return Results.Ok(ticket);
        }
        catch (PanelConflictException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (PanelValidationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult RevokeDevice(string id, HttpContext context, PanelDeviceService devices, NotificationLog log)
    {
        try
        {
            var device = devices.List().FirstOrDefault(d => d.Id == id);
            devices.Revoke(id);
            log.RecordPanel($"Dispositivo \"{device?.Name ?? id}\" revocato", $"da {PanelContext.Actor(context)}");
            return Results.Ok(new { ok = true, devices = devices.List() });
        }
        catch (PanelValidationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Pair(HttpContext context, PanelPairBody body, PanelDeviceService devices, NotificationLog log)
    {
        if (context.Connection.LocalPort == PanelOptions.KioskPort)
            return Results.Json(new { error = "Il pairing è disponibile solo sulla porta LAN." }, statusCode: 400);

        var paired = devices.TryRedeem(body.Token ?? string.Empty, out var error);
        if (paired == null)
            return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

        context.Response.Cookies.Append(
            PanelDeviceService.CookieName,
            paired.Key,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/panel",
                MaxAge = TimeSpan.FromDays(devices.Options.SessionDays),
                IsEssential = true,
            }
        );
        log.RecordPanel($"Dispositivo \"{paired.Name}\" accoppiato");
        return Results.Ok(new { ok = true, device = paired.Name });
    }

    private static IResult Logout(HttpContext context)
    {
        context.Response.Cookies.Delete(
            PanelDeviceService.CookieName,
            new CookieOptions { Path = "/panel", HttpOnly = true, SameSite = SameSiteMode.Strict }
        );
        return Results.Ok(new { ok = true });
    }

    private static IResult WhoAmI(HttpContext context, PanelDeviceService devices)
    {
        var key = context.Request.Cookies[PanelDeviceService.CookieName];
        var session = devices.Authenticate(key);
        return Results.Ok(new { authenticated = session != null, device = session?.Name });
    }

    private static IEnumerable<HeatingIntervalInput> ToIntervals(PanelProgramBody body)
    {
        var intervals = body.Intervals ?? [];
        foreach (var interval in intervals)
        {
            yield return new HeatingIntervalInput(
                interval.From / 60,
                interval.From % 60,
                interval.To / 60,
                interval.To % 60,
                interval.Temperature
            );
        }
    }

    private static DateTime? ParseLocal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : null;
    }
}
