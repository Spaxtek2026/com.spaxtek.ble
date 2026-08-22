#pragma once

#include <cstdint>

#ifdef SPAXTEK_BLE_WINDOWS_EXPORTS
#define SPAXTEK_BLE_API __declspec(dllexport)
#else
#define SPAXTEK_BLE_API __declspec(dllimport)
#endif

extern "C"
{
    struct SpaxBleEventHeader
    {
        std::int32_t eventType;
        std::int32_t success;
        std::int64_t operationId;
        std::int32_t errorCode;
        std::int32_t nativeCode;
        std::int32_t valueA;
        std::int32_t valueB;
        std::int32_t dataLength;
    };

    SPAXTEK_BLE_API void* __cdecl spax_ble_create();
    SPAXTEK_BLE_API void __cdecl spax_ble_destroy(void* handle);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_initialize(void* handle);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_start_scan(void* handle, const wchar_t* deviceName, const wchar_t* serviceUuid, std::int32_t allowDuplicates);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_stop_scan(void* handle);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_connect(void* handle, const wchar_t* deviceId);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_disconnect(void* handle);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_discover_services(void* handle, std::int64_t operationId, std::int32_t uncached);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_set_subscription(void* handle, std::int64_t operationId,
        const wchar_t* serviceUuid, const wchar_t* characteristicUuid, std::int32_t enabled);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_read(void* handle, std::int64_t operationId,
        const wchar_t* serviceUuid, const wchar_t* characteristicUuid);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_write(void* handle, std::int64_t operationId,
        const wchar_t* serviceUuid, const wchar_t* characteristicUuid, const std::uint8_t* data,
        std::int32_t dataLength, std::int32_t writeMode);
    SPAXTEK_BLE_API std::int32_t __cdecl spax_ble_poll_event(void* handle, SpaxBleEventHeader* header,
        wchar_t* textA, std::int32_t textACapacity, wchar_t* textB, std::int32_t textBCapacity,
        wchar_t* textC, std::int32_t textCCapacity, std::uint8_t* data, std::int32_t dataCapacity);
}
