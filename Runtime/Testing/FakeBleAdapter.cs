using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;

namespace Spaxtek.Ble.Testing
{
    public sealed class FakeBleAdapter : IBleAdapter
    {
        private readonly Dictionary<BleDeviceId, FakePeripheralDefinition> _peripherals = new Dictionary<BleDeviceId, FakePeripheralDefinition>();
        private BleAdapterState _state = BleAdapterState.Uninitialized;
        private bool _disposed;

        public BlePlatform Platform => BlePlatform.Simulated;
        public BleAdapterState State => _state;

        public event Action<BleAdapterState> OnStateChanged;
        public event Action<BleAdvertisement> OnAdvertisementReceived;
        public event Action<BleError> OnError;

        public void RegisterPeripheral(BleAdvertisement advertisement, IReadOnlyList<BleGattService> services)
        {
            RequireNotDisposed();
            if (advertisement == null)
                throw new ArgumentNullException(nameof(advertisement));
            if (advertisement.DeviceId.Platform != BlePlatform.Simulated)
                throw new ArgumentException("Fake peripherals require a simulated device identifier.", nameof(advertisement));

            _peripherals[advertisement.DeviceId] = new FakePeripheralDefinition(advertisement, services);
        }

        public void EmitError(BleError error)
        {
            RequireNotDisposed();
            if (error == null)
                throw new ArgumentNullException(nameof(error));
            OnError?.Invoke(error);
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            RequireNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            SetState(BleAdapterState.Ready);
            return Task.CompletedTask;
        }

        public Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken)
        {
            RequireReady();
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            cancellationToken.ThrowIfCancellationRequested();

            SetState(BleAdapterState.Scanning);
            foreach (KeyValuePair<BleDeviceId, FakePeripheralDefinition> pair in _peripherals)
            {
                if (MatchesFilter(pair.Value.Advertisement, filter))
                    OnAdvertisementReceived?.Invoke(pair.Value.Advertisement);
            }
            return Task.CompletedTask;
        }

        public Task StopScanAsync(CancellationToken cancellationToken)
        {
            RequireNotDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_state == BleAdapterState.Scanning)
                SetState(BleAdapterState.Ready);
            return Task.CompletedTask;
        }

        public Task<IBleConnection> ConnectAsync(BleDeviceId deviceId, CancellationToken cancellationToken)
        {
            RequireReady();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_peripherals.TryGetValue(deviceId, out FakePeripheralDefinition peripheral))
                return Task.FromException<IBleConnection>(new BleException(BleErrorCode.DeviceNotFound, $"Fake BLE peripheral was not found: {deviceId}."));

            FakeBleConnection connection = new FakeBleConnection(deviceId, peripheral.Services);
            connection.Connect();
            return Task.FromResult<IBleConnection>(connection);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _peripherals.Clear();
            SetState(BleAdapterState.Disposed);
            OnStateChanged = null;
            OnAdvertisementReceived = null;
            OnError = null;
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
                throw new BleException(BleErrorCode.InvalidState, $"The fake BLE adapter is not ready. Current state: {_state}.");
        }

        private void RequireNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FakeBleAdapter));
        }

        private static bool MatchesFilter(BleAdvertisement advertisement, BleScanFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.DeviceName) && !string.Equals(advertisement.Name, filter.DeviceName, StringComparison.Ordinal))
                return false;
            if (!filter.ServiceUuid.HasValue)
                return true;

            for (int index = 0; index < advertisement.ServiceUuids.Count; index++)
            {
                if (advertisement.ServiceUuids[index] == filter.ServiceUuid.Value)
                    return true;
            }
            return false;
        }

        private sealed class FakePeripheralDefinition
        {
            public FakePeripheralDefinition(BleAdvertisement advertisement, IReadOnlyList<BleGattService> services)
            {
                Advertisement = advertisement;
                Services = services ?? Array.Empty<BleGattService>();
            }

            public BleAdvertisement Advertisement { get; }
            public IReadOnlyList<BleGattService> Services { get; }
        }
    }
}
