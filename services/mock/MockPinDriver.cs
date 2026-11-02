namespace PodereBot.Services;

internal class MockPinDriver(ILogger<MockPinDriver> logger) : IPinDriver
{
    private readonly ILogger logger = logger;
    readonly Dictionary<int, int> pins = [];

    public int? DigitalRead(int? pin)
    {
        if (pin == null)
            return null;
        if (pins.TryGetValue((int)pin, out var pinValue))
        {
            return pinValue;
        }
        return null;
    }

    public void PinHigh(int? pin)
    {
        if (pin == null)
            return;
        if (pins.ContainsKey((int)pin))
        {
            pins[(int)pin] = 1;
        }
        else
        {
            pins.Add((int)pin, 1);
        }
    }

    public void PinLow(int? pin)
    {
        if (pin == null)
            return;
        if (pins.ContainsKey((int)pin))
        {
            pins[(int)pin] = 0;
        }
        else
        {
            pins.Add((int)pin, 0);
        }
    }
}
