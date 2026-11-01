using System.IO.Ports;

namespace PodereBot.Services;

internal class SerialPinDriver : IPinDriver
{
    private const int BAUD_RATE = 115200;

    private readonly ILogger logger;
    private readonly SerialPort? serialPort;

    public SerialPinDriver(ILogger<SerialPinDriver> logger, IConfiguration configuration)
    {
        this.logger = logger;
        string? port = configuration.GetValue<string>("SerialPort")!;
        try
        {
            serialPort = new SerialPort(port, BAUD_RATE);
            serialPort.Open();
            logger.LogInformation("serial port ({p}) opened", serialPort.PortName);
        }
        catch (Exception ex)
        {
            logger.LogWarning("error opening serial bus: {ex}", ex);
        }
    }

    public void PinHigh(int? pin)
    {
        if (pin == null)
            return;
        try
        {
            serialPort?.Write($"h{pin}");
            logger.LogDebug("serial pin {p}: high", pin);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "error writing to serial port {p}", serialPort);
        }
    }

    public int? DigitalRead(int? pin)
    {
        return null;
    }

    public void PinLow(int? pin)
    {
        if (pin == null)
            return;
        try
        {
            serialPort?.Write($"l{pin}");
            logger.LogDebug("serial pin {p}: low", pin);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "error writing to serial port {p}", serialPort);
        }
    }
}
