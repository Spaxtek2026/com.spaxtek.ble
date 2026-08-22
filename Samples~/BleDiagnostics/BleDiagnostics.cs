using System;
using System.Threading;
using System.Threading.Tasks;
using Spaxtek.Ble.Core;
using Spaxtek.Ble.Unity;
using UnityEngine;

namespace Spaxtek.Ble.Samples
{
    public sealed class BleDiagnostics : MonoBehaviour
    {
        [SerializeField] private BleManager _bleManager;
        [SerializeField] private string _deviceName = string.Empty;
        [SerializeField] private string _serviceUuid = string.Empty;

        private CancellationTokenSource _lifetimeCancellation;

        private void Awake()
        {
            _lifetimeCancellation = new CancellationTokenSource();
            _bleManager.OnAdapterStateChanged += HandleAdapterStateChanged;
            _bleManager.OnAdvertisementReceived += HandleAdvertisementReceived;
            _bleManager.OnConnectionStateChanged += HandleConnectionStateChanged;
            _bleManager.OnNotificationReceived += HandleNotificationReceived;
            _bleManager.OnError += HandleError;
        }

        private void OnDestroy()
        {
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
            if (_bleManager == null)
                return;

            _bleManager.OnAdapterStateChanged -= HandleAdapterStateChanged;
            _bleManager.OnAdvertisementReceived -= HandleAdvertisementReceived;
            _bleManager.OnConnectionStateChanged -= HandleConnectionStateChanged;
            _bleManager.OnNotificationReceived -= HandleNotificationReceived;
            _bleManager.OnError -= HandleError;
        }

        [ContextMenu("Start BLE Scan")]
        public void StartScan()
        {
            StartScanAsync();
        }

        [ContextMenu("Stop BLE Scan")]
        public void StopScan()
        {
            StopScanAsync();
        }

        private async void StartScanAsync()
        {
            try
            {
                Guid? serviceUuid = Guid.TryParse(_serviceUuid, out Guid parsedUuid) ? parsedUuid : null;
                await _bleManager.InitializeAsync(_lifetimeCancellation.Token);
                await _bleManager.StartScanAsync(new BleScanFilter(_deviceName, serviceUuid), _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private async void StopScanAsync()
        {
            try
            {
                await _bleManager.StopScanAsync(_lifetimeCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void HandleAdapterStateChanged(BleAdapterState state)
        {
            Debug.Log($"[BLE] Adapter state: {state}", this);
        }

        private void HandleAdvertisementReceived(BleAdvertisement advertisement)
        {
            Debug.Log($"[BLE] Advertisement: {advertisement.Name}, {advertisement.DeviceId}, RSSI {advertisement.Rssi}", this);
        }

        private void HandleConnectionStateChanged(BleConnectionState state)
        {
            Debug.Log($"[BLE] Connection state: {state}", this);
        }

        private void HandleNotificationReceived(BleNotification notification)
        {
            Debug.Log($"[BLE] Notification: {notification.CharacteristicId}, {notification.Data.Length} byte(s)", this);
        }

        private void HandleError(BleError error)
        {
            Debug.LogError($"[BLE] {error}", this);
        }
    }
}
