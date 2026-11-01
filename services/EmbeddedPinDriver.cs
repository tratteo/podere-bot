using System.Device.Gpio;

namespace PodereBot.Services;

internal class EmbeddedPinDriver : IPinDriver
{
    private readonly GpioController gpioController;
    private readonly ILogger<EmbeddedPinDriver> logger;
    private readonly IConfiguration configuration;

    public EmbeddedPinDriver(ILogger<EmbeddedPinDriver> logger, IConfiguration configuration)
    {
        gpioController = new GpioController();
        this.logger = logger;
        this.configuration = configuration;
        InitializePins();
    }

    private void InitializePins()
    {
        for (int i = 0; i < gpioController.PinCount; i++)
        {
            try
            {
                gpioController.OpenPin(i);

                if (gpioController.IsPinModeSupported(i, PinMode.Output))
                {
                    gpioController.SetPinMode(i, PinMode.Output);
                    gpioController.Write(i, PinValue.Low);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("exception initializing pin {p}: {ex}", i, ex);
            }
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < gpioController.PinCount; i++)
        {
            gpioController.ClosePin(i);
        }
        gpioController.Dispose();
    }

    public void PinHigh(int? pin)
    {
        if (pin == null)
            return;

        try
        {
            gpioController.SetPinMode((int)pin, PinMode.Output);
            gpioController.Write((int)pin, PinValue.High);
            logger.LogDebug("embedded pin {p}: high", pin);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "error accessing pin {p}", pin);
        }
    }

    public void PinLow(int? pin)
    {
        if (pin == null)
            return;

        try
        {
            gpioController.SetPinMode((int)pin, PinMode.Output);
            gpioController.Write((int)pin, PinValue.Low);
            logger.LogDebug("embedded pin {p}: low", pin);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "error accessing pin {p}", pin);
        }
        return;
    }

    public int? DigitalRead(int? pin)
    {
        if (pin == null)
            return null;
        return gpioController.Read((int)pin) == PinValue.High ? 1 : 0;
    }
}
