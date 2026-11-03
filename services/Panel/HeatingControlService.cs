using PodereBot.Lib.Common;

namespace PodereBot.Services.Panel;

internal sealed record HeatingIntervalInput(
    int HoursFrom,
    int MinutesFrom,
    int HoursTo,
    int MinutesTo,
    double Temperature
);

/// <summary>
/// Heating changes requested from the panel, with the same rules as the Telegram command but
/// without the confirmation flow: the panel confirms in its own UI.
/// </summary>
internal sealed class HeatingControlService(HeatingDriver driver, Database db, ILogger<HeatingControlService> logger)
{
    private const double MinTemperature = 5;
    private const double MaxTemperature = 30;

    public void SetBoiler(bool on)
    {
        // The program owns the boiler while an interval runs: manual control would be undone
        // within one poll and the panel would lie about the state.
        if (db.Data.HeatingProgram is { IsSuspended: false } program && program.GetCurrentInterval() != null)
            throw new PanelValidationException("Programma in corso: la caldaia è gestita dal programma.");

        driver.SwitchHeating(on);
        db.Edit(d => d.ManualHeatingActive = on);
        logger.LogInformation("boiler switched {s} from the panel", on ? "on" : "off");
    }

    public void SetSuspended(bool suspended)
    {
        if (db.Data.HeatingProgram == null)
            throw new PanelValidationException("Nessun programma impostato.");

        db.Edit(d => d.HeatingProgram!.IsSuspended = suspended);
        if (suspended)
            driver.SwitchHeating(false);
    }

    public void SetProgram(IEnumerable<HeatingIntervalInput> inputs)
    {
        var intervals = new List<HeatingInterval>();
        foreach (var input in inputs)
        {
            if (input.HoursFrom is < 0 or > 24 || input.HoursTo is < 0 or > 24)
                throw new PanelValidationException("Le ore devono stare tra 00 e 24.");
            if (input.MinutesFrom is < 0 or > 59 || input.MinutesTo is < 0 or > 59)
                throw new PanelValidationException("I minuti devono stare tra 00 e 59.");
            if (input.HoursFrom == 24 && input.MinutesFrom != 0)
                throw new PanelValidationException("Le 24:00 non possono avere minuti.");
            if (input.HoursTo == 24 && input.MinutesTo != 0)
                throw new PanelValidationException("Le 24:00 non possono avere minuti.");
            if (input.Temperature < MinTemperature || input.Temperature > MaxTemperature)
                throw new PanelValidationException($"La temperatura deve stare tra {MinTemperature} e {MaxTemperature} °C.");

            intervals.Add(
                new HeatingInterval
                {
                    HoursFrom = input.HoursFrom,
                    MinutesFrom = input.MinutesFrom,
                    HoursTo = input.HoursTo,
                    MinutesTo = input.MinutesTo,
                    Temperature = (float)Math.Round(input.Temperature, 1),
                }
            );
        }

        if (intervals.Count == 0)
            throw new PanelValidationException("Aggiungi almeno una fascia.");

        intervals.Sort((a, b) => a.FromTimestamp.CompareTo(b.FromTimestamp));
        if (!HeatingProgram.TryBuild(intervals, out var program) || program == null)
            throw new PanelValidationException("Le fasce non possono sovrapporsi e devono iniziare prima di finire.");

        db.Edit(d =>
        {
            d.HeatingProgram = program;
            d.ManualHeatingActive = false;
        });
        logger.LogInformation("heating program replaced from the panel ({c} intervals)", intervals.Count);
    }

    public void ClearProgram()
    {
        var running = db.Data.HeatingProgram?.GetCurrentInterval() != null;
        db.Edit(d =>
        {
            d.HeatingProgram = null;
            d.ManualHeatingActive = false;
        });

        if (running)
            driver.SwitchHeating(false);
        logger.LogInformation("heating program removed from the panel");
    }
}
