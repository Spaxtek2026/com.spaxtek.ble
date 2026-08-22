#include "SpaxtekBleWindows.h"

#include <algorithm>
#include <condition_variable>
#include <deque>
#include <functional>
#include <future>
#include <iomanip>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <semaphore>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/base.h>

namespace
{
    using namespace winrt;
    using namespace Windows::Devices::Bluetooth;
    using namespace Windows::Devices::Bluetooth::Advertisement;
    using namespace Windows::Devices::Bluetooth::GenericAttributeProfile;
    using namespace Windows::Foundation::Collections;
    using namespace Windows::Storage::Streams;

    constexpr std::int32_t errorNone = 0;
    constexpr std::int32_t errorUnsupported = 1;
    constexpr std::int32_t errorPermissionDenied = 2;
    constexpr std::int32_t errorBluetoothDisabled = 3;
    constexpr std::int32_t errorDeviceNotFound = 4;
    constexpr std::int32_t errorDeviceUnreachable = 6;
    constexpr std::int32_t errorServiceNotFound = 7;
    constexpr std::int32_t errorCharacteristicNotFound = 8;
    constexpr std::int32_t errorSubscriptionFailed = 9;
    constexpr std::int32_t errorWriteRejected = 10;
    constexpr std::int32_t errorOperationBusy = 11;
    constexpr std::int32_t errorInvalidState = 13;
    constexpr std::int32_t errorInvalidArgument = 14;
    constexpr std::int32_t errorPlatform = 15;
    constexpr std::int32_t errorDisposed = 16;
    constexpr std::int32_t errorReadRejected = 18;

    constexpr std::int32_t nativeEventInitialized = 1;
    constexpr std::int32_t nativeEventScanStarted = 2;
    constexpr std::int32_t nativeEventAdvertisement = 3;
    constexpr std::int32_t nativeEventScanStopped = 4;
    constexpr std::int32_t nativeEventConnectionState = 5;
    constexpr std::int32_t nativeEventCharacteristicDiscovered = 6;
    constexpr std::int32_t nativeEventDiscoveryComplete = 7;
    constexpr std::int32_t nativeEventNotification = 8;
    constexpr std::int32_t nativeEventOperationComplete = 9;
    constexpr std::int32_t nativeEventError = 10;

    constexpr std::int32_t unityPropertyRead = 1 << 0;
    constexpr std::int32_t unityPropertyWrite = 1 << 1;
    constexpr std::int32_t unityPropertyWriteWithoutResponse = 1 << 2;
    constexpr std::int32_t unityPropertyNotify = 1 << 3;
    constexpr std::int32_t unityPropertyIndicate = 1 << 4;

    struct NativeEvent
    {
        SpaxBleEventHeader header{};
        std::wstring textA;
        std::wstring textB;
        std::wstring textC;
        std::vector<std::uint8_t> data;
    };

    struct CharacteristicEntry
    {
        GattCharacteristic characteristic{ nullptr };
        event_token valueChangedToken{};
        bool subscribed = false;
    };

    class OperationGate
    {
    public:
        void lock()
        {
            m_semaphore.acquire();
        }

        void unlock()
        {
            m_semaphore.release();
        }

    private:
        std::binary_semaphore m_semaphore{1};
    };

    class BleContext final : public std::enable_shared_from_this<BleContext>
    {
    public:
        BleContext() = default;

        ~BleContext()
        {
            Stop();
        }

        void Start()
        {
            m_worker = std::thread([this]() { WorkerMain(); });
        }

        void Stop()
        {
            {
                std::lock_guard<std::mutex> lifecycleLock(m_lifecycleMutex);
                if (m_stopped)
                    return;
                m_stopped = true;
            }

            std::promise<void> cleanupPromise;
            std::future<void> cleanupFuture = cleanupPromise.get_future();
            if (Post([this, &cleanupPromise]() {
                Cleanup();
                cleanupPromise.set_value();
            }))
                cleanupFuture.wait();

            {
                std::lock_guard<std::mutex> commandLock(m_commandMutex);
                m_stopRequested = true;
            }
            m_commandAvailable.notify_all();
            if (m_worker.joinable())
                m_worker.join();
        }

        bool Post(std::function<void()> command)
        {
            if (!command)
                return false;

            {
                std::lock_guard<std::mutex> commandLock(m_commandMutex);
                if (m_stopRequested)
                    return false;
                m_commands.push_back(std::move(command));
            }
            m_commandAvailable.notify_one();
            return true;
        }

        bool Poll(NativeEvent& output)
        {
            std::lock_guard<std::mutex> eventLock(m_eventMutex);
            if (m_events.empty())
                return false;

            output = std::move(m_events.front());
            m_events.pop_front();
            return true;
        }

        void Initialize()
        {
            Post([context = shared_from_this()]() { context->InitializeAsync(); });
        }

        void StartScan(std::wstring deviceName, std::wstring serviceUuid, bool allowDuplicates)
        {
            Post([context = shared_from_this(), deviceName = std::move(deviceName), serviceUuid = std::move(serviceUuid), allowDuplicates]() {
                context->StartScanOnWorker(deviceName, serviceUuid, allowDuplicates);
            });
        }

        void StopScan()
        {
            Post([context = shared_from_this()]() { context->StopScanOnWorker(true); });
        }

        void Connect(std::wstring deviceId)
        {
            Post([context = shared_from_this(), deviceId = std::move(deviceId)]() { context->ConnectAsync(deviceId); });
        }

        void Disconnect()
        {
            Post([context = shared_from_this()]() { context->DisconnectWithLock(true); });
        }

        void DiscoverServices(std::int64_t operationId, bool uncached)
        {
            Post([context = shared_from_this(), operationId, uncached]() { context->DiscoverServicesAsync(operationId, uncached); });
        }

        void SetSubscription(std::int64_t operationId, std::wstring serviceUuid, std::wstring characteristicUuid, bool enabled)
        {
            Post([context = shared_from_this(), operationId, serviceUuid = std::move(serviceUuid), characteristicUuid = std::move(characteristicUuid), enabled]() {
                context->SetSubscriptionAsync(operationId, serviceUuid, characteristicUuid, enabled);
            });
        }

        void Read(std::int64_t operationId, std::wstring serviceUuid, std::wstring characteristicUuid)
        {
            Post([context = shared_from_this(), operationId, serviceUuid = std::move(serviceUuid),
                characteristicUuid = std::move(characteristicUuid)]() {
                context->ReadAsync(operationId, serviceUuid, characteristicUuid);
            });
        }

        void Write(std::int64_t operationId, std::wstring serviceUuid, std::wstring characteristicUuid, std::vector<std::uint8_t> data, std::int32_t writeMode)
        {
            Post([context = shared_from_this(), operationId, serviceUuid = std::move(serviceUuid),
                characteristicUuid = std::move(characteristicUuid), data = std::move(data), writeMode]() {
                context->WriteAsync(operationId, serviceUuid, characteristicUuid, data, writeMode);
            });
        }

    private:
        void WorkerMain()
        {
            try
            {
                init_apartment(apartment_type::multi_threaded);
                m_workerReady = true;
            }
            catch (hresult_error const& exception)
            {
                m_workerError = exception.message().c_str();
            }
            catch (...)
            {
                m_workerError = L"Unable to initialize the Windows Runtime apartment.";
            }

            while (true)
            {
                std::function<void()> command;
                {
                    std::unique_lock<std::mutex> commandLock(m_commandMutex);
                    m_commandAvailable.wait(commandLock, [this]() { return m_stopRequested || !m_commands.empty(); });
                    if (m_stopRequested && m_commands.empty())
                        break;
                    command = std::move(m_commands.front());
                    m_commands.pop_front();
                }

                try
                {
                    command();
                }
                catch (hresult_error const& exception)
                {
                    PushError(errorPlatform, exception.code().value, exception.message().c_str());
                }
                catch (std::exception const& exception)
                {
                    PushError(errorPlatform, 0, Widen(exception.what()));
                }
                catch (...)
                {
                    PushError(errorPlatform, 0, L"Unhandled Windows BLE worker error.");
                }
            }

            if (m_workerReady)
                uninit_apartment();
        }

        fire_and_forget InitializeAsync()
        {
            NativeEvent output = CreateEvent(nativeEventInitialized);
            try
            {
                if (!m_workerReady)
                {
                    output.header.errorCode = errorPlatform;
                    output.textA = m_workerError.empty() ? L"Windows Runtime is unavailable." : m_workerError;
                    Push(std::move(output));
                    co_return;
                }

                BluetoothAdapter nativeAdapter = co_await BluetoothAdapter::GetDefaultAsync();
                if (!nativeAdapter)
                {
                    output.header.errorCode = errorBluetoothDisabled;
                    output.textA = L"No Windows Bluetooth adapter is available.";
                    Push(std::move(output));
                    co_return;
                }
                if (!nativeAdapter.IsLowEnergySupported())
                {
                    output.header.errorCode = errorUnsupported;
                    output.textA = L"The Windows Bluetooth adapter does not support BLE.";
                    Push(std::move(output));
                    co_return;
                }

                output.header.success = 1;
                output.textA = L"Windows BLE adapter is ready.";
            }
            catch (hresult_access_denied const& exception)
            {
                output.header.errorCode = errorPermissionDenied;
                output.header.nativeCode = exception.code().value;
                output.textA = exception.message().c_str();
            }
            catch (hresult_error const& exception)
            {
                output.header.errorCode = errorPlatform;
                output.header.nativeCode = exception.code().value;
                output.textA = exception.message().c_str();
            }
            catch (std::exception const& exception)
            {
                output.header.errorCode = errorPlatform;
                output.textA = Widen(exception.what());
            }
            catch (...)
            {
                output.header.errorCode = errorPlatform;
                output.textA = L"Unable to initialize the Windows BLE adapter.";
            }
            Push(std::move(output));
        }

        void StartScanOnWorker(std::wstring const& deviceName, std::wstring const& serviceUuid, bool allowDuplicates)
        {
            NativeEvent completion = CreateEvent(nativeEventScanStarted);
            try
            {
                StopScanOnWorker(false);
                std::optional<guid> parsedServiceUuid = ParseOptionalGuid(serviceUuid);
                {
                    std::lock_guard<std::mutex> stateLock(m_stateMutex);
                    m_scanDeviceName = deviceName;
                    m_allowDuplicateAdvertisements = allowDuplicates;
                    m_reportedDeviceIds.clear();
                }

                m_watcher = BluetoothLEAdvertisementWatcher();
                m_watcher.ScanningMode(BluetoothLEScanningMode::Active);
                if (parsedServiceUuid.has_value())
                    m_watcher.AdvertisementFilter().Advertisement().ServiceUuids().Append(parsedServiceUuid.value());

                std::weak_ptr<BleContext> weakContext = shared_from_this();
                m_watcherReceivedToken = m_watcher.Received([weakContext](BluetoothLEAdvertisementWatcher const&, BluetoothLEAdvertisementReceivedEventArgs const& args) {
                    if (std::shared_ptr<BleContext> context = weakContext.lock())
                        context->HandleAdvertisement(args);
                });
                m_watcherStoppedToken = m_watcher.Stopped([weakContext](BluetoothLEAdvertisementWatcher const&, BluetoothLEAdvertisementWatcherStoppedEventArgs const& args) {
                    if (std::shared_ptr<BleContext> context = weakContext.lock())
                        context->HandleWatcherStopped(args);
                });
                m_watcher.Start();
                completion.header.success = 1;
                completion.textA = L"Windows BLE scan started.";
            }
            catch (hresult_access_denied const& exception)
            {
                StopScanOnWorker(false);
                completion.header.errorCode = errorPermissionDenied;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (hresult_error const& exception)
            {
                StopScanOnWorker(false);
                completion.header.errorCode = errorPlatform;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (std::exception const& exception)
            {
                StopScanOnWorker(false);
                completion.header.errorCode = errorInvalidArgument;
                completion.textA = Widen(exception.what());
            }
            catch (...)
            {
                StopScanOnWorker(false);
                completion.header.errorCode = errorPlatform;
                completion.textA = L"Unable to start the Windows BLE scan.";
            }
            Push(std::move(completion));
        }

        void StopScanOnWorker(bool notify)
        {
            if (m_watcher)
            {
                try
                {
                    m_watcher.Received(m_watcherReceivedToken);
                    m_watcher.Stopped(m_watcherStoppedToken);
                    if (m_watcher.Status() == BluetoothLEAdvertisementWatcherStatus::Started || m_watcher.Status() == BluetoothLEAdvertisementWatcherStatus::Stopping)
                        m_watcher.Stop();
                }
                catch (...)
                {
                }
                m_watcher = nullptr;
            }

            if (notify)
                Push(CreateEvent(nativeEventScanStopped, true));
        }

        fire_and_forget ConnectAsync(std::wstring deviceId)
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            try
            {
                StopScanOnWorker(false);
                DisconnectOnWorker(false);
                std::uint64_t address = ParseBluetoothAddress(deviceId);
                BluetoothLEDevice device = co_await BluetoothLEDevice::FromBluetoothAddressAsync(address);
                if (!device)
                {
                    PushConnectionState(false, errorDeviceNotFound, 0, L"Windows could not resolve the BLE device.");
                    co_return;
                }

                m_device = device;
                std::weak_ptr<BleContext> weakContext = shared_from_this();
                m_connectionStatusToken = m_device.ConnectionStatusChanged([weakContext](BluetoothLEDevice const& sender, Windows::Foundation::IInspectable const&) {
                    if (std::shared_ptr<BleContext> context = weakContext.lock())
                        context->HandleConnectionStatusChanged(sender);
                });

                GattDeviceServicesResult probe = co_await m_device.GetGattServicesAsync(BluetoothCacheMode::Uncached);
                if (probe.Status() != GattCommunicationStatus::Success)
                {
                    std::int32_t nativeStatus = static_cast<std::int32_t>(probe.Status());
                    DisconnectOnWorker(false);
                    PushConnectionState(false, MapCommunicationError(probe.Status()), nativeStatus, L"Windows could not establish a GATT connection.");
                    co_return;
                }

                for (GattDeviceService const& service : probe.Services())
                    service.Close();
                PushConnectionState(true, errorNone, 0, L"Windows GATT connection is ready.");
            }
            catch (hresult_access_denied const& exception)
            {
                DisconnectOnWorker(false);
                PushConnectionState(false, errorPermissionDenied, exception.code().value, exception.message().c_str());
            }
            catch (hresult_error const& exception)
            {
                DisconnectOnWorker(false);
                PushConnectionState(false, errorDeviceUnreachable, exception.code().value, exception.message().c_str());
            }
            catch (std::exception const& exception)
            {
                DisconnectOnWorker(false);
                PushConnectionState(false, errorInvalidArgument, 0, Widen(exception.what()));
            }
            catch (...)
            {
                DisconnectOnWorker(false);
                PushConnectionState(false, errorPlatform, 0, L"Unable to connect to the Windows BLE device.");
            }
        }

        void DisconnectOnWorker(bool notify)
        {
            std::lock_guard<std::mutex> stateLock(m_stateMutex);
            for (auto& pair : m_characteristics)
            {
                CharacteristicEntry& entry = pair.second;
                if (entry.subscribed && entry.characteristic)
                {
                    try
                    {
                        entry.characteristic.ValueChanged(entry.valueChangedToken);
                    }
                    catch (...)
                    {
                    }
                }
            }
            m_characteristics.clear();

            for (GattDeviceService const& service : m_services)
                service.Close();
            m_services.clear();

            if (m_device)
            {
                try
                {
                    m_device.ConnectionStatusChanged(m_connectionStatusToken);
                    m_device.Close();
                }
                catch (...)
                {
                }
                m_device = nullptr;
            }

            if (notify)
                PushConnectionState(false, errorNone, 0, L"Windows GATT connection was closed.");
        }

        void DisconnectWithLock(bool notify)
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            DisconnectOnWorker(notify);
        }

        fire_and_forget DiscoverServicesAsync(std::int64_t operationId, bool uncached)
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            NativeEvent completion = CreateOperationEvent(nativeEventDiscoveryComplete, operationId);
            try
            {
                if (!m_device)
                {
                    completion.header.errorCode = errorInvalidState;
                    completion.textA = L"An active Windows BLE connection is required.";
                    Push(std::move(completion));
                    co_return;
                }

                BluetoothCacheMode cacheMode = uncached ? BluetoothCacheMode::Uncached : BluetoothCacheMode::Cached;
                GattDeviceServicesResult serviceResult = co_await m_device.GetGattServicesAsync(cacheMode);
                if (serviceResult.Status() != GattCommunicationStatus::Success)
                {
                    completion.header.errorCode = MapCommunicationError(serviceResult.Status());
                    completion.header.nativeCode = static_cast<std::int32_t>(serviceResult.Status());
                    completion.textA = L"Windows GATT service discovery failed.";
                    Push(std::move(completion));
                    co_return;
                }

                std::lock_guard<std::mutex> stateLock(m_stateMutex);
                ClearGattCache();
                for (GattDeviceService const& service : serviceResult.Services())
                {
                    m_services.push_back(service);
                    GattCharacteristicsResult characteristicResult = co_await service.GetCharacteristicsAsync(cacheMode);
                    if (characteristicResult.Status() != GattCommunicationStatus::Success)
                        continue;

                    for (GattCharacteristic const& characteristic : characteristicResult.Characteristics())
                    {
                        std::wstring serviceUuid = ToWide(service.Uuid());
                        std::wstring characteristicUuid = ToWide(characteristic.Uuid());
                        std::wstring key = CreateCharacteristicKey(serviceUuid, characteristicUuid);
                        CharacteristicEntry entry;
                        entry.characteristic = characteristic;
                        m_characteristics.insert_or_assign(key, entry);

                        NativeEvent discovered = CreateEvent(nativeEventCharacteristicDiscovered, true);
                        discovered.textA = serviceUuid;
                        discovered.textB = characteristicUuid;
                        discovered.header.valueA = ToUnityProperties(characteristic.CharacteristicProperties());
                        Push(std::move(discovered));
                    }
                }

                completion.header.success = 1;
                completion.textA = L"Windows GATT discovery completed.";
            }
            catch (hresult_access_denied const& exception)
            {
                completion.header.errorCode = errorPermissionDenied;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (hresult_error const& exception)
            {
                completion.header.errorCode = errorPlatform;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (std::exception const& exception)
            {
                completion.header.errorCode = errorPlatform;
                completion.textA = Widen(exception.what());
            }
            catch (...)
            {
                completion.header.errorCode = errorPlatform;
                completion.textA = L"Unable to discover Windows GATT services.";
            }
            Push(std::move(completion));
        }

        fire_and_forget SetSubscriptionAsync(std::int64_t operationId, std::wstring serviceUuid, std::wstring characteristicUuid, bool enabled)
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            NativeEvent completion = CreateOperationEvent(nativeEventOperationComplete, operationId);
            try
            {
                CharacteristicEntry* entry = FindCharacteristic(serviceUuid, characteristicUuid);
                if (entry == nullptr)
                {
                    completion.header.errorCode = errorCharacteristicNotFound;
                    completion.textA = L"Windows GATT characteristic was not found.";
                    Push(std::move(completion));
                    co_return;
                }

                GattClientCharacteristicConfigurationDescriptorValue descriptorValue = GattClientCharacteristicConfigurationDescriptorValue::None;
                if (enabled)
                {
                    GattCharacteristicProperties properties = entry->characteristic.CharacteristicProperties();
                    if ((properties & GattCharacteristicProperties::Notify) == GattCharacteristicProperties::Notify)
                        descriptorValue = GattClientCharacteristicConfigurationDescriptorValue::Notify;
                    else if ((properties & GattCharacteristicProperties::Indicate) == GattCharacteristicProperties::Indicate)
                        descriptorValue = GattClientCharacteristicConfigurationDescriptorValue::Indicate;
                    else
                    {
                        completion.header.errorCode = errorSubscriptionFailed;
                        completion.textA = L"The Windows GATT characteristic does not support notifications or indications.";
                        Push(std::move(completion));
                        co_return;
                    }
                }

                GattWriteResult result = co_await entry->characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(descriptorValue);
                if (result.Status() != GattCommunicationStatus::Success)
                {
                    completion.header.errorCode = errorSubscriptionFailed;
                    completion.header.nativeCode = static_cast<std::int32_t>(result.Status());
                    completion.textA = L"Windows rejected the GATT subscription update.";
                    Push(std::move(completion));
                    co_return;
                }

                if (entry->subscribed)
                {
                    entry->characteristic.ValueChanged(entry->valueChangedToken);
                    entry->subscribed = false;
                }
                if (enabled)
                {
                    std::weak_ptr<BleContext> weakContext = shared_from_this();
                    entry->valueChangedToken = entry->characteristic.ValueChanged(
                        [weakContext, serviceUuid, characteristicUuid](GattCharacteristic const&, GattValueChangedEventArgs const& args) {
                        if (std::shared_ptr<BleContext> context = weakContext.lock())
                            context->HandleNotification(serviceUuid, characteristicUuid, args.CharacteristicValue());
                        });
                    entry->subscribed = true;
                }

                completion.header.success = 1;
                completion.textA = enabled ? L"Windows GATT subscription enabled." : L"Windows GATT subscription disabled.";
            }
            catch (hresult_access_denied const& exception)
            {
                completion.header.errorCode = errorPermissionDenied;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (hresult_error const& exception)
            {
                completion.header.errorCode = errorSubscriptionFailed;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (std::exception const& exception)
            {
                completion.header.errorCode = errorSubscriptionFailed;
                completion.textA = Widen(exception.what());
            }
            catch (...)
            {
                completion.header.errorCode = errorSubscriptionFailed;
                completion.textA = L"Unable to update the Windows GATT subscription.";
            }
            Push(std::move(completion));
        }

        fire_and_forget ReadAsync(std::int64_t operationId, std::wstring serviceUuid, std::wstring characteristicUuid)
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            NativeEvent completion = CreateOperationEvent(nativeEventOperationComplete, operationId);
            try
            {
                CharacteristicEntry* entry = FindCharacteristic(serviceUuid, characteristicUuid);
                if (entry == nullptr)
                {
                    completion.header.errorCode = errorCharacteristicNotFound;
                    completion.textA = L"Windows GATT characteristic was not found.";
                    Push(std::move(completion));
                    co_return;
                }

                GattCharacteristicProperties properties = entry->characteristic.CharacteristicProperties();
                if ((properties & GattCharacteristicProperties::Read) != GattCharacteristicProperties::Read)
                {
                    completion.header.errorCode = errorReadRejected;
                    completion.textA = L"The Windows GATT characteristic is not readable.";
                    Push(std::move(completion));
                    co_return;
                }

                GattReadResult result = co_await entry->characteristic.ReadValueAsync(BluetoothCacheMode::Uncached);
                if (result.Status() != GattCommunicationStatus::Success)
                {
                    completion.header.errorCode = errorReadRejected;
                    completion.header.nativeCode = static_cast<std::int32_t>(result.Status());
                    completion.textA = L"Windows rejected the GATT characteristic read.";
                    Push(std::move(completion));
                    co_return;
                }

                completion.header.success = 1;
                completion.textA = L"Windows GATT characteristic read completed.";
                completion.data = ReadBuffer(result.Value());
                completion.header.dataLength = static_cast<std::int32_t>(completion.data.size());
            }
            catch (hresult_access_denied const& exception)
            {
                completion.header.errorCode = errorPermissionDenied;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (hresult_error const& exception)
            {
                completion.header.errorCode = errorReadRejected;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (std::exception const& exception)
            {
                completion.header.errorCode = errorReadRejected;
                completion.textA = Widen(exception.what());
            }
            catch (...)
            {
                completion.header.errorCode = errorReadRejected;
                completion.textA = L"Unable to read the Windows GATT characteristic.";
            }
            Push(std::move(completion));
        }

        fire_and_forget WriteAsync(std::int64_t operationId, std::wstring serviceUuid, std::wstring characteristicUuid, std::vector<std::uint8_t> data, std::int32_t writeMode)
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            NativeEvent completion = CreateOperationEvent(nativeEventOperationComplete, operationId);
            try
            {
                CharacteristicEntry* entry = FindCharacteristic(serviceUuid, characteristicUuid);
                if (entry == nullptr)
                {
                    completion.header.errorCode = errorCharacteristicNotFound;
                    completion.textA = L"Windows GATT characteristic was not found.";
                    Push(std::move(completion));
                    co_return;
                }
                if (data.empty())
                {
                    completion.header.errorCode = errorInvalidArgument;
                    completion.textA = L"A BLE write payload cannot be empty.";
                    Push(std::move(completion));
                    co_return;
                }

                GattWriteOption option = writeMode == 0 ? GattWriteOption::WriteWithResponse : GattWriteOption::WriteWithoutResponse;
                DataWriter writer;
                writer.WriteBytes(data);
                IBuffer buffer = writer.DetachBuffer();
                GattWriteResult result = co_await entry->characteristic.WriteValueWithResultAsync(buffer, option);
                if (result.Status() != GattCommunicationStatus::Success)
                {
                    completion.header.errorCode = errorWriteRejected;
                    completion.header.nativeCode = static_cast<std::int32_t>(result.Status());
                    completion.textA = L"Windows rejected the GATT characteristic write.";
                    Push(std::move(completion));
                    co_return;
                }

                completion.header.success = 1;
                completion.textA = L"Windows GATT characteristic write completed.";
            }
            catch (hresult_access_denied const& exception)
            {
                completion.header.errorCode = errorPermissionDenied;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (hresult_error const& exception)
            {
                completion.header.errorCode = errorWriteRejected;
                completion.header.nativeCode = exception.code().value;
                completion.textA = exception.message().c_str();
            }
            catch (std::exception const& exception)
            {
                completion.header.errorCode = errorWriteRejected;
                completion.textA = Widen(exception.what());
            }
            catch (...)
            {
                completion.header.errorCode = errorWriteRejected;
                completion.textA = L"Unable to write the Windows GATT characteristic.";
            }
            Push(std::move(completion));
        }

        void HandleAdvertisement(BluetoothLEAdvertisementReceivedEventArgs const& args)
        {
            BluetoothLEAdvertisement advertisement = args.Advertisement();
            std::wstring name = advertisement.LocalName().c_str();
            std::wstring deviceId = FormatBluetoothAddress(args.BluetoothAddress());
            {
                std::lock_guard<std::mutex> stateLock(m_stateMutex);
                if (!m_scanDeviceName.empty() && name != m_scanDeviceName)
                    return;
                if (!m_allowDuplicateAdvertisements && m_reportedDeviceIds.find(deviceId) != m_reportedDeviceIds.end())
                    return;
                m_reportedDeviceIds.insert(deviceId);
            }

            NativeEvent output = CreateEvent(nativeEventAdvertisement, true);
            output.textA = deviceId;
            output.textB = name;
            output.textC = JoinServiceUuids(advertisement.ServiceUuids());
            output.header.valueA = args.RawSignalStrengthInDBm();
            BluetoothLEAdvertisementType advertisementType = args.AdvertisementType();
            bool isConnectable = advertisementType == BluetoothLEAdvertisementType::ConnectableDirected ||
                advertisementType == BluetoothLEAdvertisementType::ConnectableUndirected;
            output.header.valueB = isConnectable ? 1 : 0;
            output.data = ReadManufacturerData(advertisement);
            output.header.dataLength = static_cast<std::int32_t>(output.data.size());
            Push(std::move(output));
        }

        void HandleWatcherStopped(BluetoothLEAdvertisementWatcherStoppedEventArgs const& args)
        {
            NativeEvent output = CreateEvent(nativeEventScanStopped, args.Error() == BluetoothError::Success);
            output.header.errorCode = args.Error() == BluetoothError::Success ? errorNone : errorPlatform;
            output.header.nativeCode = static_cast<std::int32_t>(args.Error());
            output.textA = args.Error() == BluetoothError::Success ? L"Windows BLE scan stopped." : L"Windows BLE advertisement watcher stopped with an error.";
            Push(std::move(output));
        }

        void HandleConnectionStatusChanged(BluetoothLEDevice const& sender)
        {
            bool connected = sender.ConnectionStatus() == BluetoothConnectionStatus::Connected;
            std::wstring detail = connected ? L"Windows GATT connection is active." : L"Windows GATT connection was lost.";
            PushConnectionState(connected, connected ? errorNone : errorDeviceUnreachable, 0, detail);
        }

        void HandleNotification(std::wstring const& serviceUuid, std::wstring const& characteristicUuid, IBuffer const& buffer)
        {
            NativeEvent output = CreateEvent(nativeEventNotification, true);
            output.textA = serviceUuid;
            output.textB = characteristicUuid;
            output.data = ReadBuffer(buffer);
            output.header.dataLength = static_cast<std::int32_t>(output.data.size());
            Push(std::move(output));
        }

        void Cleanup()
        {
            std::unique_lock<OperationGate> operationLock(m_operationGate);
            StopScanOnWorker(false);
            DisconnectOnWorker(false);
        }

        void ClearGattCache()
        {
            for (auto& pair : m_characteristics)
            {
                CharacteristicEntry& entry = pair.second;
                if (entry.subscribed && entry.characteristic)
                    entry.characteristic.ValueChanged(entry.valueChangedToken);
            }
            m_characteristics.clear();
            for (GattDeviceService const& service : m_services)
                service.Close();
            m_services.clear();
        }

        CharacteristicEntry* FindCharacteristic(std::wstring const& serviceUuid, std::wstring const& characteristicUuid)
        {
            std::lock_guard<std::mutex> stateLock(m_stateMutex);
            std::wstring key = CreateCharacteristicKey(serviceUuid, characteristicUuid);
            auto iterator = m_characteristics.find(key);
            return iterator == m_characteristics.end() ? nullptr : &iterator->second;
        }

        void Push(NativeEvent output)
        {
            std::lock_guard<std::mutex> eventLock(m_eventMutex);
            m_events.push_back(std::move(output));
        }

        void PushError(std::int32_t errorCode, std::int32_t nativeCode, std::wstring detail)
        {
            NativeEvent output = CreateEvent(nativeEventError);
            output.header.errorCode = errorCode;
            output.header.nativeCode = nativeCode;
            output.textA = std::move(detail);
            Push(std::move(output));
        }

        void PushConnectionState(bool connected, std::int32_t errorCode, std::int32_t nativeCode, std::wstring detail)
        {
            NativeEvent output = CreateEvent(nativeEventConnectionState, connected);
            output.header.valueA = connected ? 1 : 0;
            output.header.errorCode = errorCode;
            output.header.nativeCode = nativeCode;
            output.textA = std::move(detail);
            Push(std::move(output));
        }

        static NativeEvent CreateEvent(std::int32_t eventType, bool success = false)
        {
            NativeEvent output;
            output.header.eventType = eventType;
            output.header.success = success ? 1 : 0;
            return output;
        }

        static NativeEvent CreateOperationEvent(std::int32_t eventType, std::int64_t operationId)
        {
            NativeEvent output = CreateEvent(eventType);
            output.header.operationId = operationId;
            return output;
        }

        static std::optional<guid> ParseOptionalGuid(std::wstring const& value)
        {
            if (value.empty())
                return std::nullopt;
            return guid(value);
        }

        static std::uint64_t ParseBluetoothAddress(std::wstring const& value)
        {
            if (value.empty())
                throw std::invalid_argument("A Windows BLE device identifier is required.");
            return std::stoull(value, nullptr, 16);
        }

        static std::wstring FormatBluetoothAddress(std::uint64_t address)
        {
            std::wstringstream stream;
            stream << std::uppercase << std::hex << std::setw(12) << std::setfill(L'0') << address;
            return stream.str();
        }

        static std::wstring CreateCharacteristicKey(std::wstring serviceUuid, std::wstring characteristicUuid)
        {
            std::transform(serviceUuid.begin(), serviceUuid.end(), serviceUuid.begin(), ::towlower);
            std::transform(characteristicUuid.begin(), characteristicUuid.end(), characteristicUuid.begin(), ::towlower);
            return serviceUuid + L"/" + characteristicUuid;
        }

        static std::wstring ToWide(guid const& value)
        {
            return to_hstring(value).c_str();
        }

        static std::wstring JoinServiceUuids(IVector<guid> const& values)
        {
            std::wstring result;
            for (guid const& value : values)
            {
                if (!result.empty())
                    result += L",";
                result += ToWide(value);
            }
            return result;
        }

        static std::vector<std::uint8_t> ReadManufacturerData(BluetoothLEAdvertisement const& advertisement)
        {
            IVector<BluetoothLEManufacturerData> values = advertisement.ManufacturerData();
            if (values.Size() == 0)
                return {};
            return ReadBuffer(values.GetAt(0).Data());
        }

        static std::vector<std::uint8_t> ReadBuffer(IBuffer const& buffer)
        {
            if (!buffer || buffer.Length() == 0)
                return {};

            DataReader reader = DataReader::FromBuffer(buffer);
            std::vector<std::uint8_t> data(reader.UnconsumedBufferLength());
            reader.ReadBytes(data);
            return data;
        }

        static std::int32_t ToUnityProperties(GattCharacteristicProperties properties)
        {
            std::int32_t result = 0;
            if ((properties & GattCharacteristicProperties::Read) == GattCharacteristicProperties::Read)
                result |= unityPropertyRead;
            if ((properties & GattCharacteristicProperties::Write) == GattCharacteristicProperties::Write)
                result |= unityPropertyWrite;
            if ((properties & GattCharacteristicProperties::WriteWithoutResponse) == GattCharacteristicProperties::WriteWithoutResponse)
                result |= unityPropertyWriteWithoutResponse;
            if ((properties & GattCharacteristicProperties::Notify) == GattCharacteristicProperties::Notify)
                result |= unityPropertyNotify;
            if ((properties & GattCharacteristicProperties::Indicate) == GattCharacteristicProperties::Indicate)
                result |= unityPropertyIndicate;
            return result;
        }

        static std::int32_t MapCommunicationError(GattCommunicationStatus status)
        {
            if (status == GattCommunicationStatus::AccessDenied)
                return errorPermissionDenied;
            if (status == GattCommunicationStatus::Unreachable)
                return errorDeviceUnreachable;
            if (status == GattCommunicationStatus::ProtocolError)
                return errorPlatform;
            return status == GattCommunicationStatus::Success ? errorNone : errorPlatform;
        }

        static std::wstring Widen(std::string const& value)
        {
            return std::wstring(value.begin(), value.end());
        }

        std::thread m_worker;
        std::mutex m_lifecycleMutex;
        std::mutex m_commandMutex;
        std::condition_variable m_commandAvailable;
        std::deque<std::function<void()>> m_commands;
        bool m_stopRequested = false;
        bool m_stopped = false;
        bool m_workerReady = false;
        std::wstring m_workerError;

        std::mutex m_eventMutex;
        std::deque<NativeEvent> m_events;
        OperationGate m_operationGate;
        std::mutex m_stateMutex;

        BluetoothLEAdvertisementWatcher m_watcher{ nullptr };
        event_token m_watcherReceivedToken{};
        event_token m_watcherStoppedToken{};
        std::wstring m_scanDeviceName;
        bool m_allowDuplicateAdvertisements = false;
        std::set<std::wstring> m_reportedDeviceIds;

        BluetoothLEDevice m_device{ nullptr };
        event_token m_connectionStatusToken{};
        std::vector<GattDeviceService> m_services;
        std::map<std::wstring, CharacteristicEntry> m_characteristics;
    };

    struct BridgeHandle
    {
        std::shared_ptr<BleContext> context;
    };

    BridgeHandle* GetHandle(void* handle)
    {
        return static_cast<BridgeHandle*>(handle);
    }

    std::wstring CopyInput(const wchar_t* value)
    {
        return value == nullptr ? std::wstring() : std::wstring(value);
    }

    bool CopyText(std::wstring const& source, wchar_t* destination, std::int32_t capacity)
    {
        if (destination == nullptr || capacity <= 0)
            return source.empty();
        if (source.size() + 1 > static_cast<std::size_t>(capacity))
            return false;

        std::copy(source.begin(), source.end(), destination);
        destination[source.size()] = L'\0';
        return true;
    }
}

extern "C"
{
    void* __cdecl spax_ble_create()
    {
        try
        {
            std::unique_ptr<BridgeHandle> handle = std::make_unique<BridgeHandle>();
            handle->context = std::make_shared<BleContext>();
            handle->context->Start();
            return handle.release();
        }
        catch (...)
        {
            return nullptr;
        }
    }

    void __cdecl spax_ble_destroy(void* handle)
    {
        std::unique_ptr<BridgeHandle> ownedHandle(GetHandle(handle));
        if (ownedHandle == nullptr || ownedHandle->context == nullptr)
            return;
        ownedHandle->context->Stop();
        ownedHandle->context.reset();
    }

    std::int32_t __cdecl spax_ble_initialize(void* handle)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        bridge->context->Initialize();
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_start_scan(void* handle, const wchar_t* deviceName, const wchar_t* serviceUuid, std::int32_t allowDuplicates)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        bridge->context->StartScan(CopyInput(deviceName), CopyInput(serviceUuid), allowDuplicates != 0);
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_stop_scan(void* handle)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        bridge->context->StopScan();
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_connect(void* handle, const wchar_t* deviceId)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        if (deviceId == nullptr || *deviceId == L'\0')
            return errorInvalidArgument;
        bridge->context->Connect(CopyInput(deviceId));
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_disconnect(void* handle)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        bridge->context->Disconnect();
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_discover_services(void* handle, std::int64_t operationId, std::int32_t uncached)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        bridge->context->DiscoverServices(operationId, uncached != 0);
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_set_subscription(void* handle, std::int64_t operationId, const wchar_t* serviceUuid, const wchar_t* characteristicUuid, std::int32_t enabled)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        if (serviceUuid == nullptr || characteristicUuid == nullptr)
            return errorInvalidArgument;
        bridge->context->SetSubscription(operationId, CopyInput(serviceUuid), CopyInput(characteristicUuid), enabled != 0);
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_read(void* handle, std::int64_t operationId, const wchar_t* serviceUuid, const wchar_t* characteristicUuid)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        if (serviceUuid == nullptr || characteristicUuid == nullptr)
            return errorInvalidArgument;
        bridge->context->Read(operationId, CopyInput(serviceUuid), CopyInput(characteristicUuid));
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_write(void* handle, std::int64_t operationId, const wchar_t* serviceUuid,
        const wchar_t* characteristicUuid, const std::uint8_t* data, std::int32_t dataLength, std::int32_t writeMode)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr)
            return errorDisposed;
        if (serviceUuid == nullptr || characteristicUuid == nullptr || data == nullptr || dataLength <= 0 || (writeMode != 0 && writeMode != 1))
            return errorInvalidArgument;

        std::vector<std::uint8_t> copy(data, data + dataLength);
        bridge->context->Write(operationId, CopyInput(serviceUuid), CopyInput(characteristicUuid), std::move(copy), writeMode);
        return errorNone;
    }

    std::int32_t __cdecl spax_ble_poll_event(void* handle, SpaxBleEventHeader* header, wchar_t* textA,
        std::int32_t textACapacity, wchar_t* textB, std::int32_t textBCapacity, wchar_t* textC,
        std::int32_t textCCapacity, std::uint8_t* data, std::int32_t dataCapacity)
    {
        BridgeHandle* bridge = GetHandle(handle);
        if (bridge == nullptr || bridge->context == nullptr || header == nullptr)
            return errorDisposed;

        NativeEvent output;
        if (!bridge->context->Poll(output))
            return 0;
        bool textFits = CopyText(output.textA, textA, textACapacity) && CopyText(output.textB, textB, textBCapacity) &&
            CopyText(output.textC, textC, textCCapacity);
        bool dataFits = output.data.size() <= static_cast<std::size_t>(std::max(dataCapacity, 0));
        if (!textFits || !dataFits)
            return -1;

        *header = output.header;
        if (!output.data.empty() && data != nullptr)
            std::copy(output.data.begin(), output.data.end(), data);
        return 1;
    }
}
