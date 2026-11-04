# Change Log

All notable changes to this project are documented here and published on the project website.

## [Unreleased]

### Added
- Added Physical Input v1: WPF tracks keyboard and mouse held state per device across focus changes, then sends sequenced Raw Input frames to a native pipe that authenticates the UI process and same-user SID, expires reports after 250 ms, and fails closed on invalid data or disconnect; physical-device coverage remains unvalidated and engagement stays blocked. (OpenAI-Codex_Responsible: Source_Code)
- Added a native keyboard and XInput manual-input monitor on the ControlHost scheduler with an injectable test seam; unknown HID devices and unvalidated device coverage keep the safety gate unhealthy. (OpenAI-Codex; OpenAI-Codex_Responsible: Source_Code)
- Added a Windows GitHub Actions smoke test for connector installation, backup, idempotence, and rollback in a synthetic ETS2 folder. (OpenAI-Codex_Responsible: Source_Code)
- UI IPC v1.2 Control.v1 connection and native state diagnostics in the Overview; v1.0 and v1.1 remain supported, and engagement stays disabled. (OpenAI-Codex_Responsible: Source_Code)
- Connected the Python service to DriverAssistRuntime and the native Control.v1 status pipe when a saved ETS2 executable path is available; engagement remains denied and only neutral/disengaged commands are sent. (OpenAI-Codex_Responsible: Source_Code)
- UI IPC v1.1 perception summary and an Overview readiness panel for frame capture, focus, input monitoring, lane confidence, and front-obstacle status. (OpenAI-Codex_Responsible: Source_Code)
- Added the internal `gameframe.v1` shared-memory contract and a native D3D11 Present capture prototype; WPF prefers its fresh frames and keeps GDI capture as fallback. In-game hook compatibility is unverified. (OpenAI-Codex_Responsible: Source_Code)
- Added a 160 × 90 RGB24 perception mapping and WPF desktop capture/input-monitor publisher. The lane estimator is a prototype; the front-clear detector remains unvalidated and disabled by default. (OpenAI-Codex_Responsible: Source_Code)
- Added a bounded supervised lane-centering and speed-control policy that remains neutral unless telemetry, capture, focus, input monitoring, UI authorization, lane confidence, and a validated front detector all pass. (OpenAI-Codex_Responsible: Source_Code)
- Added Control.v1.1 safety-claim packets and server capability negotiation; the client will not engage with a legacy server, and native engagement stays blocked while its local input-monitor gate is unavailable. (OpenAI-Codex_Responsible: Source_Code)

### Changed
- Native and WPF named-pipe peer-image checks now canonicalize with the normalized NT volume path, which remains available when DOS volume-name lookup is denied; exact image-path and same-user SID checks remain required. (OpenAI-Codex_Responsible: Source_Code)
- The Physical Input pipe retains a current-user-only ACL and rejects remote clients; exact peer image and same-user SID authentication remain required. (OpenAI-Codex_Responsible: Source_Code)
- The native keyboard monitor now treats a zero-only `GetAsyncKeyState` scan as indeterminate and fail-closed; only an observed high-order key-down bit establishes current keyboard activity. (OpenAI-Codex_Responsible: Source_Code)
- Updated the Control.v1 cross-process smoke client to send required v1.1 safety claims so the native test exercises server-side fail-closed gates. (OpenAI-Codex_Responsible: Source_Code)
- Decoupled the neutral Control.v1 heartbeat from status reads and capped it at a 50 ms interval to prevent status updates from driving command traffic. (OpenAI-Codex_Responsible: Source_Code)
- Native UI authorization now fails closed; WPF input-monitor health remains unavailable until physical-device coverage is validated. Corrected Raw Input mouse-button release tracking. (OpenAI-Codex_Responsible: Source_Code)
- Documented the current fixed 0.30 steering cap and marked speed-dependent steering calibration as unfinished. (OpenAI-Codex_Responsible: Source_Code)
- Linked the perception mapping contract from the shared IPC overview and architecture; documented capture, controller-coverage, and detector-validation limits. (OpenAI-Codex_Responsible: Source_Code)
- Set `ETS2AP_TEST_PYTHON` from the configured Python installation for native Control.v1 smoke tests in CI and release builds. (OpenAI-Codex_Responsible: Source_Code)
- The MSI license text conversion now removes triple-star emphasis markers and rejects leftover asterisks. (OpenAI-Codex_Responsible: Source_Code)
- The application source code and software use the PolyForm Noncommercial License 1.0.0; website content remains CC BY-NC-SA 4.0 and website source code uses PolyForm Noncommercial License 1.0.0. The connector ZIP carries the SCS SDK MIT notice. (OpenAI-Codex_Responsible: Source_Code)

## [0.0.1] - 2026-10-25

### Added
- Windows x64 release packaging for the WPF app and native connector, with a versioned connector ZIP, an MSI installer, and a CI install/uninstall smoke test. (OpenAI-Codex_Responsible: Source_Code)
- Setup can download the published connector ZIP from GitHub, verify the release and DLL digests, and install supported DLLs into the selected ETS2 plugins folder with an elevation prompt and backups. (OpenAI-Codex_Responsible: Source_Code)
- One-command Phase 0 in-game steering probe with continuous parked-state checks for simple automatic transmission, signed pulse verification, neutral-output acknowledgement, and offline self-test. (OpenAI-Codex_Responsible: Source_Code)
- Phase 0 probe accepts an explicit output directory and uses fresh telemetry rather than process enumeration to validate game/plugin activity. (OpenAI-Codex_Responsible: Source_Code)
- Development Python UI status service and WPF named-pipe status display with bounded framing, peer-image checks, and offline protocol checks; engagement remains disabled until native control integration. (OpenAI-Codex_Responsible: Source_Code)
- Headless WPF UI checks for localized resources, theme resources and contrast, preferences, and navigation state; CI runs them explicitly. (OpenAI-Codex_Responsible: Source_Code)
- Initial .NET 10 WPF desktop UI preview with Overview, Setup, Settings, and Activity screens; localizable English resources, theme preferences, high contrast support, and keyboard navigation. (OpenAI-Codex_Responsible: Source_Code)
- Refined desktop navigation focus and accessibility labels, and fixed the sidebar footer placement. (OpenAI-Codex_Responsible: Source_Code)
- Repository initialized: `.gitignore`, README, LICENSE (CC BY-NC-SA 4.0), NOTICE. (Anthropic-Claude_Code)
- Development guidelines: `AGENTS.md`, `CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, task issue template, `.gitattributes`. (Anthropic-Claude_Code)
- GitHub Actions: secret scan (gitleaks), CI (C++ / Python / .NET 10, auto-skip until modules exist), dependency license check, Change Log sync to website, release skeleton; Dependabot for Actions. (Anthropic-Claude_Code)
- Versioned v1 IPC specifications for telemetry, control, and UI, including the UI JSON Schema. (OpenAI-Codex_Responsible: Source_Code)
- Dependabot npm updates targeting the website branch; major updates of typescript and astro are ignored. (OpenAI-Codex_Responsible: Source_Code)
- Release notes are built from the matching Change Log.md section with a fixed contributors block, a comparison link, and a preview-only manual run; extraction and validation scripts with local tests; Actionlint workflow on Source_Code. (OpenAI-Codex_Responsible: Source_Code)
- Phase 0 feasibility spike for the SCS SDK Input API (test plugin with scripted input injection, telemetry and timing logs, and an offline host harness) and the vendored SCS SDK 1.16 headers (MIT) in `third_party/scs_sdk`. (OpenAI-Codex_Responsible: Source_Code)
- Native telemetry writer: `telemetry_core` library (layout, conversions, validity groups, moving_backward hysteresis, seqlock, Windows shared-memory owner), the SCS SDK plugin glue exporting `scs_telemetry_init` and `scs_telemetry_shutdown`, a minimal in-repo test framework, and writer-to-reader round-trip tests. (OpenAI-Codex_Responsible: Source_Code)

### Changed
- Create a per-machine advertised Start Menu shortcut from the installed UI executable, excluding it from wildcard harvesting to avoid duplicate file components. (OpenAI-Codex_Responsible: Source_Code)
- Fixed PowerShell interpolation of non-ASCII license characters so release packaging succeeds under StrictMode. (OpenAI-Codex_Responsible: Source_Code)
- License Check now audits .NET packages and uses exact-version MS-RL overrides for the two approved CI-only WiX packages whose NuGet metadata lacks SPDX expressions. (OpenAI-Codex_Responsible: Source_Code)
- Connector installation locks the selected local directory chain, rejects reparse points, verifies persistent backups, and restores through a fresh temporary file. (OpenAI-Codex_Responsible: Source_Code)
- Enabled Python CI for the existing package and tests without installing unused development dependencies. (OpenAI-Codex_Responsible: Source_Code)
- Hardened the Phase 0 Spike by retaining semantic input descriptors for the device lifetime and logging CSV write failures before disabling the affected stream. (OpenAI-Codex_Responsible: Source_Code)
- Changed the Phase 0 Spike default to the SCS semantical input device, which maps configured game mixes without a player binding UI; mix support still requires verification per ETS2 profile. (OpenAI-Codex_Responsible: Source_Code)
- Restored the generic `ETS2AP Spike` device alongside the no-bind semantic device by default, so existing ETS2 profiles recognize the assigned device while semantic input remains available. (OpenAI-Codex_Responsible: Source_Code)
- Diagnosed the Phase 0 in-game generic steering probe: axis binding is required, and the tested profile maps positive Spike steering to negative game steering. The current source reverses the generic virtual-axis value, identifies polarity mismatches, and steps the ramp to limit the observed callback surge; these fixes still require game verification. (OpenAI-Codex_Responsible: Source_Code)
- Routed Phase 0 semantical steering, forward, and backward float inputs to their corresponding scripted values and recorded actual steering-event emission; game-side mix names remain unverified. (OpenAI-Codex_Responsible: Source_Code)
- Retained the SDK log callback by value so telemetry callbacks do not access initialization parameters after init. (OpenAI-Codex_Responsible: Source_Code)
- Agent signature in `AGENTS.md` set to `OpenAI-Codex_Responsible: Source_Code`; reporting format added. (Anthropic-Claude_Code)
- Linked the architecture IPC table to the shared contract specifications. (OpenAI-Codex_Responsible: Source_Code)
- Applied verified SDK channel names and coordinate conventions to the telemetry contract; steering is positive left. (OpenAI-Codex_Responsible: Source_Code)
- Hardened IPC specifications for seqlock ordering, 4096-byte mapping ownership and latest-callback validity, writer restart, overlapped pipe I/O and peer validation, control status state machine, UI parsing and state_revision, and moving_backward hysteresis. (OpenAI-Codex_Responsible: Source_Code)
- Removed MPL-2.0 from the dependency license-check allow-list. (OpenAI-Codex_Responsible: Source_Code)
- Release and Sync Docs workflows use least-privilege permissions; Change Log.md is validated before it is synced to the website; releases are restricted to tags on Source_Code; `generate_release_notes` is no longer used. (OpenAI-Codex_Responsible: Source_Code)
- CI module detection no longer uses `ls | grep` and writes its outputs in one block, which fixes the Actionlint shellcheck findings. (OpenAI-Codex_Responsible: Source_Code)
- Release packages are now the connector ZIP and the MSI installer; the additional installer wrapper was removed because its failures could not be diagnosed. (OpenAI-Codex_Responsible: Source_Code)

## [0.0.3] - 2026-10-25

### Added
- The WPF app starts the bundled Python UI status service from the installed runtime, terminates it on app exit, and allows one automatic restart after an unexpected process exit. The MSI now includes the Python modules and validates their embedded-runtime import and install paths; Control.v1 engagement remains unavailable. (OpenAI-Codex_Responsible: Source_Code)
- The production SCS plugin now registers a semantic steering/forward/brake input device without requiring player-side axis binding; native output starts neutral and fails closed on invalid, disengaged, or stale commands. ETS2 profile mix support remains **[VERIFY]**. (OpenAI-Codex_Responsible: Source_Code)
- Added a native Control.v1 core for packet validation, status encoding, 100 ms watchdog expiry logic, neutral re-arm, manual-override latching, safety gates, brake priority, and slew limits. The host must schedule `Tick` independently of pipe I/O; braking cuts applied throttle immediately, throttle stays zero until applied brake returns to zero, and a reduced steering cap immediately bounds output. Command packet transport is implemented, but end-to-end engagement and game-control integration remain unavailable. (OpenAI-Codex_Responsible: Source_Code)
- Added a native plugin host that runs the Control.v1 watchdog every 10 ms independently of command input, publishes only engaged snapshots to the SCS semantic input device, and keeps every non-engaged state neutral. The production manual-input detector remains unconnected. (OpenAI-Codex_Responsible: Source_Code)
- Added the native Control.v1 named-pipe server and Python client: current-user DACL, remote-client rejection, canonical peer image and SID validation, overlapped transport, separate command/status I/O, neutral reconnect handshake, strict status validation, and a 100 ms status-frame deadline. Engage remains denied until independent telemetry, speed-limit, UI-authorization, and physical-input safety gates are wired. (OpenAI-Codex_Responsible: Source_Code)
- Bundled the pinned CPython 3.13.17 x64 embeddable runtime and its license in the MSI, and registered the per-machine install root used by the native Control.v1 peer check. The WPF app launches the status-only Python service; the Control.v1 client is not connected to the UI authorization or safety-gate workflow. (OpenAI-Codex_Responsible: Source_Code)

### Changed
- Licensed the application source code and software under PolyForm Noncommercial License 1.0.0. The MSI includes the full license and NOTICE; the connector manifest identifies the license and copyright, and the ZIP carries the SCS SDK MIT notice. Previous release tags retain their existing license files. (OpenAI-Codex_Responsible: Source_Code)
- Formatted the MSI license agreement as plain text and reserved Control.v1 command sequence numbers before writing to prevent acknowledgements from racing ahead of the sender state. (OpenAI-Codex_Responsible: Source_Code)
- Updated native and Python documentation to describe the new Control.v1 transport, bundled runtime path, and remaining service-launch and safety-gate limitations. (OpenAI-Codex_Responsible: Source_Code)
- Release packaging uses a version-specific WiX intermediate directory so stale MSI build state cannot suppress output for a new version. (OpenAI-Codex_Responsible: Source_Code)
- This release is an installation and telemetry integration preview. Automatic driving remains disabled until the production safety gates and Python Control.v1 client are connected; semantic input mix support still requires in-game verification. (OpenAI-Codex_Responsible: Source_Code)

## [0.0.2] - 2026-10-25

### Changed
- Clarified live game telemetry, connector, control-service, and ETA availability in the WPF UI; added session-only status activity, screen-reader notifications, copyable diagnostic summaries, and connector installation progress feedback. (OpenAI-Codex_Responsible: Source_Code)

<!-- Edited by: OpenAI-Codex_Responsible: Source_Code -->
