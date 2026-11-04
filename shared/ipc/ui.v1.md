# UI IPC v1

Status: v1.3 draft, compatible with v1.2, v1.1, and v1.0. Python creates \\.\pipe\ETS2AP_UI_v1 as a single-client Windows byte-stream pipe; the WPF UI connects as client. Both directions use the same framing and [draft 2020-12 JSON Schema](ui.v1.schema.json). The UI requests actions and presents localized status; Python owns the control state. Pipe creation, current-user DACL, remote-client rejection, and peer-image verification follow [control pipe access](control.v1.md#pipe-access-and-peer-identity). Python accepts only the configured UI executable's canonical full path and current-user SID, and rejects a second concurrent connection. The UI verifies the configured Python server executable and SID. Any unverifiable identity closes the connection.

## Frame and envelope

Each frame is a 4-byte unsigned little-endian payload length followed by exactly that many bytes of UTF-8 JSON, without a BOM. The JSON root is one object. Length must be 1–65,536 bytes. One frame carries exactly one message. This limit includes JSON bytes but not the 4-byte prefix. The complete frame and JSON parse SHALL finish within 1 s of receiving its first byte. Maximum nesting depth is 16 and maximum members per object is 64. Reject duplicate object keys and nonfinite JSON numbers, including overflow such as `1e999`; NaN and Infinity tokens are invalid. A bounded parser enforces these limits before application dispatch.

The receiver first validates the generic envelope against `$defs/base` (`type`, `version`, `id`, `timestamp`, `payload`), then checks the major version, then validates the known type's payload. The whole schema is not the version detector: a 2.0 envelope can fail a known payload rule, but it must receive UNSUPPORTED_VERSION after envelope validation. Invalid length, invalid UTF-8 or JSON, incomplete-frame or parse timeout, duplicate key, nonfinite number, depth/member limit violation, or an unrecoverable envelope error closes the connection. A well-framed, decodable request with unknown type or invalid type-specific payload receives MALFORMED_MESSAGE and stays connected. An unknown event from the peer may be ignored unless it affects a safety decision; unknown events never authorize engagement.

Every message has:

| Member | Type | Meaning |
|---|---|---|
| type | string | Message type; the envelope permits unknown types so application dispatch can handle them |
| version | string | major.minor; v1.0 is the original status shape, v1.1 adds an optional perception summary, and v1.2 adds an optional native Control.v1 summary, and v1.3 adds an optional navigation-duration snapshot; accept compatible 1.x and ignore unknown optional members |
| id | string | 1–64 ASCII letters, digits, dots, underscores, or hyphens; UI requests start with u-, Python-initiated events start with p-; unique for each new request/event within one connection |
| timestamp | string | Base-10 uint64 QPC nanoseconds, with no sign or leading zero; same-boot monotonic time, never a wall-clock date |
| payload | object | Type-specific data; empty object for a message without fields |

A response uses the request's id, including its u- prefix; Python-initiated status, config, log, and error events use new p- ids. The recipient does not infer ordering from id; it uses message order and timestamp. Unknown optional members of a known 1.x type are ignored. A UI request with an unknown type receives error MALFORMED_MESSAGE; the UI ignores an unknown Python event only if no safety decision depends on it.

## Handshake and flow

1. On connection, the UI sends hello as its first frame with supported_versions containing 1.3, 1.2, 1.1, and 1.0, in preference order. Python replies with hello_ack, choosing the highest supported shared version. If no major version is shared, Python sends error with UNSUPPORTED_VERSION if the envelope can be decoded, then closes.
2. Python sends a config snapshot and status after hello_ack. The UI may request another status with get_status. Python also sends status whenever the state or summarized telemetry changes; it may coalesce high-frequency telemetry.
3. UI set_config requests an update. Python validates it and returns config with the same id containing the applied snapshot, or error with the same id. An engage request includes the `state_revision` from the latest status observed by the UI. Python evaluates the revision match, state freshness, and all control safety gates in one atomic decision. State evidence must be no older than 500 ms **[TUNE]**; stricter telemetry and native-status freshness rules still apply. A mismatch or stale state returns STALE_STATUS; a failed safety gate returns its corresponding error. UI engage/disengage requests receive status with the same id on acceptance or error on rejection. A UI request never directly controls native/input.
4. On pipe disconnection, the UI marks state offline and reconnects. Python's safety behavior follows [control v1](control.v1.md); a reconnect starts a new hello and does not imply re-engagement. The UI must wait for fresh status before presenting an engaged state.

## Message types

| Direction | type | Required payload | Response / behavior |
|---|---|---|---|
| UI → Python | hello | supported_versions: nonempty array of major.minor strings | hello_ack or error |
| UI → Python | get_status | empty object | status with matching id |
| UI → Python | set_config | config: object with target_speed_mps | config or error with matching id |
| UI → Python | engage | state_revision: latest observed nonnegative revision | status or error with matching id |
| UI → Python | disengage | empty object | status with matching id |
| Python → UI | hello_ack | selected_version: major.minor string | Completes handshake |
| Python → UI | status | state, error_code, state_revision, telemetry; optional perception in v1.1+, control in v1.2+, and navigation duration in v1.3+ | Current state, telemetry, and safety observations |
| Python → UI | config | config: object with target_speed_mps | Applied configuration |
| Python → UI | log | level, message_key, args | Localized diagnostic event |
| Python → UI | error | code | Rejected request or asynchronous error |

Python status.state is one of ready, engaged, manual_override, or fault. `offline` is local UI state after pipe loss and SHALL NOT be sent in a Python status message. status.error_code is null when there is no active error, otherwise one of the codes below. status.state_revision is a nonnegative integer, starting at zero for a Python process and incrementing only when state or error_code changes; a telemetry-summary-only update keeps the same revision. telemetry contains speed_mps, speed_limit_mps, navigation_distance_m, and telemetry_age_s. The first three are nonnegative numbers or null when their corresponding validity bit is clear. telemetry_age_s is the age in seconds of the latest valid telemetry snapshot, or null if no valid snapshot has ever been received. The summary is informational and never overrides control safety checks.

In v1.3, telemetry may additionally contain `navigation_time_s` and `navigation_time_sample_timestamp_ns`. The duration is a finite nonnegative number of seconds from telemetry.v1 validity bit 11; zero is valid. The timestamp is the original nonzero telemetry-snapshot QPC nanosecond count encoded as a decimal string, not the UI message timestamp. Both values may be null or absent; either missing or invalid value makes the duration unavailable. The producer checks snapshot freshness when constructing each status and sends nulls if the snapshot is invalid, from the future, or more than 250 ms old. A snapshot can expire during subsequent scheduling or transport, so the receiver must independently check it when displaying. The UI requires the sample timestamp to be no later than its message timestamp and the message timestamp to be no later than the current same-boot monotonic clock; it accepts a sample age of exactly 250 ms but not more. This check does not establish ordering between separate status messages. Versions 1.0-1.2 omit these members and newer consumers display them as unavailable.

For a v1.3 connection, the server waits up to 50 ms for the first bytes of a request and spaces unsolicited status events by at least 50 ms after the previous event send completes. It does not throttle direct request responses, including disengage. Versions 1.0-1.2 retain the prior 250 ms idle wait. Frame processing, pipe I/O, and UI scheduling can take longer.
The desktop shows the navigation duration, not a wall-clock arrival time, as total hours, minutes, and whole seconds (`H:MM:SS`); fractional seconds are truncated and hours may exceed 24. It does not count down locally. A localized result longer than 32 UTF-16 characters is shown as unavailable without imposing a game-value limit. The UI rechecks the currently displayed value on a 50 ms dispatcher timer and clears it on disconnect; actual clearing can be delayed by the timer interval or a blocked UI dispatcher, so this is not a hard real-time guarantee. The timestamp measures publication of the whole telemetry snapshot and does not prove independent freshness of the navigation channel.
In v1.1 and later, status may include a `perception` object with frame_valid, frame_age_s, game_focused, input_monitor_healthy, manual_input_active, lane_confidence, and front_clear. The booleans report WPF observations; age is nonnegative seconds or null; lane_confidence is in [0,1] or null; front_clear is true, false, or null. `input_monitor_healthy` must remain false until physical-device coverage is validated; Raw Input/XInput API availability alone is insufficient. `front_clear: null` means the obstacle detector is not validated. These fields are diagnostics only and never authorize engagement. v1.0 peers omit the perception member.

v1.2 and later status may include a `control` object containing `connection`, `state`, and `reason`. `connection` is `not_configured` when no trusted ETS2 executable path is available, `disconnected` when no fresh native status is available, or `connected` when a fresh Control.v1 status was received. Connected objects include the native state (`disengaged`, `engaged`, `manual_override_latched`, `timeout`, or `rejected`) and reason (`none`, `manual_input`, `command_timeout`, `malformed_packet`, `version_mismatch`, `client_disconnected`, or `telemetry_unavailable`). Non-connected objects set state and reason to null. This is a diagnostic snapshot only: the UI labels it as native-reported state, it does not imply app authorization, and it never enables engagement. Control output values are not sent to the UI. v1.0 and v1.1 peers omit the control member.

## State and error mapping

Python uses [native/input status packets](control.v1.md#status-packet-nativeinput-to-python) as the source of native state. It does not infer manual takeover from SDK `truck.input.*` alone.

| Source | UI status.state | UI status.error_code |
|---|---|---|
| Control (disengaged, none) | ready | null |
| Control state engaged | engaged | null |
| Control (disengaged, manual_input): physical input reported, not latched | manual_override | MANUAL_OVERRIDE |
| Control (manual_override_latched, manual_input or first-reconnect client_disconnected) | manual_override | MANUAL_OVERRIDE |
| Control state timeout / reason command_timeout | fault | COMMAND_TIMEOUT |
| Control state rejected / reason malformed_packet or version_mismatch | fault | CONTROL_UNAVAILABLE |
| Control (disengaged, client_disconnected) or unverified peer | fault | CONTROL_UNAVAILABLE |
| Control reason telemetry_unavailable | fault | TELEMETRY_UNAVAILABLE (reserved; v1.0 native/input does not emit this reason) |
| Python telemetry missing, invalid, or stale | fault | TELEMETRY_UNAVAILABLE or TELEMETRY_STALE |
| UI pipe disconnected (local UI inference) | offline, local only | no Python status available |

The safety state wins over an older status packet. Python treats absent native status or status older than 250 ms **[TUNE]** as control unavailable and will not show engaged. A control timeout is specifically COMMAND_TIMEOUT. MALFORMED_MESSAGE is for a well-framed request with a missing field, wrong type, or unknown request type; invalid JSON or framing closes the connection.

config.target_speed_mps is a number whose application range is 0–40 m/s **[TUNE]**. The schema checks its numeric type; Python applies the range and returns INVALID_CONFIG for an out-of-range value. Python must also cap the effective speed according to valid speed-limit and safety policy; the UI value is only a requested ceiling. A set_config request replaces this field atomically. Future 1.x config fields may be added as optional members. The current schema requires target_speed_mps in a complete config snapshot and set_config request.

log.message_key is an i18n key, and log.args is an object of scalar substitution values. Each value's string representation is at most 256 characters. Values SHALL NOT contain file paths, user names, or raw exception text; producers redact or replace them with safe diagnostic codes before serialization. The UI obtains display text from its resources. No protocol field carries hard-coded English display text. The schema bounds string args but does not enforce lengths of non-string scalar representations, id uniqueness, uint64 overflow, request/response matching, monotonic order, or allowed state transitions; implementations must handle these separately. The current desktop navigation display does not enforce timestamp ordering between separate status messages.

## Error codes

The i18n key is the suggested WPF resource identifier, not display text. A Python producer emits the code; the UI chooses localized text.

| Code | Meaning | Suggested i18n key |
|---|---|---|
| GAME_NOT_RUNNING | ETS2 process is unavailable | error.game_not_running |
| TELEMETRY_UNAVAILABLE | Mapping or required telemetry is unavailable | error.telemetry_unavailable |
| TELEMETRY_STALE | Required telemetry exceeded its freshness limit | error.telemetry_stale |
| CONTROL_UNAVAILABLE | Native input pipe or device is unavailable | error.control_unavailable |
| MANUAL_OVERRIDE | Driver input took priority | error.manual_override |
| COMMAND_TIMEOUT | No fresh valid control command within 100 ms | error.command_timeout |
| INVALID_CONFIG | Configuration value is outside its application range | error.invalid_config |
| UNSUPPORTED_VERSION | No compatible protocol major version | error.unsupported_version |
| MALFORMED_MESSAGE | Decodable request has an invalid shape or unknown type | error.malformed_message |
| STALE_STATUS | Engage revision does not match or state evidence is stale | error.stale_status |
| INTERNAL_ERROR | Unclassified application failure | error.internal_error |

When a request fails, error.id matches that request. For a framing error, the peer may be unable to send an error and closes the pipe. Error codes do not themselves authorize engagement. The UI's display resources must provide localized text for every code and log key.

<!-- Edited by: OpenAI-Codex_Responsible: Source_Code -->
<!-- Edited by: OpenAI-Codex_Source_Code -->
