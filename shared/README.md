# Shared IPC contract

Status: v1.0 draft. This directory is the contract between the native, Python, and WPF modules. Implementations must not depend on another module's source code.

| Channel | Producer | Consumer | Transport | Specification |
|---|---|---|---|---|
| Telemetry | native/plugin | python | 4096-byte Windows shared mapping; 240-byte v1.0 struct | [telemetry v1](ipc/telemetry.v1.md) |
| Game frame | native/plugin | ui | 43,264-byte Windows shared mapping; 64-byte v1.0 header and 160 × 90 RGB24 frame | [gameframe v1](ipc/gameframe.v1.md) |
| Perception | ui | python | 43,264-byte Windows shared mapping; 64-byte v1.0 header and 160 × 90 RGB24 frame | [perception v1](ipc/perception.v1.md) |
| Physical input | ui | native/input | Windows named pipe; 24-byte v1.0 state frames, 50 ms updates, 250 ms native receive expiry | [physical input v1](ipc/physical-input.v1.md) |
| Control | python commands; native/input status | native/input commands; python status | Windows named pipe, overlapped I/O; v1.1 command claims and server capabilities | [control v1](ipc/control.v1.md) |
| UI | python and ui | ui and python | Windows named pipe; v1.3 adds an optional navigation-duration snapshot; v1.1 perception and v1.2 native-control status remain compatible; engage uses state_revision | [ui v1](ipc/ui.v1.md), [JSON Schema](ipc/ui.v1.schema.json) |

## Common representation

- The supported platform is Windows 10/11 x64. Binary integers and IEEE 754 binary64 values use little-endian byte order. Binary structures use 8-byte packing. Protocol booleans are uint8 values 0 or 1, never a language-native bool.
- Physical quantities use SI units: m, m/s, rad, rad/s, and s. Normalized controls use [0,1] or [-1,1]. Engine angular speed is rad/s; SDK RPM is converted by 2π/60.
- A timestamp is an unsigned 64-bit integer count of nanoseconds from the machine's monotonic QueryPerformanceCounter (QPC) clock. The integer conversion formula and language-specific clocks are defined in [telemetry v1](ipc/telemetry.v1.md#time-and-publication). It is not Unix time. The JSON representation is a base-10 string to avoid integer precision loss. Values are comparable only on the same Windows boot.
- The world frame follows the SCS Telemetry SDK frame: +X east, +Y up, +Z south (right-handed), as confirmed against the official SCS Telemetry SDK 1.16 headers. The local vehicle frame is +X right, +Y up, +Z back; the world origin is game-defined. Telemetry placement uses the world frame, while linear and angular velocity use the local frame.
- Perception pixels use a top-down image frame and are not world coordinates. WPF prefers a fresh [gameframe v1](ipc/gameframe.v1.md) image and falls back to desktop capture before publishing [perception v1](ipc/perception.v1.md).
- Steering and angles use counterclockwise as positive. Positive steering turns left; native/input translates this convention to the virtual controller axis. Roll is positive by the right-hand rule around the vehicle's local +Z axis.
- IPC access is limited to the current user SID; remote pipe clients are rejected. The game and Python need compatible privilege levels. Each channel defines its own exact mapping or pipe access rules.
- Any value that cannot be produced or validated is marked unavailable by the channel-specific validity mechanism. Zero is not a substitute for a valid reading.

## Versions and extensions

The version is major.minor, starting at 1.0. A reader rejects an unknown major version. A new optional field increments minor; old readers of the same major must ignore the new field. Removing a field, changing its meaning, unit, offset, type, framing, or required behavior increments major. A minor release cannot make an existing optional field mandatory.

For binary channels, append new fields after the existing layout and increase struct_size or packet_size; never move or reinterpret existing bytes. The v1.0 prefix remains readable. Telemetry 1.x extensions must fit within its 4096-byte mapping; a larger mapping requires a new major. For JSON, add optional members and allow unknown members; a v1 reader accepts any 1.x message it otherwise understands. Unknown UI requests receive an error; unknown events may be ignored only if they have no safety effect. Writers set reserved bytes and flags to zero. Telemetry readers ignore reserved bytes for compatible 1.x extensions; control receivers reject nonzero reserved bytes or unknown flags until a new major version or an explicitly negotiated capability defines them.

To add a field:

1. Define its producer, consumer, name, type, size or JSON type, unit, range, unavailable behavior, and source. For binary data, assign an aligned offset after the current end and update the size and any validity bit.
2. Define its default when an older producer omits it; keep existing fields and semantics unchanged.
3. Increment minor and update the relevant specification and JSON Schema where applicable. State how an older reader skips it.
4. Validate a new producer with an old reader and an old producer with a new reader before release. A change that cannot satisfy both directions needs a new major.

## Safety ownership

Python applies range and rate limits before sending control. native/input independently enforces them and immediately disengages on manual input, malformed commands, or command timeout as specified in [control v1](ipc/control.v1.md). Perception is an untrusted observation channel; its prototype detector results do not make control available. The UI displays status and requests actions; it never runs the control loop.

<!-- Edited by: OpenAI-Codex_Responsible: Source_Code -->
<!-- Edited by: OpenAI-Codex_Source_Code -->
