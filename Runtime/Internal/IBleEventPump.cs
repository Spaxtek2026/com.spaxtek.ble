namespace Spaxtek.Ble.Internal
{
    internal interface IBleEventPump
    {
        void PumpEvents(int maximumEventCount);
    }
}
