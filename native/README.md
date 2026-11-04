<!-- Edited by: OpenAI-Codex (OpenAI-Codex_Responsible: Source_Code) -->
# Native modules

The keyboard sampler treats an all-zero `GetAsyncKeyState` scan as indeterminate because Windows also returns zero for some call failures. Only a high-order key-down bit proves an observable keyboard state; the low-order recently-pressed bit is ignored as unreliable.

This directory contains the Windows x64 telemetry writer, production SCS semantic input-device registration, and a best-effort D3D11 game-frame publisher. `telemetry_core` owns the telemetry IPC layout, conversions, validity groups, seqlock, and shared mapping. `plugin` connects SCS Telemetry SDK callbacks to that core and builds `ets2ap_plugin.dll`; its semantic input device exposes `steering`, `aforward`, and `abackward` mixes without a player-side binding screen. The Present capture prototype publishes a separate read-only-for-WPF `gameframe.v1` mapping; see [its contract](../shared/ipc/gameframe.v1.md) for capture limits and fallback behavior.

`input_core` validates Control.v1 command packets and owns the safe command state machine, 100 ms watchdog expiry logic, neutral re-arm, manual-override latch, output slew limits, and status-packet encoding. The native plugin's `ControlHost` calls `Tick` on an independent 10 ms scheduler and forwards only an engaged, safe snapshot to the semantic input device; all other states are explicitly published as neutral. A braking command cuts applied throttle immediately, throttle stays zero while applied brake remains nonzero, and a lowered steering cap is enforced immediately even when either safety bound exceeds the normal slew rate.

`ControlPipeServer` accepts one local Python client over `\\.\pipe\ETS2AP_Control_v1`, using a current-user DACL, remote-client rejection, first-instance ownership, overlapped I/O, canonical executable-path and SID checks. A dedicated status writer sends state changes and 20 Hz v1.1 capability snapshots with a 20 ms write deadline while the command reader and watchdog run independently. The native core requires both safety claims and local safety gates; telemetry freshness and speed-limit availability are connected. The native input monitor polls XInput and device inventory on the independent 10 ms scheduler and receives WPF Raw Input state over [Physical Input v1](../shared/ipc/physical-input.v1.md). The separate pipe authenticates the installed `ETS2AP.Ui.exe`, accepts 50 ms sequenced reports, and expires them after 250 ms; probe failures, stale reports, disconnects, unclassified HID devices, and unvalidated device coverage keep the monitor fail-closed. Losing monitor health after it was healthy latches a neutral manual override. Physical-device coverage remains unvalidated and local UI authorization remains false, so engagement stays blocked. The MSI bundles the CPython 3.13 x64 embeddable runtime under `runtime\` and registers its install root in `HKLM\SOFTWARE\Kingsley\ETS2 Auto Pilot\InstallLocation`. The WPF app starts the Python service and the physical-input client; automatic driving remains unavailable.

The production input device fails closed to neutral. It accepts only finite, in-range commands stamped by the same monotonic clock and returns neutral when a command is disengaged or older than 100 ms. Its independent steering amplitude cap is fixed at 0.30; a speed-dependent curve remains unimplemented and must be calibrated before release. The Python Control.v1 client verifies the native process image and user SID, starts each connection with a neutral sequence-1 handshake, validates status frames, and fails closed on incomplete frames or protocol errors. Native telemetry freshness and speed-limit availability are connected, but local UI authorization remains false. The physical-input monitor reports unhealthy until keyboard/XInput/device coverage is validated; unclassified HID devices also keep it unhealthy. Therefore engage remains denied and this transport does not enable automatic driving. Support for the semantic mix names depends on the player's ETS2 controls profile and remains **[VERIFY]** in game. The WPF Engage action remains disabled.

An isolated helper in `input_core/include/ets2ap/input/steering_curve.h` validates explicitly supplied speed/cap points and interpolates only within their range. It contains no vehicle calibration and is not connected to production control, telemetry, safety gates, or the native ABI. Invalid curves, speeds, arithmetic results, or steering requests return no value. Any future integration must treat an absent result as blocked/neutral and enforce the stricter of the curve result and the existing native 0.30 hard cap; the helper does not replace that cap. Test values are synthetic, not calibration evidence.
This generic mathematical helper can return values above 0.30 and is not itself an independent safety cap. Any future control integration must use the lower of its result and the independently approved native hard cap in force at that time, fail closed for invalid or unknown results, and undergo separate ETS2 in-game validation and safety review.
The caller must provide a valid, stable span and independently establish that the speed source is trusted and fresh; the helper does not inspect telemetry timestamps.

The wire contract is [telemetry.v1.md](../shared/ipc/telemetry.v1.md). The vendored SDK headers are under `../third_party/scs_sdk/include`; they are not modified by this build.

## Build and test

In a Visual Studio x64 developer environment:

```powershell
cmake -S native -B build/native -G "Visual Studio 17 2022" -A x64
cmake --build build/native --config Release
ctest --test-dir build/native -C Release --output-on-failure
```

Use the corresponding Visual Studio 18 generator for VS 2026. To check Debug, build with `--config Debug` and run CTest with `-C Debug`. The top-level CMake file uses the static MSVC runtime.

## Test framework

`ETS2AP_TEST(Name)` registers a test. Use `CHECK`, `CHECK_EQ`, `CHECK_NE`, and `CHECK_NEAR` for nonfatal assertions. `ETS2AP_TEST_MAIN()` supplies the runner. Test executables accept `--list` and `--filter=<substring>`; the final line reports tests, checks, and failures. A run fails when an assertion or test throws, or when no tests or checks run. CTest runs the framework selftest, telemetry core tests, mapping tests, plugin tests, and Control.v1 input-core tests separately.

`steering_curve_compile_tests.cpp` is compiled as part of `input_core_tests`; its `static_assert` checks run at compile time and do not establish runtime or in-game behavior.

<!-- Edited by: OpenAI-Codex_Source_Code -->

<!-- Edited by: OpenAI-Codex_Responsible: Source_Code -->
