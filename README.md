# Spaxtek BLE

`com.spaxtek.ble` is a protocol-agnostic BLE client package for Unity. It exposes one shared API for scanning, connecting, discovering GATT services, reading, writing, and subscribing to notifications.

Supported backends:

- Android through a Java GATT bridge.
- Windows 10/11 x64 through a C++/WinRT native plug-in.
- Fake in-memory adapter for automated tests and Editor-only simulations.

The package intentionally does not contain application protocols. Packet layouts, ACK/retry policies, and gameplay integration belong in a separate layer above `BleManager`.

## Runtime setup

1. Add `BleManager` to one persistent scene object.
2. Subscribe to events before calling `InitializeAsync`.
3. Scan with a `BleScanFilter`.
4. Connect using the opaque `BleDeviceId` from an advertisement.
5. Discover services, then read, subscribe, or write through the active `IBleConnection`.

All events exposed by `BleManager` are dispatched on the Unity main thread. Platform adapter and connection events may originate from native worker threads. Each adapter owns at most one active connection, and scanning is stopped before connecting.

`BleManager.UseAdapter` replaces the platform backend with another `IBleAdapter`, including `FakeBleAdapter`. Disable `Initialize On Awake` before injecting a custom adapter from another component.

## Android

The Android library contributes the required Bluetooth manifest entries and requests runtime permissions through Unity. Android 12 or newer uses `BLUETOOTH_SCAN` and `BLUETOOTH_CONNECT`; older versions use fine location for scanning. The manifest declares BLE hardware as optional so unsupported devices can fail through the normal adapter state and error API.

## Windows native plug-in

The ready-to-use x64 DLL is included under `Runtime/Plugins/Windows/x86_64`. Its C++/WinRT source project lives under `Native~/Windows`; rebuild `Release|x64` and replace the DLL when changing native code. Retarget the Visual Studio platform toolset if the checked-in toolset is unavailable locally.

Windows support currently targets Windows 10/11 x64 Standalone and the Windows x64 Unity Editor.

## Updating

`0.1.x` patch releases preserve the public managed API, assembly names, Unity asset GUIDs, and Windows native entry points. Updating between `0.1.x` versions does not require application code changes. Projects that reference a Git tag only need to change the package revision to `v0.1.4` and let Unity refresh the package; restart the Editor if Windows still has the previous native DLL loaded.

## Scope

Connection retry, application packet framing, ACK policies, MTU-aware payload fragmentation, and device-specific protocols intentionally remain above this package. They vary by product and can be layered over `IBleConnection` without changing either platform backend.

## Namespaces

- `Spaxtek.Ble.Core`: platform-independent contracts and models.
- `Spaxtek.Ble.Unity`: Unity lifecycle, facade, and main-thread dispatch.
- `Spaxtek.Ble.Platforms.Android`: Android backend.
- `Spaxtek.Ble.Platforms.Windows`: Windows backend.
- `Spaxtek.Ble.Testing`: deterministic fake backend.
