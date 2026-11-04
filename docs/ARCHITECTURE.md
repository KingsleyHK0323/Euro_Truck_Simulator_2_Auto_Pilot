# Architecture

Status: draft v0.1 — defines module boundaries and the IPC contract before implementation.

## Overview

```mermaid
flowchart LR
    ETS2[ETS2 game] -->|SCS Telemetry SDK| Plugin[native/ C++ plugin DLL]
    Plugin -->|shared memory: telemetry v1| Core[python/ perception & control]
    Plugin -->|shared memory: gameframe v1| UI[ui/ C# .NET 10 WPF]
    UI -->|shared memory: perception v1| Core
    Core <-->|named pipe: control v1 commands / status| Input[native/ input injection]
    Input -->|virtual controller| ETS2
    Core <-->|named pipe: ui v1| UI
```

## Modules
| Module | Language | Responsibilities | Must not |
|---|---|---|---|
| `native/plugin` | C++ | Load as SCS SDK plugin, publish telemetry and best-effort game frames to shared memory | Contain driving logic |
| `native/input` | C++ | Apply steering/throttle/brake commands via virtual controller and return applied-state packets | Decide commands |
| `python/` | Python 3 | Perception, planning, control loop, safety limits | Touch game memory directly |
| `ui/` | C# .NET 10 WPF | Settings, status, start/stop, logs, game-frame bridge and GDI fallback | Run the control loop |
| `shared/` | Spec | Versioned IPC schemas | — |

## IPC Contract
| Channel | Transport | Direction | Notes |
|---|---|---|---|
| [telemetry v1](../shared/ipc/telemetry.v1.md) | Shared memory | plugin → python | 4096-byte mapping, 240-byte v1.0 struct, SI units, version header, sequence counter |
| [gameframe v1](../shared/ipc/gameframe.v1.md) | Shared memory | plugin → ui | Internal D3D11 Present capture; WPF reads only and retains GDI fallback |
| [perception v1](../shared/ipc/perception.v1.md) | Shared memory | ui → python | 43,264-byte mapping with a 64-byte seqlock header and 160 × 90 RGB24 image; WPF bridges gameframe or GDI capture; prototype detections do not authorize control |
| [physical input v1](../shared/ipc/physical-input.v1.md) | Named pipe | ui → native/input | 24-byte Raw Input state frames at 20 Hz; native receive-time expiry fails closed after 250 ms |
| [control v1](../shared/ipc/control.v1.md) | Named pipe | python → input commands; input → python status | Overlapped I/O; 64-byte v1.0 command prefix, v1.1 safety-claim extension, 64-byte v1.0 status prefix and v1.1 capability extension |
| [ui v1](../shared/ipc/ui.v1.md) | Named pipe | python ↔ ui | JSON status with state_revision; v1.3 adds optional timestamped navigation duration while retaining v1.1 perception and v1.2 native-control observations; [JSON Schema](../shared/ipc/ui.v1.schema.json) |

The [shared contract overview](../shared/README.md) defines versioning and common units. Channel specifications define field layout, update behavior, and safety rules. Native D3D11 capture, the GDI fallback, and the lane detector remain development prototypes; front-object detection and physical-input coverage require validation before control is enabled.

## Safety
- Driver override always wins; any manual input disengages autopilot.
- Control commands are rate- and range-limited in Python and again in `native/input`.
- `native/input` independently enforces the command timeout, manual-override latch, and a fixed steering amplitude cap of 0.30. A speed-dependent steering curve is not implemented and is a release blocker until calibrated.

## Cross-cutting
- Platform: Windows 10/11 x64 only.
- i18n: English default; all user-facing strings in resource files.
- CI: GitHub Actions `windows-latest`.
- Whether `native/input` is a separate process or hosted in another native process is [Need decision: ADR pending]; the IPC and peer-identity contract applies to either arrangement.

<!-- Edited by: Anthropic-Claude_Code -->
<!-- Edited by: OpenAI-Codex_Responsible: Source_Code -->
<!-- Edited by: OpenAI-Codex_Source_Code -->
