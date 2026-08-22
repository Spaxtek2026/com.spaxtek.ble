package com.spaxtek.ble;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanFilter;
import android.bluetooth.le.ScanRecord;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelUuid;
import android.util.SparseArray;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;

public final class SpaxtekBleBridge {
    public interface Listener {
        void onInitialized(boolean success, int errorCode, String detail);
        void onScanStarted();
        void onAdvertisement(String deviceId, String name, int rssi, boolean isConnectable, String serviceUuids, byte[] manufacturerData);
        void onScanFailed(int errorCode, String detail);
        void onConnectionState(boolean connected, int errorCode, String detail);
        void onCharacteristicDiscovered(String serviceUuid, String characteristicUuid, int properties);
        void onDiscoveryComplete(long operationId, boolean success, int errorCode, String detail);
        void onNotification(String serviceUuid, String characteristicUuid, byte[] data);
        void onOperationComplete(long operationId, boolean success, int errorCode, String detail);
        void onReadComplete(long operationId, boolean success, int errorCode, String detail, byte[] data);
    }

    private static final int ERROR_NONE = 0;
    private static final int ERROR_UNSUPPORTED = 1;
    private static final int ERROR_PERMISSION_DENIED = 2;
    private static final int ERROR_BLUETOOTH_DISABLED = 3;
    private static final int ERROR_DEVICE_NOT_FOUND = 4;
    private static final int ERROR_DEVICE_UNREACHABLE = 6;
    private static final int ERROR_SERVICE_NOT_FOUND = 7;
    private static final int ERROR_CHARACTERISTIC_NOT_FOUND = 8;
    private static final int ERROR_SUBSCRIPTION_FAILED = 9;
    private static final int ERROR_WRITE_REJECTED = 10;
    private static final int ERROR_OPERATION_BUSY = 11;
    private static final int ERROR_INVALID_STATE = 13;
    private static final int ERROR_INVALID_ARGUMENT = 14;
    private static final int ERROR_PLATFORM = 15;
    private static final int ERROR_DISPOSED = 16;
    private static final int ERROR_TIMEOUT = 17;
    private static final int ERROR_READ_REJECTED = 18;

    private static final int UNITY_PROPERTY_READ = 1 << 0;
    private static final int UNITY_PROPERTY_WRITE = 1 << 1;
    private static final int UNITY_PROPERTY_WRITE_WITHOUT_RESPONSE = 1 << 2;
    private static final int UNITY_PROPERTY_NOTIFY = 1 << 3;
    private static final int UNITY_PROPERTY_INDICATE = 1 << 4;

    private static final UUID CLIENT_CONFIGURATION_UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final long OPERATION_TIMEOUT_MILLISECONDS = 10000L;

    private final Activity activity;
    private final Listener listener;
    private final Handler operationHandler = new Handler(Looper.getMainLooper());
    private final ArrayDeque<GattOperation> operationQueue = new ArrayDeque<>();
    private final Set<String> reportedDeviceIds = new HashSet<>();

    private BluetoothAdapter adapter;
    private BluetoothLeScanner scanner;
    private BluetoothGatt gatt;
    private BluetoothLEScanConfiguration scanConfiguration;
    private GattOperation currentOperation;
    private Runnable operationTimeout;
    private long discoveryOperationId;
    private boolean scanning;
    private boolean disposed;

    public SpaxtekBleBridge(Activity activity, Listener listener) {
        if (activity == null)
            throw new IllegalArgumentException("Activity is required.");
        if (listener == null)
            throw new IllegalArgumentException("Listener is required.");

        this.activity = activity;
        this.listener = listener;
    }

    public void initialize() {
        if (disposed) {
            listener.onInitialized(false, ERROR_DISPOSED, "The Android BLE bridge is disposed.");
            return;
        }

        try {
            PackageManager packageManager = activity.getPackageManager();
            if (!packageManager.hasSystemFeature(PackageManager.FEATURE_BLUETOOTH_LE)) {
                listener.onInitialized(false, ERROR_UNSUPPORTED, "This Android device does not support BLE.");
                return;
            }

            BluetoothManager manager = (BluetoothManager)activity.getSystemService(Context.BLUETOOTH_SERVICE);
            if (manager == null) {
                listener.onInitialized(false, ERROR_UNSUPPORTED, "BluetoothManager is unavailable.");
                return;
            }

            adapter = manager.getAdapter();
            if (adapter == null) {
                listener.onInitialized(false, ERROR_UNSUPPORTED, "Bluetooth adapter is unavailable.");
                return;
            }
            if (!hasRuntimePermissions()) {
                listener.onInitialized(false, ERROR_PERMISSION_DENIED, "Required Android Bluetooth permission is not granted.");
                return;
            }
            if (!adapter.isEnabled()) {
                listener.onInitialized(false, ERROR_BLUETOOTH_DISABLED, "Bluetooth is disabled.");
                return;
            }

            listener.onInitialized(true, ERROR_NONE, "Android BLE adapter is ready.");
        } catch (SecurityException exception) {
            listener.onInitialized(false, ERROR_PERMISSION_DENIED, safeMessage(exception));
        } catch (Exception exception) {
            listener.onInitialized(false, ERROR_PLATFORM, safeMessage(exception));
        }
    }

    @SuppressLint("MissingPermission")
    public void startScan(String deviceName, String serviceUuid, boolean allowDuplicates) {
        if (!requireAdapterForScan())
            return;

        try {
            stopScanInternal();
            scanner = adapter.getBluetoothLeScanner();
            if (scanner == null) {
                listener.onScanFailed(ERROR_BLUETOOTH_DISABLED, "BluetoothLeScanner is unavailable.");
                return;
            }

            UUID parsedServiceUuid = parseOptionalUuid(serviceUuid);
            scanConfiguration = new BluetoothLEScanConfiguration(emptyToNull(deviceName), parsedServiceUuid, allowDuplicates);
            reportedDeviceIds.clear();

            ScanFilter.Builder filterBuilder = new ScanFilter.Builder();
            if (scanConfiguration.deviceName != null)
                filterBuilder.setDeviceName(scanConfiguration.deviceName);
            if (scanConfiguration.serviceUuid != null)
                filterBuilder.setServiceUuid(new ParcelUuid(scanConfiguration.serviceUuid));

            ScanSettings settings = new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build();
            scanning = true;
            scanner.startScan(Collections.singletonList(filterBuilder.build()), settings, scanCallback);
            listener.onScanStarted();
        } catch (IllegalArgumentException exception) {
            scanning = false;
            listener.onScanFailed(ERROR_INVALID_ARGUMENT, safeMessage(exception));
        } catch (SecurityException exception) {
            scanning = false;
            listener.onScanFailed(ERROR_PERMISSION_DENIED, safeMessage(exception));
        } catch (Exception exception) {
            scanning = false;
            listener.onScanFailed(ERROR_PLATFORM, safeMessage(exception));
        }
    }

    @SuppressLint("MissingPermission")
    public void stopScan() {
        try {
            stopScanInternal();
        } catch (SecurityException exception) {
            listener.onScanFailed(ERROR_PERMISSION_DENIED, safeMessage(exception));
        }
    }

    @SuppressLint("MissingPermission")
    public void connect(String deviceId) {
        if (disposed) {
            listener.onConnectionState(false, ERROR_DISPOSED, "The Android BLE bridge is disposed.");
            return;
        }
        if (adapter == null || !adapter.isEnabled()) {
            listener.onConnectionState(false, ERROR_BLUETOOTH_DISABLED, "Bluetooth adapter is unavailable or disabled.");
            return;
        }
        if (!hasRuntimePermissions()) {
            listener.onConnectionState(false, ERROR_PERMISSION_DENIED, "Required Android Bluetooth permission is not granted.");
            return;
        }
        if (deviceId == null || deviceId.trim().isEmpty()) {
            listener.onConnectionState(false, ERROR_INVALID_ARGUMENT, "A BLE device identifier is required.");
            return;
        }

        try {
            stopScanInternal();
            closeGatt();
            BluetoothDevice device = adapter.getRemoteDevice(deviceId);
            gatt = device.connectGatt(activity, false, gattCallback, BluetoothDevice.TRANSPORT_LE);
            if (gatt == null)
                listener.onConnectionState(false, ERROR_DEVICE_UNREACHABLE, "connectGatt returned null.");
        } catch (IllegalArgumentException exception) {
            listener.onConnectionState(false, ERROR_DEVICE_NOT_FOUND, safeMessage(exception));
        } catch (SecurityException exception) {
            listener.onConnectionState(false, ERROR_PERMISSION_DENIED, safeMessage(exception));
        } catch (Exception exception) {
            listener.onConnectionState(false, ERROR_PLATFORM, safeMessage(exception));
        }
    }

    @SuppressLint("MissingPermission")
    public synchronized void discoverServices(long operationId, boolean uncached) {
        if (disposed) {
            listener.onDiscoveryComplete(operationId, false, ERROR_DISPOSED, "The Android BLE bridge is disposed.");
            return;
        }
        if (gatt == null) {
            listener.onDiscoveryComplete(operationId, false, ERROR_INVALID_STATE, "An active GATT connection is required.");
            return;
        }
        if (!hasRuntimePermissions()) {
            listener.onDiscoveryComplete(operationId, false, ERROR_PERMISSION_DENIED, "Required Android Bluetooth permission is not granted.");
            return;
        }
        if (currentOperation != null || !operationQueue.isEmpty() || discoveryOperationId != 0L) {
            listener.onDiscoveryComplete(operationId, false, ERROR_OPERATION_BUSY, "Another GATT operation is active.");
            return;
        }

        discoveryOperationId = operationId;
        try {
            if (!gatt.discoverServices()) {
                discoveryOperationId = 0L;
                listener.onDiscoveryComplete(operationId, false, ERROR_PLATFORM, "discoverServices returned false.");
            }
        } catch (SecurityException exception) {
            discoveryOperationId = 0L;
            listener.onDiscoveryComplete(operationId, false, ERROR_PERMISSION_DENIED, safeMessage(exception));
        } catch (Exception exception) {
            discoveryOperationId = 0L;
            listener.onDiscoveryComplete(operationId, false, ERROR_PLATFORM, safeMessage(exception));
        }
    }

    public synchronized void setSubscription(long operationId, String serviceUuid, String characteristicUuid, boolean enabled) {
        if (!requireConnectedOperation(operationId))
            return;

        BluetoothGattCharacteristic characteristic = findCharacteristic(operationId, serviceUuid, characteristicUuid);
        if (characteristic == null)
            return;

        int properties = characteristic.getProperties();
        if (enabled && (properties & (BluetoothGattCharacteristic.PROPERTY_NOTIFY | BluetoothGattCharacteristic.PROPERTY_INDICATE)) == 0) {
            listener.onOperationComplete(operationId, false, ERROR_SUBSCRIPTION_FAILED, "The characteristic does not support notifications or indications.");
            return;
        }

        enqueueOperation(new SubscriptionOperation(operationId, characteristic, enabled));
    }

    public synchronized void read(long operationId, String serviceUuid, String characteristicUuid) {
        if (!requireConnectedOperation(operationId))
            return;

        BluetoothGattCharacteristic characteristic = findCharacteristic(operationId, serviceUuid, characteristicUuid);
        if (characteristic == null)
            return;
        if ((characteristic.getProperties() & BluetoothGattCharacteristic.PROPERTY_READ) == 0) {
            listener.onOperationComplete(operationId, false, ERROR_READ_REJECTED, "The characteristic is not readable.");
            return;
        }

        enqueueOperation(new ReadOperation(operationId, characteristic));
    }

    public synchronized void write(long operationId, String serviceUuid, String characteristicUuid, byte[] data, int writeMode) {
        if (!requireConnectedOperation(operationId))
            return;
        if (data == null || data.length == 0) {
            listener.onOperationComplete(operationId, false, ERROR_INVALID_ARGUMENT, "A BLE write payload cannot be empty.");
            return;
        }
        if (writeMode != 0 && writeMode != 1) {
            listener.onOperationComplete(operationId, false, ERROR_INVALID_ARGUMENT, "Unknown BLE write mode.");
            return;
        }

        BluetoothGattCharacteristic characteristic = findCharacteristic(operationId, serviceUuid, characteristicUuid);
        if (characteristic == null)
            return;

        int requiredProperty = writeMode == 0 ? BluetoothGattCharacteristic.PROPERTY_WRITE : BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE;
        if ((characteristic.getProperties() & requiredProperty) == 0) {
            listener.onOperationComplete(operationId, false, ERROR_WRITE_REJECTED, "The characteristic does not support the requested write mode.");
            return;
        }

        enqueueOperation(new WriteOperation(operationId, characteristic, data.clone(), writeMode));
    }

    @SuppressLint("MissingPermission")
    public void disconnect() {
        try {
            stopScanInternal();
            if (gatt == null) {
                listener.onConnectionState(false, ERROR_NONE, "No active GATT connection.");
                return;
            }

            gatt.disconnect();
        } catch (SecurityException exception) {
            closeGatt();
            listener.onConnectionState(false, ERROR_PERMISSION_DENIED, safeMessage(exception));
        } catch (Exception exception) {
            closeGatt();
            listener.onConnectionState(false, ERROR_PLATFORM, safeMessage(exception));
        }
    }

    public void dispose() {
        if (disposed)
            return;

        disposed = true;
        try {
            stopScanInternal();
        } catch (Exception ignored) {
        }
        closeGatt();
        operationHandler.removeCallbacksAndMessages(null);
        adapter = null;
        scanner = null;
        scanConfiguration = null;
        reportedDeviceIds.clear();
    }

    private final ScanCallback scanCallback = new ScanCallback() {
        @Override
        @SuppressLint("MissingPermission")
        public void onScanResult(int callbackType, ScanResult result) {
            handleScanResult(result);
        }

        @Override
        @SuppressLint("MissingPermission")
        public void onBatchScanResults(List<ScanResult> results) {
            if (results == null)
                return;

            for (ScanResult result : results)
                handleScanResult(result);
        }

        @Override
        public void onScanFailed(int errorCode) {
            scanning = false;
            listener.onScanFailed(ERROR_PLATFORM, "Android scan failed with native code " + errorCode + ".");
        }
    };

    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override
        @SuppressLint("MissingPermission")
        public void onConnectionStateChange(BluetoothGatt callbackGatt, int status, int newState) {
            if (!isCurrentGatt(callbackGatt)) {
                if (callbackGatt != null)
                    callbackGatt.close();
                return;
            }

            if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
                listener.onConnectionState(true, ERROR_NONE, "GATT connected.");
                return;
            }
            if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                int errorCode = status == BluetoothGatt.GATT_SUCCESS ? ERROR_NONE : ERROR_DEVICE_UNREACHABLE;
                String detail = status == BluetoothGatt.GATT_SUCCESS ? "GATT disconnected." : "GATT disconnected with native status " + status + ".";
                closeGatt(callbackGatt);
                listener.onConnectionState(false, errorCode, detail);
                return;
            }
            if (status != BluetoothGatt.GATT_SUCCESS) {
                closeGatt(callbackGatt);
                listener.onConnectionState(false, ERROR_DEVICE_UNREACHABLE, "GATT connection failed with native status " + status + ".");
            }
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt callbackGatt, int status) {
            if (!isCurrentGatt(callbackGatt))
                return;

            long operationId;
            synchronized (SpaxtekBleBridge.this) {
                operationId = discoveryOperationId;
                discoveryOperationId = 0L;
            }
            if (operationId == 0L)
                return;
            if (status != BluetoothGatt.GATT_SUCCESS) {
                listener.onDiscoveryComplete(operationId, false, ERROR_PLATFORM, "Service discovery failed with native status " + status + ".");
                return;
            }

            try {
                List<BluetoothGattService> services = callbackGatt.getServices();
                for (BluetoothGattService service : services) {
                    for (BluetoothGattCharacteristic characteristic : service.getCharacteristics()) {
                        listener.onCharacteristicDiscovered(service.getUuid().toString(), characteristic.getUuid().toString(), toUnityProperties(characteristic.getProperties()));
                    }
                }
                listener.onDiscoveryComplete(operationId, true, ERROR_NONE, "GATT discovery completed.");
            } catch (Exception exception) {
                listener.onDiscoveryComplete(operationId, false, ERROR_PLATFORM, safeMessage(exception));
            }
        }

        @Override
        public void onDescriptorWrite(BluetoothGatt callbackGatt, BluetoothGattDescriptor descriptor, int status) {
            synchronized (SpaxtekBleBridge.this) {
                if (callbackGatt != gatt)
                    return;
                if (!(currentOperation instanceof SubscriptionOperation))
                    return;

                SubscriptionOperation operation = (SubscriptionOperation)currentOperation;
                if (!operation.matches(descriptor))
                    return;

                boolean success = status == BluetoothGatt.GATT_SUCCESS;
                int errorCode = success ? ERROR_NONE : ERROR_SUBSCRIPTION_FAILED;
                String detail = success ? "Subscription updated." : "Descriptor write failed with native status " + status + ".";
                completeCurrentOperation(success, errorCode, detail);
            }
        }

        @Override
        public void onCharacteristicWrite(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic, int status) {
            synchronized (SpaxtekBleBridge.this) {
                if (callbackGatt != gatt)
                    return;
                if (!(currentOperation instanceof WriteOperation))
                    return;

                WriteOperation operation = (WriteOperation)currentOperation;
                if (!operation.matches(characteristic))
                    return;

                boolean success = status == BluetoothGatt.GATT_SUCCESS;
                int errorCode = success ? ERROR_NONE : ERROR_WRITE_REJECTED;
                String detail = success ? "Characteristic write completed." : "Characteristic write failed with native status " + status + ".";
                completeCurrentOperation(success, errorCode, detail);
            }
        }

        @Override
        public void onCharacteristicRead(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic, byte[] value, int status) {
            handleCharacteristicRead(callbackGatt, characteristic, value, status);
        }

        @Override
        @SuppressWarnings("deprecation")
        public void onCharacteristicRead(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic, int status) {
            handleCharacteristicRead(callbackGatt, characteristic, characteristic.getValue(), status);
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic, byte[] value) {
            if (!isCurrentGatt(callbackGatt))
                return;
            notifyUnity(characteristic, value);
        }

        @Override
        @SuppressWarnings("deprecation")
        public void onCharacteristicChanged(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic) {
            if (!isCurrentGatt(callbackGatt))
                return;
            notifyUnity(characteristic, characteristic.getValue());
        }
    };

    @SuppressLint("MissingPermission")
    private void handleScanResult(ScanResult result) {
        if (!scanning || result == null || result.getDevice() == null || scanConfiguration == null)
            return;

        BluetoothDevice device = result.getDevice();
        String deviceId = device.getAddress();
        if (!scanConfiguration.allowDuplicates && reportedDeviceIds.contains(deviceId))
            return;

        ScanRecord record = result.getScanRecord();
        String name = device.getName();
        if (name == null && record != null)
            name = record.getDeviceName();
        if (scanConfiguration.deviceName != null && !scanConfiguration.deviceName.equals(name))
            return;

        String serviceUuids = getServiceUuids(record);
        if (scanConfiguration.serviceUuid != null && !containsServiceUuid(record, scanConfiguration.serviceUuid))
            return;

        reportedDeviceIds.add(deviceId);
        byte[] manufacturerData = getManufacturerData(record);
        listener.onAdvertisement(deviceId, name == null ? "" : name, result.getRssi(), result.isConnectable(), serviceUuids, manufacturerData);
    }

    private synchronized void enqueueOperation(GattOperation operation) {
        operationQueue.add(operation);
        startNextOperation();
    }

    private synchronized void startNextOperation() {
        if (currentOperation != null || operationQueue.isEmpty())
            return;

        currentOperation = operationQueue.removeFirst();
        boolean started;
        try {
            started = currentOperation.execute();
        } catch (SecurityException exception) {
            completeCurrentOperation(false, ERROR_PERMISSION_DENIED, safeMessage(exception));
            return;
        } catch (Exception exception) {
            completeCurrentOperation(false, ERROR_PLATFORM, safeMessage(exception));
            return;
        }

        if (!started) {
            completeCurrentOperation(false, currentOperation.failureErrorCode(), "Android rejected the GATT operation.");
            return;
        }

        long operationId = currentOperation.operationId;
        operationTimeout = () -> handleOperationTimeout(operationId);
        operationHandler.postDelayed(operationTimeout, OPERATION_TIMEOUT_MILLISECONDS);
    }

    private synchronized void handleOperationTimeout(long operationId) {
        if (currentOperation == null || currentOperation.operationId != operationId)
            return;

        completeCurrentOperation(false, ERROR_TIMEOUT, "The Android GATT operation timed out.");
    }

    private synchronized void completeCurrentOperation(boolean success, int errorCode, String detail) {
        if (currentOperation == null)
            return;

        if (operationTimeout != null)
            operationHandler.removeCallbacks(operationTimeout);
        operationTimeout = null;
        long operationId = currentOperation.operationId;
        currentOperation = null;
        listener.onOperationComplete(operationId, success, errorCode, detail);
        startNextOperation();
    }

    private synchronized void completeCurrentReadOperation(boolean success, int errorCode, String detail, byte[] data) {
        if (!(currentOperation instanceof ReadOperation))
            return;

        if (operationTimeout != null)
            operationHandler.removeCallbacks(operationTimeout);
        operationTimeout = null;
        long operationId = currentOperation.operationId;
        currentOperation = null;
        listener.onReadComplete(operationId, success, errorCode, detail, data == null ? new byte[0] : data.clone());
        startNextOperation();
    }

    private synchronized void failAllOperations(int errorCode, String detail) {
        if (operationTimeout != null)
            operationHandler.removeCallbacks(operationTimeout);
        operationTimeout = null;

        if (currentOperation != null) {
            listener.onOperationComplete(currentOperation.operationId, false, errorCode, detail);
            currentOperation = null;
        }
        while (!operationQueue.isEmpty()) {
            GattOperation operation = operationQueue.removeFirst();
            listener.onOperationComplete(operation.operationId, false, errorCode, detail);
        }
    }

    private abstract class GattOperation {
        private final long operationId;

        private GattOperation(long operationId) {
            this.operationId = operationId;
        }

        abstract boolean execute();
        abstract int failureErrorCode();
    }

    private final class SubscriptionOperation extends GattOperation {
        private final BluetoothGattCharacteristic characteristic;
        private final boolean enabled;

        private SubscriptionOperation(long operationId, BluetoothGattCharacteristic characteristic, boolean enabled) {
            super(operationId);
            this.characteristic = characteristic;
            this.enabled = enabled;
        }

        @Override
        @SuppressLint("MissingPermission")
        @SuppressWarnings("deprecation")
        boolean execute() {
            if (!gatt.setCharacteristicNotification(characteristic, enabled))
                return false;

            BluetoothGattDescriptor descriptor = characteristic.getDescriptor(CLIENT_CONFIGURATION_UUID);
            if (descriptor == null)
                return false;

            byte[] value = BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE;
            if (enabled) {
                boolean supportsNotify = (characteristic.getProperties() & BluetoothGattCharacteristic.PROPERTY_NOTIFY) != 0;
                value = supportsNotify ? BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE : BluetoothGattDescriptor.ENABLE_INDICATION_VALUE;
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
                return gatt.writeDescriptor(descriptor, value) == BluetoothGatt.GATT_SUCCESS;

            descriptor.setValue(value);
            return gatt.writeDescriptor(descriptor);
        }

        @Override
        int failureErrorCode() {
            return ERROR_SUBSCRIPTION_FAILED;
        }

        boolean matches(BluetoothGattDescriptor descriptor) {
            if (descriptor == null || descriptor.getCharacteristic() == null)
                return false;

            BluetoothGattCharacteristic descriptorCharacteristic = descriptor.getCharacteristic();
            return descriptorCharacteristic.getUuid().equals(characteristic.getUuid()) &&
                descriptorCharacteristic.getService().getUuid().equals(characteristic.getService().getUuid());
        }
    }

    private final class WriteOperation extends GattOperation {
        private final BluetoothGattCharacteristic characteristic;
        private final byte[] data;
        private final int writeMode;

        private WriteOperation(long operationId, BluetoothGattCharacteristic characteristic, byte[] data, int writeMode) {
            super(operationId);
            this.characteristic = characteristic;
            this.data = data;
            this.writeMode = writeMode;
        }

        @Override
        @SuppressLint("MissingPermission")
        @SuppressWarnings("deprecation")
        boolean execute() {
            int nativeWriteType = writeMode == 0 ? BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT : BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
                return gatt.writeCharacteristic(characteristic, data, nativeWriteType) == BluetoothGatt.GATT_SUCCESS;

            characteristic.setWriteType(nativeWriteType);
            characteristic.setValue(data);
            return gatt.writeCharacteristic(characteristic);
        }

        @Override
        int failureErrorCode() {
            return ERROR_WRITE_REJECTED;
        }

        boolean matches(BluetoothGattCharacteristic callbackCharacteristic) {
            return callbackCharacteristic != null && callbackCharacteristic.getUuid().equals(characteristic.getUuid()) &&
                callbackCharacteristic.getService().getUuid().equals(characteristic.getService().getUuid());
        }
    }

    private final class ReadOperation extends GattOperation {
        private final BluetoothGattCharacteristic characteristic;

        private ReadOperation(long operationId, BluetoothGattCharacteristic characteristic) {
            super(operationId);
            this.characteristic = characteristic;
        }

        @Override
        @SuppressLint("MissingPermission")
        boolean execute() {
            return gatt.readCharacteristic(characteristic);
        }

        @Override
        int failureErrorCode() {
            return ERROR_READ_REJECTED;
        }

        boolean matches(BluetoothGattCharacteristic callbackCharacteristic) {
            return callbackCharacteristic != null && callbackCharacteristic.getUuid().equals(characteristic.getUuid()) &&
                callbackCharacteristic.getService().getUuid().equals(characteristic.getService().getUuid());
        }
    }

    private boolean requireAdapterForScan() {
        if (disposed) {
            listener.onScanFailed(ERROR_DISPOSED, "The Android BLE bridge is disposed.");
            return false;
        }
        if (adapter == null) {
            listener.onScanFailed(ERROR_INVALID_STATE, "BLE is not initialized.");
            return false;
        }
        if (!hasRuntimePermissions()) {
            listener.onScanFailed(ERROR_PERMISSION_DENIED, "Required Android Bluetooth permission is not granted.");
            return false;
        }
        if (!adapter.isEnabled()) {
            listener.onScanFailed(ERROR_BLUETOOTH_DISABLED, "Bluetooth is disabled.");
            return false;
        }

        return true;
    }

    private synchronized boolean requireConnectedOperation(long operationId) {
        if (disposed) {
            listener.onOperationComplete(operationId, false, ERROR_DISPOSED, "The Android BLE bridge is disposed.");
            return false;
        }
        if (gatt == null) {
            listener.onOperationComplete(operationId, false, ERROR_INVALID_STATE, "An active GATT connection is required.");
            return false;
        }
        if (!hasRuntimePermissions()) {
            listener.onOperationComplete(operationId, false, ERROR_PERMISSION_DENIED, "Required Android Bluetooth permission is not granted.");
            return false;
        }

        return true;
    }

    private BluetoothGattCharacteristic findCharacteristic(long operationId, String serviceUuid, String characteristicUuid) {
        UUID parsedServiceUuid;
        UUID parsedCharacteristicUuid;
        try {
            parsedServiceUuid = UUID.fromString(serviceUuid);
            parsedCharacteristicUuid = UUID.fromString(characteristicUuid);
        } catch (Exception exception) {
            listener.onOperationComplete(operationId, false, ERROR_INVALID_ARGUMENT, "Invalid service or characteristic UUID.");
            return null;
        }

        BluetoothGattService service = gatt.getService(parsedServiceUuid);
        if (service == null) {
            listener.onOperationComplete(operationId, false, ERROR_SERVICE_NOT_FOUND, "GATT service was not found: " + serviceUuid + ".");
            return null;
        }

        BluetoothGattCharacteristic characteristic = service.getCharacteristic(parsedCharacteristicUuid);
        if (characteristic == null)
            listener.onOperationComplete(operationId, false, ERROR_CHARACTERISTIC_NOT_FOUND, "GATT characteristic was not found: " + characteristicUuid + ".");
        return characteristic;
    }

    private void notifyUnity(BluetoothGattCharacteristic characteristic, byte[] value) {
        if (characteristic == null || characteristic.getService() == null)
            return;

        listener.onNotification(characteristic.getService().getUuid().toString(), characteristic.getUuid().toString(), value == null ? new byte[0] : value.clone());
    }

    private void handleCharacteristicRead(BluetoothGatt callbackGatt, BluetoothGattCharacteristic characteristic, byte[] value, int status) {
        synchronized (this) {
            if (callbackGatt != gatt || !(currentOperation instanceof ReadOperation))
                return;

            ReadOperation operation = (ReadOperation)currentOperation;
            if (!operation.matches(characteristic))
                return;

            boolean success = status == BluetoothGatt.GATT_SUCCESS;
            int errorCode = success ? ERROR_NONE : ERROR_READ_REJECTED;
            String detail = success ? "Characteristic read completed." : "Characteristic read failed with native status " + status + ".";
            completeCurrentReadOperation(success, errorCode, detail, value);
        }
    }

    @SuppressLint("MissingPermission")
    private void stopScanInternal() {
        if (scanning && scanner != null)
            scanner.stopScan(scanCallback);
        scanning = false;
    }

    private synchronized void closeGatt() {
        closeGatt(gatt);
    }

    private synchronized boolean isCurrentGatt(BluetoothGatt callbackGatt) {
        return callbackGatt != null && callbackGatt == gatt;
    }

    @SuppressLint("MissingPermission")
    private synchronized void closeGatt(BluetoothGatt callbackGatt) {
        if (callbackGatt == null)
            return;

        if (gatt != null && callbackGatt != gatt) {
            callbackGatt.close();
            return;
        }

        if (callbackGatt == gatt)
            gatt = null;
        discoveryOperationId = 0L;
        failAllOperations(ERROR_DEVICE_UNREACHABLE, "The GATT connection was closed.");
        callbackGatt.close();
    }

    private boolean hasRuntimePermissions() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            boolean canScan = activity.checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED;
            boolean canConnect = activity.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED;
            return canScan && canConnect;
        }

        return Build.VERSION.SDK_INT < Build.VERSION_CODES.M || activity.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED;
    }

    private static UUID parseOptionalUuid(String value) {
        return value == null || value.trim().isEmpty() ? null : UUID.fromString(value);
    }

    private static String emptyToNull(String value) {
        return value == null || value.trim().isEmpty() ? null : value.trim();
    }

    private static boolean containsServiceUuid(ScanRecord record, UUID serviceUuid) {
        if (record == null || record.getServiceUuids() == null)
            return false;

        for (ParcelUuid parcelUuid : record.getServiceUuids()) {
            if (serviceUuid.equals(parcelUuid.getUuid()))
                return true;
        }
        return false;
    }

    private static String getServiceUuids(ScanRecord record) {
        if (record == null || record.getServiceUuids() == null || record.getServiceUuids().isEmpty())
            return "";

        StringBuilder builder = new StringBuilder();
        for (ParcelUuid parcelUuid : record.getServiceUuids()) {
            if (builder.length() > 0)
                builder.append(',');
            builder.append(parcelUuid.getUuid());
        }
        return builder.toString();
    }

    private static byte[] getManufacturerData(ScanRecord record) {
        if (record == null)
            return new byte[0];

        SparseArray<byte[]> values = record.getManufacturerSpecificData();
        if (values == null || values.size() == 0)
            return new byte[0];

        byte[] value = values.valueAt(0);
        return value == null ? new byte[0] : value.clone();
    }

    private static int toUnityProperties(int nativeProperties) {
        int properties = 0;
        if ((nativeProperties & BluetoothGattCharacteristic.PROPERTY_READ) != 0)
            properties |= UNITY_PROPERTY_READ;
        if ((nativeProperties & BluetoothGattCharacteristic.PROPERTY_WRITE) != 0)
            properties |= UNITY_PROPERTY_WRITE;
        if ((nativeProperties & BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE) != 0)
            properties |= UNITY_PROPERTY_WRITE_WITHOUT_RESPONSE;
        if ((nativeProperties & BluetoothGattCharacteristic.PROPERTY_NOTIFY) != 0)
            properties |= UNITY_PROPERTY_NOTIFY;
        if ((nativeProperties & BluetoothGattCharacteristic.PROPERTY_INDICATE) != 0)
            properties |= UNITY_PROPERTY_INDICATE;
        return properties;
    }

    private static String safeMessage(Exception exception) {
        String message = exception.getMessage();
        return message == null || message.trim().isEmpty() ? exception.getClass().getSimpleName() : message;
    }

    private static final class BluetoothLEScanConfiguration {
        private final String deviceName;
        private final UUID serviceUuid;
        private final boolean allowDuplicates;

        private BluetoothLEScanConfiguration(String deviceName, UUID serviceUuid, boolean allowDuplicates) {
            this.deviceName = deviceName;
            this.serviceUuid = serviceUuid;
            this.allowDuplicates = allowDuplicates;
        }
    }
}
