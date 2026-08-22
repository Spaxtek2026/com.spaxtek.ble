using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;

namespace Spaxtek.Ble.Testing
{
    public sealed class FakeBleConnection : IBleConnection
    {
        private readonly BleGattService[] _services;
        private readonly HashSet<BleCharacteristicId> _subscriptions = new HashSet<BleCharacteristicId>();
        private readonly List<FakeBleWrite> _writes = new List<FakeBleWrite>();
        private readonly Dictionary<BleCharacteristicId, byte[]> _values = new Dictionary<BleCharacteristicId, byte[]>();
        private BleConnectionState _state = BleConnectionState.Connecting;
        private bool _disposed;

        public FakeBleConnection(BleDeviceId deviceId, IReadOnlyList<BleGattService> services)
        {
            DeviceId = deviceId;
            _services = CopyServices(services);
        }

        public BleDeviceId DeviceId { get; }
        public BleConnectionState State => _state;
        public IReadOnlyList<FakeBleWrite> Writes => _writes;

        public event Action<BleConnectionState> OnStateChanged;
        public event Action<BleNotification> OnNotificationReceived;
        public event Action<BleError> OnError;
        public event Action<FakeBleWrite> OnWriteReceived;

        public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(BleCacheMode cacheMode, CancellationToken cancellationToken)
        {
            RequireConnected();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BleGattService>>(_services);
        }

        public Task SubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken)
        {
            RequireConnected();
            cancellationToken.ThrowIfCancellationRequested();
            BleGattCharacteristic characteristic = FindCharacteristic(characteristicId);
            if (characteristic == null)
                return Task.FromException(new BleException(BleErrorCode.CharacteristicNotFound, $"Fake GATT characteristic was not found: {characteristicId}."));
            if ((characteristic.Properties & (BleCharacteristicProperties.Notify | BleCharacteristicProperties.Indicate)) == 0)
                return Task.FromException(new BleException(BleErrorCode.SubscriptionFailed, $"Fake GATT characteristic does not support notifications: {characteristicId}."));

            _subscriptions.Add(characteristicId);
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReadAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken)
        {
            RequireConnected();
            cancellationToken.ThrowIfCancellationRequested();
            BleGattCharacteristic characteristic = FindCharacteristic(characteristicId);
            if (characteristic == null)
            {
                BleException exception = new BleException(BleErrorCode.CharacteristicNotFound, $"Fake GATT characteristic was not found: {characteristicId}.");
                return Task.FromException<ReadOnlyMemory<byte>>(exception);
            }
            if ((characteristic.Properties & BleCharacteristicProperties.Read) == 0)
                return Task.FromException<ReadOnlyMemory<byte>>(new BleException(BleErrorCode.InvalidArgument, $"Fake GATT characteristic is not readable: {characteristicId}."));

            byte[] value = _values.TryGetValue(characteristicId, out byte[] storedValue) ? (byte[])storedValue.Clone() : Array.Empty<byte>();
            return Task.FromResult<ReadOnlyMemory<byte>>(value);
        }

        public Task UnsubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken)
        {
            RequireConnected();
            cancellationToken.ThrowIfCancellationRequested();
            _subscriptions.Remove(characteristicId);
            return Task.CompletedTask;
        }

        public Task<BleWriteResult> WriteAsync(BleCharacteristicId characteristicId, ReadOnlyMemory<byte> data, BleWriteMode writeMode, CancellationToken cancellationToken)
        {
            RequireConnected();
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length == 0)
                throw new ArgumentException("A BLE write payload cannot be empty.", nameof(data));

            BleGattCharacteristic characteristic = FindCharacteristic(characteristicId);
            if (characteristic == null)
                return Task.FromResult(BleWriteResult.Failure(BleErrorCode.CharacteristicNotFound, $"Fake GATT characteristic was not found: {characteristicId}."));

            BleCharacteristicProperties requiredProperty = writeMode == BleWriteMode.WithResponse
                ? BleCharacteristicProperties.Write
                : BleCharacteristicProperties.WriteWithoutResponse;
            if ((characteristic.Properties & requiredProperty) == 0)
                return Task.FromResult(BleWriteResult.Failure(BleErrorCode.WriteRejected, "The fake GATT characteristic does not support the requested write mode."));

            FakeBleWrite write = new FakeBleWrite(characteristicId, data.ToArray(), writeMode);
            _writes.Add(write);
            _values[characteristicId] = data.ToArray();
            OnWriteReceived?.Invoke(write);
            return Task.FromResult(BleWriteResult.Success("Fake GATT write completed."));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            RequireNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_state == BleConnectionState.Disconnected)
                return Task.CompletedTask;

            SetState(BleConnectionState.Disconnecting);
            _subscriptions.Clear();
            SetState(BleConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public bool EmitNotification(BleCharacteristicId characteristicId, byte[] data)
        {
            RequireConnected();
            if (!_subscriptions.Contains(characteristicId))
                return false;

            OnNotificationReceived?.Invoke(new BleNotification(characteristicId, data));
            return true;
        }

        public void EmitError(BleError error)
        {
            RequireNotDisposed();
            if (error == null)
                throw new ArgumentNullException(nameof(error));
            OnError?.Invoke(error);
        }

        public void SetValue(BleCharacteristicId characteristicId, byte[] data)
        {
            RequireNotDisposed();
            if (FindCharacteristic(characteristicId) == null)
                throw new BleException(BleErrorCode.CharacteristicNotFound, $"Fake GATT characteristic was not found: {characteristicId}.");
            _values[characteristicId] = data == null ? Array.Empty<byte>() : (byte[])data.Clone();
        }

        public void Connect()
        {
            RequireNotDisposed();
            SetState(BleConnectionState.Connected);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _subscriptions.Clear();
            _values.Clear();
            SetState(BleConnectionState.Disposed);
            OnStateChanged = null;
            OnNotificationReceived = null;
            OnError = null;
            OnWriteReceived = null;
        }

        private BleGattCharacteristic FindCharacteristic(BleCharacteristicId characteristicId)
        {
            for (int serviceIndex = 0; serviceIndex < _services.Length; serviceIndex++)
            {
                IReadOnlyList<BleGattCharacteristic> characteristics = _services[serviceIndex].Characteristics;
                for (int characteristicIndex = 0; characteristicIndex < characteristics.Count; characteristicIndex++)
                {
                    if (characteristics[characteristicIndex].Id == characteristicId)
                        return characteristics[characteristicIndex];
                }
            }
            return null;
        }

        private void SetState(BleConnectionState state)
        {
            if (_state == state)
                return;
            _state = state;
            OnStateChanged?.Invoke(state);
        }

        private void RequireConnected()
        {
            RequireNotDisposed();
            if (_state != BleConnectionState.Connected)
                throw new BleException(BleErrorCode.InvalidState, $"The fake BLE connection is not connected. Current state: {_state}.");
        }

        private void RequireNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FakeBleConnection));
        }

        private static BleGattService[] CopyServices(IReadOnlyList<BleGattService> services)
        {
            if (services == null || services.Count == 0)
                return Array.Empty<BleGattService>();

            BleGattService[] copy = new BleGattService[services.Count];
            for (int index = 0; index < services.Count; index++)
                copy[index] = services[index];
            return copy;
        }
    }

    public sealed class FakeBleWrite
    {
        private readonly byte[] _data;

        public FakeBleWrite(BleCharacteristicId characteristicId, byte[] data, BleWriteMode writeMode)
        {
            CharacteristicId = characteristicId;
            _data = data == null ? Array.Empty<byte>() : (byte[])data.Clone();
            WriteMode = writeMode;
        }

        public BleCharacteristicId CharacteristicId { get; }
        public ReadOnlyMemory<byte> Data => _data;
        public BleWriteMode WriteMode { get; }
    }
}
