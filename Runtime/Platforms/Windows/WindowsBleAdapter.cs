#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_ANDROID)
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;
using Spaxtek.Ble.Internal;

namespace Spaxtek.Ble.Platforms.Windows
{
    internal sealed class WindowsBleAdapter : IBleAdapter, IBleEventPump
    {
        private const int NativeEventInitialized = 1;
        private const int NativeEventScanStarted = 2;
        private const int NativeEventAdvertisement = 3;
        private const int NativeEventScanStopped = 4;
        private const int NativeEventConnectionState = 5;
        private const int NativeEventCharacteristicDiscovered = 6;
        private const int NativeEventDiscoveryComplete = 7;
        private const int NativeEventNotification = 8;
        private const int NativeEventOperationComplete = 9;
        private const int NativeEventError = 10;
        private const int TextCapacity = 1024;
        private const int DataCapacity = 65536;

        private readonly char[] _textA = new char[TextCapacity];
        private readonly char[] _textB = new char[TextCapacity];
        private readonly char[] _textC = new char[TextCapacity];
        private readonly byte[] _eventData = new byte[DataCapacity];
        private IntPtr _handle;
        private TaskCompletionSource<bool> _initializeCompletion;
        private TaskCompletionSource<bool> _scanCompletion;
        private TaskCompletionSource<IBleConnection> _connectCompletion;
        private WindowsBleConnection _connection;
        private BleAdapterState _state = BleAdapterState.Uninitialized;
        private bool _disposed;

        public BlePlatform Platform => BlePlatform.Windows;
        public BleAdapterState State => _state;

        public event Action<BleAdapterState> OnStateChanged;
        public event Action<BleAdvertisement> OnAdvertisementReceived;
        public event Action<BleError> OnError;

        internal IntPtr Handle => _handle;

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            RequireNotDisposed();
            if (_state == BleAdapterState.Ready || _state == BleAdapterState.Scanning)
                return;
            if (_state == BleAdapterState.Initializing && _initializeCompletion != null)
            {
                await _initializeCompletion.Task;
                return;
            }

            try
            {
                EnsureNativeHandle();
                _initializeCompletion = CreateCompletion<bool>();
                using CancellationTokenRegistration registration = cancellationToken.Register(() => _initializeCompletion.TrySetCanceled());
                SetState(BleAdapterState.Initializing);
                ThrowIfNativeRejected(WindowsBleNative.spax_ble_initialize(_handle), "initialize Windows BLE");
                await _initializeCompletion.Task;
            }
            catch (DllNotFoundException exception)
            {
                HandleNativeLibraryFailure(exception);
                throw new BleException(BleErrorCode.Unsupported, "SpaxtekBleWindows.dll is missing or could not be loaded.", 0, exception);
            }
            catch (EntryPointNotFoundException exception)
            {
                HandleNativeLibraryFailure(exception);
                throw new BleException(BleErrorCode.Unsupported, "SpaxtekBleWindows.dll has an incompatible API version.", 0, exception);
            }
        }

        public async Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken)
        {
            RequireReady();
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            bool connectionAttemptActive = _connectCompletion != null && !_connectCompletion.Task.IsCompleted;
            bool connectionActive = _connection != null && _connection.State != BleConnectionState.Disconnected && _connection.State != BleConnectionState.Disposed;
            if (connectionAttemptActive || connectionActive)
                throw new BleException(BleErrorCode.OperationBusy, "Scanning is unavailable while a Windows BLE connection is active.");
            if (_state == BleAdapterState.Scanning)
                await StopScanAsync(cancellationToken);

            _scanCompletion = CreateCompletion<bool>();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _scanCompletion.TrySetCanceled());
            string serviceUuid = filter.ServiceUuid?.ToString("D") ?? string.Empty;
            SetState(BleAdapterState.Scanning);
            try
            {
                int result = WindowsBleNative.spax_ble_start_scan(_handle, filter.DeviceName ?? string.Empty, serviceUuid, filter.AllowDuplicates ? 1 : 0);
                ThrowIfNativeRejected(result, "start Windows BLE scan");
                await _scanCompletion.Task;
            }
            catch (Exception)
            {
                if (!_disposed && _state == BleAdapterState.Scanning)
                    await StopScanAsync(CancellationToken.None);
                throw;
            }
        }

        public Task StopScanAsync(CancellationToken cancellationToken)
        {
            RequireNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_state != BleAdapterState.Scanning)
                return Task.CompletedTask;

            ThrowIfNativeRejected(WindowsBleNative.spax_ble_stop_scan(_handle), "stop Windows BLE scan");
            SetState(BleAdapterState.Ready);
            return Task.CompletedTask;
        }

        public async Task<IBleConnection> ConnectAsync(BleDeviceId deviceId, CancellationToken cancellationToken)
        {
            RequireReady();
            if (deviceId.Platform != BlePlatform.Windows)
                throw new ArgumentException("The device identifier does not belong to the Windows BLE adapter.", nameof(deviceId));
            if (_connectCompletion != null && !_connectCompletion.Task.IsCompleted)
                throw new BleException(BleErrorCode.OperationBusy, "A Windows BLE connection attempt is already active.");
            if (_state == BleAdapterState.Scanning)
                await StopScanAsync(cancellationToken);
            if (_connection != null && _connection.State != BleConnectionState.Disconnected && _connection.State != BleConnectionState.Disposed)
                throw new BleException(BleErrorCode.OperationBusy, "Another Windows BLE connection is active.");

            _connection?.Dispose();
            _connection = new WindowsBleConnection(this, deviceId);
            _connectCompletion = CreateCompletion<IBleConnection>();
            using CancellationTokenRegistration registration = cancellationToken.Register(CancelPendingConnection);
            ThrowIfNativeRejected(WindowsBleNative.spax_ble_connect(_handle, deviceId.Value), "connect to Windows BLE device");
            return await _connectCompletion.Task;
        }

        // Fixed UTF-16 arrays keep the native capacity independent of IL2CPP StringBuilder marshalling.
        private static string ReadText(char[] buffer)
        {
            int length = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, length < 0 ? buffer.Length : length);
        }

        public void PumpEvents(int maximumEventCount)
        {
            if (_disposed || _handle == IntPtr.Zero || maximumEventCount <= 0)
                return;

            for (int index = 0; index < maximumEventCount; index++)
            {
                WindowsBleNative.NativeEventHeader header = default;
                _textA[0] = '\0';
                _textB[0] = '\0';
                _textC[0] = '\0';
                int result = WindowsBleNative.spax_ble_poll_event(_handle, ref header, _textA, TextCapacity, _textB, TextCapacity, _textC, TextCapacity, _eventData, DataCapacity);
                if (result == 0)
                    return;
                if (result < 0)
                {
                    OnError?.Invoke(new BleError(BleErrorCode.PlatformError, "The Windows BLE native event buffer was too small.", result));
                    return;
                }

                ProcessNativeEvent(header, ReadText(_textA), ReadText(_textB), ReadText(_textC));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _initializeCompletion?.TrySetException(new ObjectDisposedException(nameof(WindowsBleAdapter)));
            _scanCompletion?.TrySetException(new ObjectDisposedException(nameof(WindowsBleAdapter)));
            _connectCompletion?.TrySetException(new ObjectDisposedException(nameof(WindowsBleAdapter)));
            _connection?.Dispose();
            _connection = null;

            if (_handle != IntPtr.Zero)
            {
                WindowsBleNative.spax_ble_destroy(_handle);
                _handle = IntPtr.Zero;
            }

            SetState(BleAdapterState.Disposed);
            OnStateChanged = null;
            OnAdvertisementReceived = null;
            OnError = null;
        }

        internal void DiscoverServices(long operationId, BleCacheMode cacheMode)
        {
            ThrowIfNativeRejected(WindowsBleNative.spax_ble_discover_services(_handle, operationId, cacheMode == BleCacheMode.Uncached ? 1 : 0), "discover Windows GATT services");
        }

        internal void SetSubscription(long operationId, BleCharacteristicId characteristicId, bool enabled)
        {
            string serviceUuid = characteristicId.ServiceUuid.ToString("D");
            string valueUuid = characteristicId.CharacteristicUuid.ToString("D");
            int result = WindowsBleNative.spax_ble_set_subscription(_handle, operationId, serviceUuid, valueUuid, enabled ? 1 : 0);
            ThrowIfNativeRejected(result, "update Windows GATT subscription");
        }

        internal void Read(long operationId, BleCharacteristicId characteristicId)
        {
            string serviceUuid = characteristicId.ServiceUuid.ToString("D");
            string valueUuid = characteristicId.CharacteristicUuid.ToString("D");
            int result = WindowsBleNative.spax_ble_read(_handle, operationId, serviceUuid, valueUuid);
            ThrowIfNativeRejected(result, "read Windows GATT characteristic");
        }

        internal void Write(long operationId, BleCharacteristicId characteristicId, byte[] data, BleWriteMode writeMode)
        {
            string serviceUuid = characteristicId.ServiceUuid.ToString("D");
            string valueUuid = characteristicId.CharacteristicUuid.ToString("D");
            int result = WindowsBleNative.spax_ble_write(_handle, operationId, serviceUuid, valueUuid, data, data.Length, (int)writeMode);
            ThrowIfNativeRejected(result, "write Windows GATT characteristic");
        }

        internal void Disconnect()
        {
            ThrowIfNativeRejected(WindowsBleNative.spax_ble_disconnect(_handle), "disconnect Windows BLE device");
        }

        private void ProcessNativeEvent(WindowsBleNative.NativeEventHeader header, string textA, string textB, string textC)
        {
            switch (header.EventType)
            {
                case NativeEventInitialized:
                    HandleInitialized(header, textA);
                    break;
                case NativeEventScanStarted:
                    HandleScanStarted(header, textA);
                    break;
                case NativeEventAdvertisement:
                    HandleAdvertisement(header, textA, textB, textC);
                    break;
                case NativeEventScanStopped:
                    SetState(BleAdapterState.Ready);
                    break;
                case NativeEventConnectionState:
                    HandleConnectionState(header, textA);
                    break;
                case NativeEventCharacteristicDiscovered:
                    _connection?.HandleCharacteristicDiscovered(textA, textB, header.ValueA);
                    break;
                case NativeEventDiscoveryComplete:
                    _connection?.HandleDiscoveryComplete(header.OperationId, header.Success != 0, header.ErrorCode, textA, header.NativeCode);
                    break;
                case NativeEventNotification:
                    _connection?.HandleNotification(textA, textB, CopyEventData(header.DataLength));
                    break;
                case NativeEventOperationComplete:
                    _connection?.HandleOperationComplete(header.OperationId, header.Success != 0, header.ErrorCode, textA, header.NativeCode, CopyEventData(header.DataLength));
                    break;
                case NativeEventError:
                    OnError?.Invoke(new BleError(MapError(header.ErrorCode), textA, header.NativeCode));
                    break;
            }
        }

        private void HandleInitialized(WindowsBleNative.NativeEventHeader header, string detail)
        {
            if (header.Success != 0)
            {
                SetState(BleAdapterState.Ready);
                _initializeCompletion?.TrySetResult(true);
                return;
            }

            BleException exception = new BleException(MapError(header.ErrorCode), detail, header.NativeCode);
            SetState(BleAdapterState.Unavailable);
            _initializeCompletion?.TrySetException(exception);
            OnError?.Invoke(new BleError(exception.ErrorCode, exception.Message, exception.NativeCode));
        }

        private void HandleScanStarted(WindowsBleNative.NativeEventHeader header, string detail)
        {
            if (header.Success != 0)
            {
                SetState(BleAdapterState.Scanning);
                _scanCompletion?.TrySetResult(true);
                return;
            }

            BleException exception = new BleException(MapError(header.ErrorCode), detail, header.NativeCode);
            SetState(BleAdapterState.Ready);
            _scanCompletion?.TrySetException(exception);
            OnError?.Invoke(new BleError(exception.ErrorCode, exception.Message, exception.NativeCode));
        }

        private void HandleAdvertisement(WindowsBleNative.NativeEventHeader header, string deviceId, string name, string serviceUuids)
        {
            if (_state != BleAdapterState.Scanning || string.IsNullOrWhiteSpace(deviceId))
                return;

            BleDeviceId parsedDeviceId = new BleDeviceId(BlePlatform.Windows, deviceId);
            IReadOnlyList<Guid> parsedServiceUuids = ParseUuids(serviceUuids);
            byte[] manufacturerData = CopyEventData(header.DataLength);
            BleAdvertisement advertisement = new BleAdvertisement(parsedDeviceId, name, header.ValueA, header.ValueB != 0, parsedServiceUuids, manufacturerData);
            OnAdvertisementReceived?.Invoke(advertisement);
        }

        private void HandleConnectionState(WindowsBleNative.NativeEventHeader header, string detail)
        {
            if (_connection == null)
                return;

            bool connected = header.ValueA != 0;
            _connection.HandleConnectionState(connected, header.ErrorCode, detail, header.NativeCode);
            if (connected)
            {
                _connectCompletion?.TrySetResult(_connection);
                return;
            }

            if (_connectCompletion != null && !_connectCompletion.Task.IsCompleted)
            {
                BleException exception = new BleException(MapError(header.ErrorCode == 0 ? (int)BleErrorCode.DeviceUnreachable : header.ErrorCode), detail, header.NativeCode);
                _connectCompletion.TrySetException(exception);
                OnError?.Invoke(new BleError(exception.ErrorCode, exception.Message, exception.NativeCode));
            }
        }

        private void EnsureNativeHandle()
        {
            if (_handle != IntPtr.Zero)
                return;

            _handle = WindowsBleNative.spax_ble_create();
            if (_handle == IntPtr.Zero)
                throw new BleException(BleErrorCode.PlatformError, "Unable to create the Windows BLE native context.");
        }

        private void CancelPendingConnection()
        {
            _connectCompletion?.TrySetCanceled();
            if (_handle != IntPtr.Zero)
                WindowsBleNative.spax_ble_disconnect(_handle);
        }

        private void HandleNativeLibraryFailure(Exception exception)
        {
            SetState(BleAdapterState.Unavailable);
            OnError?.Invoke(new BleError(BleErrorCode.Unsupported, exception.Message));
        }

        private void SetState(BleAdapterState state)
        {
            if (_state == state)
                return;

            _state = state;
            OnStateChanged?.Invoke(state);
        }

        private void RequireReady()
        {
            RequireNotDisposed();
            if (_state != BleAdapterState.Ready && _state != BleAdapterState.Scanning)
                throw new BleException(BleErrorCode.InvalidState, $"The Windows BLE adapter is not ready. Current state: {_state}.");
        }

        private void RequireNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WindowsBleAdapter));
        }

        private byte[] CopyEventData(int dataLength)
        {
            if (dataLength <= 0)
                return Array.Empty<byte>();

            int copyLength = Math.Min(dataLength, _eventData.Length);
            byte[] copy = new byte[copyLength];
            Buffer.BlockCopy(_eventData, 0, copy, 0, copyLength);
            return copy;
        }

        private static IReadOnlyList<Guid> ParseUuids(string value)
        {
            List<Guid> result = new List<Guid>();
            if (string.IsNullOrWhiteSpace(value))
                return result;

            string[] values = value.Split(',');
            for (int index = 0; index < values.Length; index++)
            {
                if (Guid.TryParse(values[index], out Guid uuid))
                    result.Add(uuid);
            }
            return result;
        }

        private static void ThrowIfNativeRejected(int result, string operation)
        {
            if (result == 0)
                return;

            throw new BleException(MapError(result), $"Unable to {operation}. Native result: {result}.", result);
        }

        private static BleErrorCode MapError(int errorCode)
        {
            return Enum.IsDefined(typeof(BleErrorCode), errorCode) ? (BleErrorCode)errorCode : BleErrorCode.PlatformError;
        }

        private static TaskCompletionSource<T> CreateCompletion<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
#endif
