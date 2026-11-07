# Changelog

## 0.1.4

- Corrected the Unity Package Manager version metadata after the `v0.1.3` tag retained the previous `0.1.2` package version.
- Includes the fixed UTF-16 Windows event buffers introduced in `v0.1.3`, with no public API, Unity asset GUID, or native entry-point ABI changes.

## 0.1.3

- Replaced Windows native event text `StringBuilder` marshalling with fixed UTF-16 buffers so buffer capacity is explicit and consistent under Mono and IL2CPP.
- Preserved the public managed API, Unity asset GUIDs, and Windows native entry-point ABI. Updating from an earlier `0.1.x` release requires no application code changes.

## 0.1.2

- Fixed a Windows native use-after-free when the BLE context was destroyed while initialization or another asynchronous operation was still running.
- Fixed undefined behavior caused by holding a thread-affine mutex across Windows Runtime coroutine suspension points during GATT discovery.
- Hardened Windows shutdown so cleanup drains accepted work, rejects new commands, unregisters callbacks, and closes GATT resources safely.
- Rebuilt the bundled Windows x64 plug-in without changing the public managed API or native entry-point ABI. Updating from `0.1.0` or `0.1.1` requires no application code changes.

## 0.1.1

- Fixed Windows GATT characteristic lookup when discovered UUIDs include braces but managed operation UUIDs do not.

## 0.1.0

- Added protocol-agnostic BLE adapter and connection abstractions.
- Added Unity `BleManager` facade with main-thread event dispatch.
- Added Android Java GATT backend.
- Added Windows C++/WinRT backend source and C# native wrapper.
- Added fake adapter and core tests.
- Added cross-platform GATT characteristic reads.
- Included the built Windows x64 native plug-in and diagnostics sample.
