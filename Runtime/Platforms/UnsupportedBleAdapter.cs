using System;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;

namespace Spaxtek.Ble.Platforms
{
    internal sealed class UnsupportedBleAdapter : IBleAdapter
    {
        private bool _disposed;

        public BlePlatform Platform => BlePlatform.Unknown;
        public BleAdapterState State => _disposed ? BleAdapterState.Disposed : BleAdapterState.Unavailable;

        public event Action<BleAdapterState> OnStateChanged;
        public event Action<BleAdvertisement> OnAdvertisementReceived
        {
            add { }
            remove { }
        }
        public event Action<BleError> OnError;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return CreateFailure("BLE is not supported on the current Unity platform.");
        }

        public Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken)
        {
            return CreateFailure("BLE scanning is not supported on the current Unity platform.");
        }

        public Task StopScanAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IBleConnection> ConnectAsync(BleDeviceId deviceId, CancellationToken cancellationToken)
        {
            BleException exception = CreateException("BLE connections are not supported on the current Unity platform.");
            return Task.FromException<IBleConnection>(exception);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            OnStateChanged?.Invoke(BleAdapterState.Disposed);
            OnStateChanged = null;
            OnError = null;
        }

        private Task CreateFailure(string detail)
        {
            return Task.FromException(CreateException(detail));
        }

        private BleException CreateException(string detail)
        {
            BleException exception = new BleException(BleErrorCode.Unsupported, detail);
            OnError?.Invoke(new BleError(exception.ErrorCode, exception.Message));
            return exception;
        }
    }
}
