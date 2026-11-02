using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using PodereBot.Lib.Common;
using PodereBot.Services.Hosted;
using PodereBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using PodereBot.Lib;

namespace PodereBot.Services.AiTools;

internal sealed class AiToolRegistry
{
    public IReadOnlyList<AIFunction> AllAiFunctions { get; }
    public IReadOnlyList<McpServerTool> AllMcpServerTools { get; }

    public AiToolRegistry(
        ITemperatureDriver temperatureDriver,
        GateDriver gateDriver,
        Database db,
        IConfiguration configuration,
        HeatingDriver heatingDriver,
        BotHostedService bot,
        UserDirectory users,
        GateAccessService gateAccess,
        ILogger<AiToolRegistry> logger
    )
    {
        var functions = new List<AIFunction>();

        // ===== Info Tools =====

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    var proc = Process.GetCurrentProcess();
                    var runningTime = DateTime.Now - proc.StartTime;
                    var memory = proc.PrivateMemorySize64 / 1E6;
                    return $"Memoria: {memory:0.000} MB\nUptime: {runningTime.Days}g {runningTime.Hours}h {runningTime.Minutes}m {runningTime.Seconds}s";
                },
                new AIFunctionFactoryOptions { Name = "get_status", Description = "Restituisce le statistiche di sistema (uptime e memoria)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                async () =>
                {
                    var sb = new StringBuilder();
                    var local = await temperatureDriver.GetLocalTemperature();
                    sb.AppendLine($"Host: {(local.HasValue ? $"{local:F2}°C" : "N/A")}");
                    foreach (var r in temperatureDriver.GetExternalTemperatureReadings())
                        sb.AppendLine($"{r.Location}: {r.Temperature:F2}°C ({r.Timestamp:HH:mm})");
                    return sb.ToString();
                },
                new AIFunctionFactoryOptions { Name = "get_temperatures", Description = "Legge le temperature di tutti i sensori disponibili" }
            )
        );

        // ===== Gate Control Tools =====

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorizeGateOpening(users, db))
                        return "I cancelli sono bloccati al momento. Solo gli amministratori possono aprirli.";
                    var gateUserId = AiChatService.GetCurrentUserId();
                    if (gateUserId == null)
                        return "Impossibile identificare l'utente.";
                    if (!gateAccess.TryRegisterOpening(gateUserId.Value, out var retryAfter))
                        return $"Hai aperto il cancello troppe volte. Riprova tra {(int)Math.Ceiling(retryAfter.TotalMinutes)} minuti.";
                    gateDriver.Open(GateId.automatic).GetAwaiter().GetResult();
                    return "Cancello automatico aperto.";
                },
                new AIFunctionFactoryOptions { Name = "open_automatic_gate", Description = "Apre il cancello automatico. Solo admin può aprirlo se i cancelli non sono sbloccati." }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorizeGateOpening(users, db))
                        return "I cancelli sono bloccati al momento. Solo gli amministratori possono aprirli.";
                    var gateUserId = AiChatService.GetCurrentUserId();
                    if (gateUserId == null)
                        return "Impossibile identificare l'utente.";
                    if (!gateAccess.TryRegisterOpening(gateUserId.Value, out var retryAfter))
                        return $"Hai aperto il cancello troppe volte. Riprova tra {(int)Math.Ceiling(retryAfter.TotalMinutes)} minuti.";
                    gateDriver.Open(GateId.pedestrian).GetAwaiter().GetResult();
                    return "Cancello pedonale aperto.";
                },
                new AIFunctionFactoryOptions { Name = "open_pedestrian_gate", Description = "Apre il cancello pedonale. Solo admin può aprirlo se i cancelli non sono sbloccati." }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    gateDriver.ToggleLights().GetAwaiter().GetResult();
                    return "Ho attivato/disattivato le luci dei cancelli. Non posso sapere in che stato sono.";
                },
                new AIFunctionFactoryOptions { Name = "toggle_gate_lights", Description = "Accende o spegne le luci dei cancelli (richiede permessi admin)" }
            )
        );

        // ===== Gate Access Tools =====

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    var expiration = db.Data.GatesOpenAccessExpirationDate;
                    if (!GateAccessPolicy.IsOpen(expiration, DateTime.Now))
                        return "I cancelli sono bloccati. Solo gli amministratori possono aprirli.";
                    return $"I cancelli sono sbloccati fino al {expiration:dd/MM/yyyy HH:mm}.";
                },
                new AIFunctionFactoryOptions { Name = "get_gate_access_status", Description = "Mostra lo stato di accesso ai cancelli (richiede permessi admin)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                (int hours) =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    if (hours < 1 || hours > 24)
                        return "Le ore devono essere tra 1 e 24.";
                    var start = DateTime.Now;
                    var expiration = start.AddHours(hours);
                    db.Edit(d =>
                    {
                        d.GatesOpenAccessStartDate = start;
                        d.GatesOpenAccessExpirationDate = expiration;
                    });
                    return $"Cancelli sbloccati fino al {expiration:dd/MM/yyyy HH:mm}.";
                },
                new AIFunctionFactoryOptions { Name = "set_gate_access_hours", Description = "Abilita l'accesso ai cancelli per tutti gli utenti per un numero di ore (richiede permessi admin)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                (bool confirm) =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    if (!confirm)
                        return "Operazione distruttiva: chiedi conferma esplicita all'utente e richiama lo strumento con confirm=true.";
                    db.Edit(d =>
                    {
                        d.GatesOpenAccessExpirationDate = null;
                        d.GatesOpenAccessStartDate = null;
                    });
                    return "Cancelli bloccati. Solo gli amministratori possono utilizzarli.";
                },
                new AIFunctionFactoryOptions { Name = "lock_gates", Description = "Blocca l'accesso ai cancelli per tutti gli utenti non admin (richiede permessi admin)" }
            )
        );

        // ===== Heating Tools =====

        functions.Add(
            AIFunctionFactory.Create(
                async () =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    var sb = new StringBuilder();
                    var temp = await heatingDriver.GetOperationalTemperature();
                    sb.AppendLine($"Temperatura: {(temp.HasValue ? $"{temp:F2}°C" : "N/A")}");
                    sb.AppendLine($"Caldaia: {(heatingDriver.IsBoilerActive() ? "accesa" : "spenta")}");
                    sb.AppendLine();
                    var program = db.Data.HeatingProgram;
                    if (program == null)
                    {
                        sb.AppendLine("Nessun programma impostato.");
                        sb.AppendLine(db.Data.ManualHeatingActive ? "Riscaldamento acceso manualmente." : "Riscaldamento spento.");
                    }
                    else
                    {
                        if (program.IsSuspended)
                            sb.AppendLine("Programma SOSPESO");
                        sb.AppendLine($"Programma:\n{program}");
                        sb.AppendLine($"Codice: {program.ToCodeString()}");
                        var interval = program.GetCurrentInterval();
                        if (interval != null && !program.IsSuspended)
                            sb.AppendLine($"Intervallo attivo: {interval.Temperature}° fino alle {interval.HoursTo:D2}:{interval.MinutesTo:D2}");
                    }
                    return sb.ToString();
                },
                new AIFunctionFactoryOptions { Name = "get_heating_program", Description = "Mostra il programma di riscaldamento attuale e lo stato della caldaia (richiede permessi admin)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                async (string program) =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    var splits = program.Split('/');
                    var intervals = new List<HeatingInterval>();
                    var matcher = @"(?<hfrom>[0-2]?\d):?(?<mfrom>[0-5]\d)?-(?<hto>[0-2]?\d):?(?<mto>[0-5]\d)?@(?<temp>[0-9]+(\.[0-9]+)?)";
                    foreach (var s in splits)
                    {
                        var m = Regex.Match(s, matcher);
                        if (!int.TryParse(m.Groups["hfrom"]?.Value, out var hoursFrom) || !int.TryParse(m.Groups["hto"]?.Value, out var hoursTo))
                            return $"Formato non valido per '{s}'. Usa hh:mm-hh:mm@temp.";
                        if (!float.TryParse(m.Groups["temp"]?.Value, CultureInfo.InvariantCulture, out var temp))
                            return $"Temperatura non valida per '{s}'.";
                        if (!int.TryParse(m.Groups["mfrom"]?.Value, out var minFrom))
                            minFrom = 0;
                        if (!int.TryParse(m.Groups["mto"]?.Value, out var minTo))
                            minTo = 0;
                        if (hoursFrom > 24 || hoursTo > 24)
                            return "Le ore non possono essere maggiori di 24.";
                        if (hoursFrom > hoursTo || (hoursFrom == hoursTo && minFrom > minTo))
                            return $"L'intervallo '{s}' non è valido: inizio dopo la fine.";
                        intervals.Add(new HeatingInterval
                        {
                            HoursFrom = hoursFrom,
                            MinutesFrom = minFrom,
                            HoursTo = hoursTo,
                            MinutesTo = minTo,
                            Temperature = temp
                        });
                    }
                    if (!HeatingProgram.TryBuild(intervals, out var built))
                        return "Gli intervalli non sono ordinati correttamente o si sovrappongono.";
                    db.Edit(d => d.HeatingProgram = built);
                    var temperature = await heatingDriver.GetOperationalTemperature();
                    var current = built!.GetCurrentInterval();
                    if (current != null && temperature < current.Temperature - 1)
                        heatingDriver.SwitchHeating(true);
                    return $"Programma aggiornato:\n{built}";
                },
                new AIFunctionFactoryOptions { Name = "set_heating_program", Description = "Imposta il programma di riscaldamento (richiede permessi admin). Formato: hh:mm-hh:mm@temp/hh:mm-hh:mm@temp" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    heatingDriver.SwitchHeating(true);
                    db.Edit(d => d.ManualHeatingActive = true);
                    return "Riscaldamento acceso.";
                },
                new AIFunctionFactoryOptions { Name = "heating_on", Description = "Accende manualmente il riscaldamento (richiede permessi admin)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    heatingDriver.SwitchHeating(false);
                    db.Edit(d => d.ManualHeatingActive = false);
                    return "Riscaldamento spento.";
                },
                new AIFunctionFactoryOptions { Name = "heating_off", Description = "Spegne manualmente il riscaldamento (richiede permessi admin)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                (bool confirm) =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    if (!confirm)
                        return "Operazione distruttiva: chiedi conferma esplicita all'utente e richiama lo strumento con confirm=true.";
                    db.Edit(d => d.HeatingProgram = null);
                    heatingDriver.SwitchHeating(false);
                    return "Programma di riscaldamento rimosso.";
                },
                new AIFunctionFactoryOptions { Name = "delete_heating_program", Description = "Rimuove il programma di riscaldamento (richiede permessi admin)" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    if (!AdminAuthorization.TryAuthorize(users))
                        return "Non autorizzato: servono permessi admin.";
                    if (db.Data.HeatingProgram == null)
                        return "Nessun programma da sospendere.";
                    var wasSuspended = db.Data.HeatingProgram.IsSuspended;
                    db.Edit(d => d.HeatingProgram!.IsSuspended = !d.HeatingProgram.IsSuspended);
                    if (!wasSuspended)
                    {
                        heatingDriver.SwitchHeating(false);
                        return "Programma sospeso. Il riscaldamento può essere controllato manualmente.";
                    }
                    else
                    {
                        return "Programma riattivato.";
                    }
                },
                new AIFunctionFactoryOptions { Name = "suspend_heating_program", Description = "Sospende o riprende il programma di riscaldamento (richiede permessi admin)" }
            )
        );

        // ===== Telegram Tools =====

        functions.Add(
            AIFunctionFactory.Create(
                async (long chatId) =>
                {
                    await bot.Client.SendVenue(chatId, 41.4986180363405, 12.797875896442752, "Podere 739 (canne libere)", "Via del Valloncello 16");
                    return "Posizione inviata.";
                },
                new AIFunctionFactoryOptions { Name = "send_position", Description = "Invia la posizione GPS della casa a una chat Telegram" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                async (long chatId) =>
                {
                    await bot.Client.SendMessage(
                        chatId,
                        $"""
                        Per i comandi usa il pannello accanto alla tastiera.

                        <b>Regole</b>
                        <blockquote>1. Se entri dal cancello automatico, aspetta che si chiuda prima di proseguire (controlla che non escano i cani)</blockquote>
                        <blockquote>2. I cani si spostano, basta muoversi in maniera costante e lineare, no accelerazioni o frenate brusche</blockquote>
                        <blockquote>3. Chiudi sempre il cancello pedonale</blockquote>
                        <blockquote>4. Se non c'è troppo fango, lascia la macchina nel parco di fronte alla legnaia</blockquote>
                        """,
                        parseMode: ParseMode.Html,
                        disableNotification: true
                    );
                    return "Messaggio di benvenuto inviato.";
                },
                new AIFunctionFactoryOptions { Name = "send_welcome", Description = "Invia il messaggio di benvenuto con le regole della casa a una chat Telegram" }
            )
        );

        functions.Add(
            AIFunctionFactory.Create(
                (string userDataJson) =>
                {
                    return $"Informazioni utente ricevute:\n\n{userDataJson}";
                },
                new AIFunctionFactoryOptions { Name = "get_user_info", Description = "Formatta e restituisce le informazioni di un utente Telegram in formato leggibile" }
            )
        );


        AllAiFunctions = functions.AsReadOnly();
        AllMcpServerTools = functions.ConvertAll(f => McpServerTool.Create(f));
    }

}
