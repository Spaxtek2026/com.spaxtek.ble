using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;
using Spaxtek.Ble.Internal;
using UnityEngine;

namespace Spaxtek.Ble.Unity
{
    [DisallowMultipleComponent]
    public sealed class BleManager : MonoBehaviour
    {
        [Header("Lifecycle")]
        [SerializeField] private bool _initializeOnAwake = true;
        [SerializeField] private bool _dontDestroyOnLoad = true;
        [SerializeField, Min(1)] private int _maximumCallbacksPerFrame = 128;

        private readonly BleCallbackDispatcher _callbackDispatcher = new BleCallbackDispatcher();
        private CancellationTokenSource _lifetimeCancellation;
        private IBleAdapter _adapter;
        private IBleConnection _connection;
        private bool _destroyed;

        public IBleAdapter Adapter => _adapter;
        public IBleConnection Connection => _connection;
        public bool HasConnection => _connection != null && _connection.State == BleConnectionState.Connected;

        public event Action<BleAdapterState> OnAdapterStateChanged;
        public event Action<BleAdvertisement> OnAdvertisementReceived;
        public event Action<BleConnectionState> OnConnectionStateChanged;
        public event Action<BleNotification> OnNotificationReceived;
        public event Action<BleError> OnError;

        private void Awake()
        {
            if (_dontDestroyOnLoad)
                DontDestroyOnLoad(gameObject);

            _lifetimeCancellation = new CancellationTokenSource();
            AttachAdapter(BleAdapterFactory.Create());

            if (_initializeOnAwake)
                InitializeOnAwakeAsync();
        }

        private void Update()
        {
            if (_adapter is IBleEventPump adapterPump)
                adapterPump.PumpEvents(_maximumCallbacksPerFrame);
            if (_connection is IBleEventPump connectionPump && !ReferenceEquals(connectionPump, _adapter))
                connectionPump.PumpEvents(_maximumCallbacksPerFrame);

            _callbackDispatcher.Drain(_maximumCallbacksPerFrame);
        }

        private void OnDestroy()
        {
            if (_destroyed)
                return;

            _destroyed = true;
            _lifetimeCancellation?.Cancel();
            DetachConnection();
            DetachAdapter();
            _callbackDispatcher.Clear();
            _lifetimeCancellation?.Dispose();
            _lifetimeCancellation = null;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            RequireAlive();
            return _adapter.InitializeAsync(cancellationToken);
        }

        public void UseAdapter(IBleAdapter adapter)
        {
            RequireAlive();
            if (adapter == null)
                throw new ArgumentNullException(nameof(adapter));
            if (ReferenceEquals(adapter, _adapter))
                return;

            DetachConnection();
            AttachAdapter(adapter);
        }

        public Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken = default)
        {
            RequireAlive();
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            return _adapter.StartScanAsync(filter, cancellationToken);
        }

        public Task StopScanAsync(CancellationToken cancellationToken = default)
        {
            RequireAlive();
            return _adapter.StopScanAsync(cancellationToken);
        }

        public async Task<IBleConnection> ConnectAsync(BleDeviceId deviceId, CancellationToken cancellationToken = default)
        {
            RequireAlive();
            if (_connection != null)
                await DisconnectAsync(cancellationToken);

            IBleConnection connection = await _adapter.ConnectAsync(deviceId, cancellationToken);
            AttachConnection(connection);
            return connection;
        }

        public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(BleCacheMode cacheMode = BleCacheMode.Uncached, CancellationToken cancellationToken = default)
        {
            RequireConnection();
            return _connection.DiscoverServicesAsync(cacheMode, cancellationToken);
        }

        public Task SubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken = default)
        {
            RequireConnection();
            return _connection.SubscribeAsync(characteristicId, cancellationToken);
        }

        public Task<ReadOnlyMemory<byte>> ReadAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken = default)
        {
            RequireConnection();
            return _connection.ReadAsync(characteristicId, cancellationToken);
        }

        public Task UnsubscribeAsync(BleCharacteristicId characteristicId, CancellationToken cancellationToken = default)
        {
            RequireConnection();
            return _connection.UnsubscribeAsync(characteristicId, cancellationToken);
        }

        public Task<BleWriteResult> WriteAsync(BleCharacteristicId characteristicId, ReadOnlyMemory<byte> data, BleWriteMode writeMode,
            CancellationToken cancellationToken = default)
        {
            RequireConnection();
            return _connection.WriteAsync(characteristicId, data, writeMode, cancellationToken);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            RequireAlive();
            if (_connection == null)
                return;

            IBleConnection connection = _connection;
            await connection.DisconnectAsync(cancellationToken);
            if (ReferenceEquals(connection, _connection))
                DetachConnection();
        }

        private async void InitializeOnAwakeAsync()
        {
            try
            {
                await InitializeAsync(_lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_destroyed)
            {
            }
            catch (Exception exception)
            {
                BleError error = ToBleError(exception);
                _callbackDispatcher.Enqueue(() => OnError?.Invoke(error));
            }
        }

        private void AttachAdapter(IBleAdapter adapter)
        {
            if (adapter == null)
                throw new ArgumentNullException(nameof(adapter));

            DetachAdapter();
            _adapter = adapter;
            _adapter.OnStateChanged += HandleAdapterStateChanged;
            _adapter.OnAdvertisementReceived += HandleAdvertisementReceived;
            _adapter.OnError += HandleAdapterError;
        }

        private void AttachConnection(IBleConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            DetachConnection();
            _connection = connection;
            _connection.OnStateChanged += HandleConnectionStateChanged;
            _connection.OnNotificationReceived += HandleNotificationReceived;
            _connection.OnError += HandleConnectionError;
            _callbackDispatcher.Enqueue(() => OnConnectionStateChanged?.Invoke(connection.State));
        }

        private void DetachConnection()
        {
            if (_connection == null)
                return;

            _connection.OnStateChanged -= HandleConnectionStateChanged;
            _connection.OnNotificationReceived -= HandleNotificationReceived;
            _connection.OnError -= HandleConnectionError;
            _connection.Dispose();
            _connection = null;
        }

        private void DetachAdapter()
        {
            if (_adapter == null)
                return;

            _adapter.OnStateChanged -= HandleAdapterStateChanged;
            _adapter.OnAdvertisementReceived -= HandleAdvertisementReceived;
            _adapter.OnError -= HandleAdapterError;
            _adapter.Dispose();
            _adapter = null;
        }

        private void HandleAdapterStateChanged(BleAdapterState state)
        {
            _callbackDispatcher.Enqueue(() => OnAdapterStateChanged?.Invoke(state));
        }

        private void HandleAdvertisementReceived(BleAdvertisement advertisement)
        {
            _callbackDispatcher.Enqueue(() => OnAdvertisementReceived?.Invoke(advertisement));
        }

        private void HandleAdapterError(BleError error)
        {
            _callbackDispatcher.Enqueue(() => OnError?.Invoke(error));
        }

        private void HandleConnectionStateChanged(BleConnectionState state)
        {
            _callbackDispatcher.Enqueue(() => OnConnectionStateChanged?.Invoke(state));
        }

        private void HandleNotificationReceived(BleNotification notification)
        {
            _callbackDispatcher.Enqueue(() => OnNotificationReceived?.Invoke(notification));
        }

        private void HandleConnectionError(BleError error)
        {
            _callbackDispatcher.Enqueue(() => OnError?.Invoke(error));
        }

        private void RequireAlive()
        {
            if (_destroyed || _adapter == null)
                throw new ObjectDisposedException(nameof(BleManager));
        }

        private void RequireConnection()
        {
            RequireAlive();
            if (_connection == null || _connection.State != BleConnectionState.Connected)
                throw new BleException(BleErrorCode.InvalidState, "An active BLE connection is required.");
        }

        private static BleError ToBleError(Exception exception)
        {
            if (exception is BleException bleException)
                return new BleError(bleException.ErrorCode, bleException.Message, bleException.NativeCode);
            if (exception is OperationCanceledException)
                return new BleError(BleErrorCode.Cancelled, exception.Message);

            return new BleError(BleErrorCode.PlatformError, exception.Message);
        }
    }
}
