#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;
using UnityEngine;
using UnityEngine.Android;

namespace Spaxtek.Ble.Platforms.Android
{
    internal sealed class AndroidBleAdapter : IBleAdapter
    {
        private const string BluetoothScanPermission = "android.permission.BLUETOOTH_SCAN";
        private const string BluetoothConnectPermission = "android.permission.BLUETOOTH_CONNECT";
        private const string FineLocationPermission = "android.permission.ACCESS_FINE_LOCATION";
        private const string JavaBridgeClass = "com.spaxtek.ble.SpaxtekBleBridge";

        private AndroidJavaObject _bridge;
        private AndroidBleListenerProxy _listener;
        private PermissionCallbacks _permissionCallbacks;
        private TaskCompletionSource<bool> _initializeCompletion;
        private TaskCompletionSource<bool> _scanCompletion;
        private TaskCompletionSource<IBleConnection> _connectCompletion;
        private AndroidBleConnection _connection;
        private BleAdapterState _state = BleAdapterState.Uninitialized;
        private bool _disposed;

        public BlePlatform Platform => BlePlatform.Android;
        public BleAdapterState State => _state;

        public event Action<BleAdapterState> OnStateChanged;
        public event Action<BleAdvertisement> OnAdvertisementReceived;
        public event Action<BleError> OnError;

        internal AndroidJavaObject Bridge => _bridge;

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

            SetState(BleAdapterState.Initializing);
            await RequestPermissionsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            EnsureBridge();
            _initializeCompletion = CreateCompletion<bool>();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _initializeCompletion.TrySetCanceled());
            _bridge.Call("initialize");
            await _initializeCompletion.Task;
        }

        public async Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken)
        {
            RequireReady();
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            bool connectionAttemptActive = _connectCompletion != null && !_connectCompletion.Task.IsCompleted;
            bool connectionActive = _connection != null && _connection.State != BleConnectionState.Disconnected && _connection.State != BleConnectionState.Disposed;
            if (connectionAttemptActive || connectionActive)
                throw new BleException(BleErrorCode.OperationBusy, "Scanning is unavailable while an Android BLE connection is active.");
            if (_state == BleAdapterState.Scanning)
                await StopScanAsync(cancellationToken);

            _scanCompletion = CreateCompletion<bool>();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _scanCompletion.TrySetCanceled());
            string serviceUuid = filter.ServiceUuid?.ToString("D") ?? string.Empty;
            SetState(BleAdapterState.Scanning);
            try
            {
                _bridge.Call("startScan", filter.DeviceName ?? string.Empty, serviceUuid, filter.AllowDuplicates);
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

            _bridge?.Call("stopScan");
            SetState(BleAdapterState.Ready);
            return Task.CompletedTask;
        }

        public async Task<IBleConnection> ConnectAsync(BleDeviceId deviceId, CancellationToken cancellationToken)
        {
            RequireReady();
            if (deviceId.Platform != BlePlatform.Android)
                throw new ArgumentException("The device identifier does not belong to the Android BLE adapter.", nameof(deviceId));
            if (_connectCompletion != null && !_connectCompletion.Task.IsCompleted)
                throw new BleException(BleErrorCode.OperationBusy, "An Android BLE connection attempt is already active.");
            if (_state == BleAdapterState.Scanning)
                await StopScanAsync(cancellationToken);
            if (_connection != null && _connection.State != BleConnectionState.Disconnected && _connection.State != BleConnectionState.Disposed)
                throw new BleException(BleErrorCode.OperationBusy, "Another Android BLE connection is active.");

            _connection?.Dispose();
            _connection = new AndroidBleConnection(this, deviceId);
            _connectCompletion = CreateCompletion<IBleConnection>();
            using CancellationTokenRegistration registration = cancellationToken.Register(CancelPendingConnection);
            _bridge.Call("connect", deviceId.Value);
            return await _connectCompletion.Task;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _initializeCompletion?.TrySetException(new ObjectDisposedException(nameof(AndroidBleAdapter)));
            _scanCompletion?.TrySetException(new ObjectDisposedException(nameof(AndroidBleAdapter)));
            _connectCompletion?.TrySetException(new ObjectDisposedException(nameof(AndroidBleAdapter)));
            _connection?.Dispose();
            _connection = null;

            try
            {
                _bridge?.Call("dispose");
            }
            catch (Exception)
            {
            }

            _bridge?.Dispose();
            _bridge = null;
            _listener = null;
            _permissionCallbacks = null;
            SetState(BleAdapterState.Disposed);
            OnStateChanged = null;
            OnAdvertisementReceived = null;
            OnError = null;
        }

        internal void HandleInitialized(bool success, int errorCode, string detail)
        {
            if (success)
            {
                SetState(BleAdapterState.Ready);
                _initializeCompletion?.TrySetResult(true);
                return;
            }

            BleException exception = CreateException(errorCode, detail);
            SetState(BleAdapterState.Unavailable);
            _initializeCompletion?.TrySetException(exception);
            RaiseError(exception);
        }

        internal void HandleScanStarted()
        {
            _scanCompletion?.TrySetResult(true);
        }

        internal void HandleAdvertisement(string deviceId, string name, int rssi, bool isConnectable, string serviceUuids, sbyte[] manufacturerData)
        {
            if (_state != BleAdapterState.Scanning || string.IsNullOrWhiteSpace(deviceId))
                return;

            List<Guid> parsedServiceUuids = ParseUuids(serviceUuids);
            byte[] parsedManufacturerData = AndroidBleUtility.ToUnsignedBytes(manufacturerData);
            BleDeviceId parsedDeviceId = new BleDeviceId(BlePlatform.Android, deviceId);
            BleAdvertisement advertisement = new BleAdvertisement(parsedDeviceId, name, rssi, isConnectable, parsedServiceUuids, parsedManufacturerData);
            OnAdvertisementReceived?.Invoke(advertisement);
        }

        internal void HandleScanFailed(int errorCode, string detail)
        {
            BleException exception = CreateException(errorCode, detail);
            SetState(BleAdapterState.Ready);
            _scanCompletion?.TrySetException(exception);
            RaiseError(exception);
        }

        internal void HandleConnectionState(bool connected, int errorCode, string detail)
        {
            if (_connection == null)
                return;

            _connection.HandleConnectionState(connected, errorCode, detail);
            if (connected)
            {
                _connectCompletion?.TrySetResult(_connection);
                return;
            }

            if (_connectCompletion != null && !_connectCompletion.Task.IsCompleted)
            {
                BleException exception = CreateException(errorCode == 0 ? (int)BleErrorCode.DeviceUnreachable : errorCode, detail);
                _connectCompletion.TrySetException(exception);
                RaiseError(exception);
            }
        }

        internal void HandleCharacteristicDiscovered(string serviceUuid, string characteristicUuid, int properties)
        {
            _connection?.HandleCharacteristicDiscovered(serviceUuid, characteristicUuid, properties);
        }

        internal void HandleDiscoveryComplete(long operationId, bool success, int errorCode, string detail)
        {
            _connection?.HandleDiscoveryComplete(operationId, success, errorCode, detail);
        }

        internal void HandleNotification(string serviceUuid, string characteristicUuid, sbyte[] data)
        {
            _connection?.HandleNotification(serviceUuid, characteristicUuid, AndroidBleUtility.ToUnsignedBytes(data));
        }

        internal void HandleOperationComplete(long operationId, bool success, int errorCode, string detail)
        {
            _connection?.HandleOperationComplete(operationId, success, errorCode, detail);
        }

        internal void HandleReadComplete(long operationId, bool success, int errorCode, string detail, sbyte[] data)
        {
            _connection?.HandleOperationComplete(operationId, success, errorCode, detail, AndroidBleUtility.ToUnsignedBytes(data));
        }

        private async Task RequestPermissionsAsync(CancellationToken cancellationToken)
        {
            string[] permissions = GetRequiredPermissions();
            if (ArePermissionsGranted(permissions))
                return;

            TaskCompletionSource<bool> completion = CreateCompletion<bool>();
            _permissionCallbacks = new PermissionCallbacks();
            _permissionCallbacks.PermissionGranted += _ => CompletePermissionsIfGranted(permissions, completion);
            _permissionCallbacks.PermissionDenied += permission =>
                completion.TrySetException(new BleException(BleErrorCode.PermissionDenied, $"Android BLE permission was denied: {permission}."));
            using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled());
            Permission.RequestUserPermissions(permissions, _permissionCallbacks);
            await completion.Task;
            _permissionCallbacks = null;
        }

        private void EnsureBridge()
        {
            if (_bridge != null)
                return;

            using AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            _listener = new AndroidBleListenerProxy(this);
            _bridge = new AndroidJavaObject(JavaBridgeClass, activity, _listener);
        }

        private void CancelPendingConnection()
        {
            _connectCompletion?.TrySetCanceled();
            try
            {
                _bridge?.Call("disconnect");
            }
            catch (Exception)
            {
            }
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
                throw new BleException(BleErrorCode.InvalidState, $"The Android BLE adapter is not ready. Current state: {_state}.");
        }

        private void RequireNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AndroidBleAdapter));
        }

        private void RaiseError(BleException exception)
        {
            OnError?.Invoke(new BleError(exception.ErrorCode, exception.Message, exception.NativeCode));
        }

        private static string[] GetRequiredPermissions()
        {
            return GetAndroidSdkVersion() >= 31 ? new[] { BluetoothScanPermission, BluetoothConnectPermission } : new[] { FineLocationPermission };
        }

        private static bool ArePermissionsGranted(IReadOnlyList<string> permissions)
        {
            for (int index = 0; index < permissions.Count; index++)
            {
                if (!Permission.HasUserAuthorizedPermission(permissions[index]))
                    return false;
            }

            return true;
        }

        private static void CompletePermissionsIfGranted(IReadOnlyList<string> permissions, TaskCompletionSource<bool> completion)
        {
            if (ArePermissionsGranted(permissions))
                completion.TrySetResult(true);
        }

        private static int GetAndroidSdkVersion()
        {
            using AndroidJavaClass version = new AndroidJavaClass("android.os.Build$VERSION");
            return version.GetStatic<int>("SDK_INT");
        }

        private static List<Guid> ParseUuids(string value)
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

        private static TaskCompletionSource<T> CreateCompletion<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static BleException CreateException(int errorCode, string detail)
        {
            BleErrorCode code = Enum.IsDefined(typeof(BleErrorCode), errorCode) ? (BleErrorCode)errorCode : BleErrorCode.PlatformError;
            return new BleException(code, string.IsNullOrWhiteSpace(detail) ? "Android BLE operation failed." : detail, errorCode);
        }

        private sealed class AndroidBleListenerProxy : AndroidJavaProxy
        {
            private readonly AndroidBleAdapter _owner;

            public AndroidBleListenerProxy(AndroidBleAdapter owner)
                : base("com.spaxtek.ble.SpaxtekBleBridge$Listener")
            {
                _owner = owner;
            }

            public void onInitialized(bool success, int errorCode, string detail)
            {
                _owner.HandleInitialized(success, errorCode, detail);
            }

            public void onScanStarted()
            {
                _owner.HandleScanStarted();
            }

            public void onAdvertisement(string deviceId, string name, int rssi, bool isConnectable, string serviceUuids, sbyte[] manufacturerData)
            {
                _owner.HandleAdvertisement(deviceId, name, rssi, isConnectable, serviceUuids, manufacturerData);
            }

            public void onScanFailed(int errorCode, string detail)
            {
                _owner.HandleScanFailed(errorCode, detail);
            }

            public void onConnectionState(bool connected, int errorCode, string detail)
            {
                _owner.HandleConnectionState(connected, errorCode, detail);
            }

            public void onCharacteristicDiscovered(string serviceUuid, string characteristicUuid, int properties)
            {
                _owner.HandleCharacteristicDiscovered(serviceUuid, characteristicUuid, properties);
            }

            public void onDiscoveryComplete(long operationId, bool success, int errorCode, string detail)
            {
                _owner.HandleDiscoveryComplete(operationId, success, errorCode, detail);
            }

            public void onNotification(string serviceUuid, string characteristicUuid, sbyte[] data)
            {
                _owner.HandleNotification(serviceUuid, characteristicUuid, data);
            }

            public void onOperationComplete(long operationId, bool success, int errorCode, string detail)
            {
                _owner.HandleOperationComplete(operationId, success, errorCode, detail);
            }

            public void onReadComplete(long operationId, bool success, int errorCode, string detail, sbyte[] data)
            {
                _owner.HandleReadComplete(operationId, success, errorCode, detail, data);
            }
        }
    }
}
#endif
