using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spaxtek.Ble.Core
{
    public interface IBleConnection : IDisposable
    {
        BleDeviceId DeviceId { get; }
        BleConnectionState State { get; }

        event Action<BleConnectionState> OnStateChanged;
        event Action<BleNotification> OnNotificationReceived;
        event Action<BleError> OnError;

        Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(BleCacheMode cacheMode, CancellationToken cancellationToken);
        Task<ReadOnlyMemory<byte>> ReadAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken);
        Task SubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken);
        Task UnsubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken);
        Task<BleWriteResult> WriteAsync(BleCharacteristicId characteristicId, ReadOnlyMemory<byte> data, BleWriteMode writeMode, CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
