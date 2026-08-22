#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;

namespace Spaxtek.Ble.Platforms.Android
{
    internal sealed class AndroidBleConnection : IBleConnection
    {
        private readonly AndroidBleAdapter _adapter;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<NativeOperationResult>> _pendingOperations =
            new ConcurrentDictionary<long, TaskCompletionSource<NativeOperationResult>>();
        private readonly object _discoveryLock = new object();
        private readonly Dictionary<Guid, List<BleGattCharacteristic>> _discoveredCharacteristics = new Dictionary<Guid, List<BleGattCharacteristic>>();
        private TaskCompletionSource<IReadOnlyList<BleGattService>> _discoveryCompletion;
        private TaskCompletionSource<bool> _disconnectCompletion;
        private long _discoveryOperationId;
        private long _nextOperationId;
        private BleConnectionState _state = BleConnectionState.Connecting;
        private bool _disposed;

        public AndroidBleConnection(AndroidBleAdapter adapter, BleDeviceId deviceId)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            DeviceId = deviceId;
        }

        public BleDeviceId DeviceId { get; }
        public BleConnectionState State => _state;

        public event Action<BleConnectionState> OnStateChanged;
        public event Action<BleNotification> OnNotificationReceived;
        public event Action<BleError> OnError;

        public async Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(BleCacheMode cacheMode, CancellationToken cancellationToken)
        {
            RequireConnected();
            lock (_discoveryLock)
            {
                if (_discoveryCompletion != null && !_discoveryCompletion.Task.IsCompleted)
                    throw new BleException(BleErrorCode.OperationBusy, "GATT discovery is already in progress.");

                _discoveredCharacteristics.Clear();
                _discoveryOperationId = NextOperationId();
                _discoveryCompletion = CreateCompletion<IReadOnlyList<BleGattService>>();
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(() => _discoveryCompletion.TrySetCanceled());
            _adapter.Bridge.Call("discoverServices", _discoveryOperationId, cacheMode == BleCacheMode.Uncached);
            return await _discoveryCompletion.Task;
        }

        public Task SubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken)
        {
            return SetSubscriptionAsync(characteristicId, true, cancellationToken);
        }

        public async Task<ReadOnlyMemory<byte>> ReadAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken)
        {
            RequireConnected();
            long operationId = NextOperationId();
            TaskCompletionSource<NativeOperationResult> completion = RegisterOperation(operationId);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancelOperation(operationId));
            _adapter.Bridge.Call("read", operationId, characteristicId.ServiceUuid.ToString("D"), characteristicId.CharacteristicUuid.ToString("D"));
            NativeOperationResult result = await completion.Task;
            if (!result.Success)
                throw new BleException(result.ErrorCode, result.Detail);
            return result.Data;
        }

        public Task UnsubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken)
        {
            return SetSubscriptionAsync(characteristicId, false, cancellationToken);
        }

        public async Task<BleWriteResult> WriteAsync(BleCharacteristicId characteristicId, ReadOnlyMemory<byte> data, BleWriteMode writeMode, CancellationToken cancellationToken)
        {
            RequireConnected();
            if (data.Length == 0)
                throw new ArgumentException("A BLE write payload cannot be empty.", nameof(data));

            long operationId = NextOperationId();
            TaskCompletionSource<NativeOperationResult> completion = RegisterOperation(operationId);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancelOperation(operationId));
            byte[] bytes = data.ToArray();
            string serviceUuid = characteristicId.ServiceUuid.ToString("D");
            string characteristicUuid = characteristicId.CharacteristicUuid.ToString("D");
            _adapter.Bridge.Call("write", operationId, serviceUuid, characteristicUuid, AndroidBleUtility.ToSignedBytes(bytes), (int)writeMode);
            NativeOperationResult result = await completion.Task;
            return result.Success ? BleWriteResult.Success(result.Detail) : BleWriteResult.Failure(result.ErrorCode, result.Detail);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            RequireNotDisposed();
            if (_state == BleConnectionState.Disconnected)
                return;
            if (_state == BleConnectionState.Disconnecting && _disconnectCompletion != null)
            {
                await _disconnectCompletion.Task;
                return;
            }

            SetState(BleConnectionState.Disconnecting);
            _disconnectCompletion = CreateCompletion<bool>();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _disconnectCompletion.TrySetCanceled());
            _adapter.Bridge.Call("disconnect");
            await _disconnectCompletion.Task;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            BleConnectionState previousState = _state;
            _disposed = true;
            if (previousState != BleConnectionState.Disconnected && previousState != BleConnectionState.Disconnecting)
            {
                try
                {
                    _adapter.Bridge.Call("disconnect");
                }
                catch (Exception)
                {
                }
            }

            ObjectDisposedException exception = new ObjectDisposedException(nameof(AndroidBleConnection));
            foreach (KeyValuePair<long, TaskCompletionSource<NativeOperationResult>> pair in _pendingOperations)
                pair.Value.TrySetException(exception);
            _pendingOperations.Clear();
            _discoveryCompletion?.TrySetException(exception);
            _disconnectCompletion?.TrySetResult(true);
            SetState(BleConnectionState.Disposed);
            OnStateChanged = null;
            OnNotificationReceived = null;
            OnError = null;
        }

        internal void HandleConnectionState(bool connected, int errorCode, string detail)
        {
            if (_disposed)
                return;

            if (connected)
            {
                SetState(BleConnectionState.Connected);
                return;
            }

            BleConnectionState previousState = _state;
            SetState(BleConnectionState.Disconnected);
            _disconnectCompletion?.TrySetResult(true);
            FailPendingOperations(new BleException(MapError(errorCode), string.IsNullOrWhiteSpace(detail) ? "Android BLE connection was closed." : detail, errorCode));
            if (previousState != BleConnectionState.Disconnecting && errorCode != 0)
                OnError?.Invoke(new BleError(MapError(errorCode), detail, errorCode));
        }

        internal void HandleCharacteristicDiscovered(string serviceUuid, string characteristicUuid, int properties)
        {
            if (!Guid.TryParse(serviceUuid, out Guid parsedServiceUuid) || !Guid.TryParse(characteristicUuid, out Guid parsedCharacteristicUuid))
                return;

            BleCharacteristicId characteristicId = new BleCharacteristicId(parsedServiceUuid, parsedCharacteristicUuid);
            BleGattCharacteristic characteristic = new BleGattCharacteristic(characteristicId, (BleCharacteristicProperties)properties);
            lock (_discoveryLock)
            {
                if (!_discoveredCharacteristics.TryGetValue(parsedServiceUuid, out List<BleGattCharacteristic> characteristics))
                {
                    characteristics = new List<BleGattCharacteristic>();
                    _discoveredCharacteristics.Add(parsedServiceUuid, characteristics);
                }

                characteristics.Add(characteristic);
            }
        }

        internal void HandleDiscoveryComplete(long operationId, bool success, int errorCode, string detail)
        {
            if (operationId != _discoveryOperationId || _discoveryCompletion == null)
                return;

            if (!success)
            {
                BleException exception = new BleException(MapError(errorCode), detail, errorCode);
                _discoveryCompletion.TrySetException(exception);
                OnError?.Invoke(new BleError(exception.ErrorCode, exception.Message, exception.NativeCode));
                return;
            }

            List<BleGattService> services = new List<BleGattService>();
            lock (_discoveryLock)
            {
                foreach (KeyValuePair<Guid, List<BleGattCharacteristic>> pair in _discoveredCharacteristics)
                    services.Add(new BleGattService(pair.Key, pair.Value));
            }

            _discoveryCompletion.TrySetResult(services);
        }

        internal void HandleNotification(string serviceUuid, string characteristicUuid, byte[] data)
        {
            if (_disposed || !Guid.TryParse(serviceUuid, out Guid parsedServiceUuid) || !Guid.TryParse(characteristicUuid, out Guid parsedCharacteristicUuid))
                return;

            OnNotificationReceived?.Invoke(new BleNotification(new BleCharacteristicId(parsedServiceUuid, parsedCharacteristicUuid), data));
        }

        internal void HandleOperationComplete(long operationId, bool success, int errorCode, string detail, byte[] data = null)
        {
            if (!_pendingOperations.TryRemove(operationId, out TaskCompletionSource<NativeOperationResult> completion))
                return;

            completion.TrySetResult(new NativeOperationResult(success, MapError(errorCode), detail, data));
            if (!success)
                OnError?.Invoke(new BleError(MapError(errorCode), detail, errorCode));
        }

        private async Task SetSubscriptionAsync(BleCharacteristicId characteristicId, bool enabled, CancellationToken cancellationToken)
        {
            RequireConnected();
            long operationId = NextOperationId();
            TaskCompletionSource<NativeOperationResult> completion = RegisterOperation(operationId);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancelOperation(operationId));
            _adapter.Bridge.Call("setSubscription", operationId, characteristicId.ServiceUuid.ToString("D"), characteristicId.CharacteristicUuid.ToString("D"), enabled);
            NativeOperationResult result = await completion.Task;
            if (!result.Success)
                throw new BleException(result.ErrorCode, result.Detail);
        }

        private TaskCompletionSource<NativeOperationResult> RegisterOperation(long operationId)
        {
            TaskCompletionSource<NativeOperationResult> completion = CreateCompletion<NativeOperationResult>();
            if (!_pendingOperations.TryAdd(operationId, completion))
                throw new BleException(BleErrorCode.OperationBusy, $"BLE operation {operationId} is already registered.");

            return completion;
        }

        private void CancelOperation(long operationId)
        {
            if (_pendingOperations.TryRemove(operationId, out TaskCompletionSource<NativeOperationResult> completion))
                completion.TrySetCanceled();
        }

        private void FailPendingOperations(Exception exception)
        {
            foreach (KeyValuePair<long, TaskCompletionSource<NativeOperationResult>> pair in _pendingOperations)
            {
                if (_pendingOperations.TryRemove(pair.Key, out TaskCompletionSource<NativeOperationResult> completion))
                    completion.TrySetException(exception);
            }

            _discoveryCompletion?.TrySetException(exception);
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
                throw new BleException(BleErrorCode.InvalidState, $"The Android BLE connection is not connected. Current state: {_state}.");
        }

        private void RequireNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AndroidBleConnection));
        }

        private long NextOperationId()
        {
            return Interlocked.Increment(ref _nextOperationId);
        }

        private static TaskCompletionSource<T> CreateCompletion<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static BleErrorCode MapError(int errorCode)
        {
            return Enum.IsDefined(typeof(BleErrorCode), errorCode) ? (BleErrorCode)errorCode : BleErrorCode.PlatformError;
        }

        private readonly struct NativeOperationResult
        {
            private readonly byte[] _data;

            public NativeOperationResult(bool success, BleErrorCode errorCode, string detail, byte[] data)
            {
                Success = success;
                ErrorCode = errorCode;
                Detail = detail ?? string.Empty;
                _data = data == null ? Array.Empty<byte>() : (byte[])data.Clone();
            }

            public bool Success { get; }
            public BleErrorCode ErrorCode { get; }
            public string Detail { get; }
            public ReadOnlyMemory<byte> Data => _data;
        }
    }
}
#endif
