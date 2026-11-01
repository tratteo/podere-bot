internal interface IPinDriver
{
    int? DigitalRead(int? pin);
    void PinHigh(int? pin);
    void PinLow(int? pin);
}
