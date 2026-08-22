using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spaxtek.Ble.Core
{
    public interface IBleAdapter : IDisposable
    {
        BlePlatform Platform { get; }
        BleAdapterState State { get; }

        event Action<BleAdapterState> OnStateChanged;
        event Action<BleAdvertisement> OnAdvertisementReceived;
        event Action<BleError> OnError;

        Task InitializeAsync(CancellationToken cancellationToken);
        Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken);
        Task StopScanAsync(CancellationToken cancellationToken);
        Task<IBleConnection> ConnectAsync(BleDeviceId deviceId, CancellationToken cancellationToken);
    }
}
