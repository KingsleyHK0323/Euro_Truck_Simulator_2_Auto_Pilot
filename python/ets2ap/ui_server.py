"""Development UI status server for the Windows UI v1 named pipe.

Edited by: OpenAI-Codex_Responsible: Source_Code
Edited by: OpenAI-Codex_Source_Code
"""

from __future__ import annotations

import argparse
import ctypes
import math
import msvcrt
import os
import struct
import sys
import threading
import time
from ctypes import wintypes

from .control_client import ControlClient, ControlClientError, MAX_STATUS_AGE_NS
from .control_protocol import ControlReason, ControlState, SafetyFlags, ControlStatus
from .driver_runtime import DriverAssistRuntime
from .telemetry import TelemetryReader, TelemetrySummary
from .perception_protocol import MAX_FRAME_AGE_NS, PerceptionFrame
from .perception import PerceptionReader
from .ui_protocol import MAX_FRAME, SUPPORTED_VERSIONS, VERSION, frame, message, parse

PIPE_NAME = r"\\.\pipe\ETS2AP_UI_v1"
ERROR_IO_PENDING = 997
ERROR_PIPE_CONNECTED = 535
ERROR_BROKEN_PIPE = 109
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258
PIPE_ACCESS_DUPLEX = 0x00000003
FILE_FLAG_OVERLAPPED = 0x40000000
FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000
PIPE_TYPE_BYTE = 0
PIPE_READMODE_BYTE = 0
PIPE_WAIT = 0
PIPE_REJECT_REMOTE_CLIENTS = 0x00000008
TOKEN_QUERY = 0x0008
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
CONTROL_NEUTRAL_HEARTBEAT_INTERVAL_S = 0.05
MAX_NAVIGATION_AGE_NS = 250_000_000
UI_V13_STATUS_INTERVAL_NS = 50_000_000


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_void_p), ("InternalHigh", ctypes.c_void_p),
                ("Offset", wintypes.DWORD), ("OffsetHigh", wintypes.DWORD),
                ("hEvent", ctypes.c_void_p)]


class SECURITY_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("nLength", wintypes.DWORD), ("lpSecurityDescriptor", ctypes.c_void_p),
                ("bInheritHandle", wintypes.BOOL)]


class TOKEN_USER(ctypes.Structure):
    _fields_ = [("Sid", ctypes.c_void_p), ("Attributes", wintypes.DWORD)]


class WinPipe:
    def __init__(self, trusted_ui_image: str):
        if sys.platform != "win32":
            raise OSError("Windows is required")
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.advapi = ctypes.WinDLL("advapi32", use_last_error=True)
        self._configure()
        self.trusted_ui_image = self._canonical_image(trusted_ui_image)
        self.user_sid = self._process_sid(self.kernel.GetCurrentProcess())
        descriptor = ctypes.c_void_p()
        sddl = f"D:P(A;;GA;;;{self.user_sid})"
        if not self.advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(descriptor), None):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            security = SECURITY_ATTRIBUTES(ctypes.sizeof(SECURITY_ATTRIBUTES), descriptor, False)
            self.handle = self.kernel.CreateNamedPipeW(
                PIPE_NAME, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1, MAX_FRAME + 4, MAX_FRAME + 4, 0, ctypes.byref(security))
            if self.handle == ctypes.c_void_p(-1).value:
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            self.kernel.LocalFree(descriptor)

    def _configure(self) -> None:
        k, a = self.kernel, self.advapi
        for name, args, result in (
            ("CreateNamedPipeW", [ctypes.c_wchar_p, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p], ctypes.c_void_p),
            ("CreateEventW", [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, ctypes.c_wchar_p], ctypes.c_void_p),
            ("ConnectNamedPipe", [ctypes.c_void_p, ctypes.POINTER(OVERLAPPED)], wintypes.BOOL),
            ("ReadFile", [ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p,
                          ctypes.POINTER(OVERLAPPED)], wintypes.BOOL),
            ("WriteFile", [ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p,
                           ctypes.POINTER(OVERLAPPED)], wintypes.BOOL),
            ("GetOverlappedResult", [ctypes.c_void_p, ctypes.POINTER(OVERLAPPED), ctypes.POINTER(wintypes.DWORD),
                                     wintypes.BOOL], wintypes.BOOL),
            ("CancelIoEx", [ctypes.c_void_p, ctypes.POINTER(OVERLAPPED)], wintypes.BOOL),
            ("GetNamedPipeClientProcessId", [ctypes.c_void_p, ctypes.POINTER(wintypes.ULONG)], wintypes.BOOL),
            ("OpenProcess", [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], ctypes.c_void_p),
            ("GetCurrentProcess", [], ctypes.c_void_p),
            ("QueryFullProcessImageNameW", [ctypes.c_void_p, wintypes.DWORD, ctypes.c_wchar_p,
                                             ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            ("GetFinalPathNameByHandleW", [ctypes.c_void_p, ctypes.c_wchar_p, wintypes.DWORD,
                                             wintypes.DWORD], wintypes.DWORD),
            ("CloseHandle", [ctypes.c_void_p], wintypes.BOOL),
            ("DisconnectNamedPipe", [ctypes.c_void_p], wintypes.BOOL),
            ("WaitForSingleObject", [ctypes.c_void_p, wintypes.DWORD], wintypes.DWORD),
        ):
            function = getattr(k, name)
            function.argtypes, function.restype = args, result
        a.GetTokenInformation.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p,
                                         wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
        a.GetTokenInformation.restype = wintypes.BOOL
        a.OpenProcessToken.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p)]
        a.OpenProcessToken.restype = wintypes.BOOL
        a.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
        a.ConvertSidToStringSidW.restype = wintypes.BOOL
        a.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD,
                                                                            ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p]
        a.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL
        k.LocalFree.argtypes = [ctypes.c_void_p]

    def _canonical_image(self, path: str) -> str:
        if not os.path.isfile(path):
            raise ValueError("trusted executable does not exist")
        with open(path, "rb") as source:
            buffer = ctypes.create_unicode_buffer(32768)
            handle = msvcrt.get_osfhandle(source.fileno())
            length = self.kernel.GetFinalPathNameByHandleW(handle, buffer, len(buffer), 0)
            if not length or length >= len(buffer):
                raise ctypes.WinError(ctypes.get_last_error())
            return buffer.value.upper()

    def _process_sid(self, process: int) -> str:
        token = ctypes.c_void_p()
        if not self.advapi.OpenProcessToken(process, TOKEN_QUERY, ctypes.byref(token)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            needed = wintypes.DWORD()
            self.advapi.GetTokenInformation(token, 1, None, 0, ctypes.byref(needed))
            buffer = ctypes.create_string_buffer(needed.value)
            if not self.advapi.GetTokenInformation(token, 1, buffer, needed, ctypes.byref(needed)):
                raise ctypes.WinError(ctypes.get_last_error())
            sid = TOKEN_USER.from_buffer(buffer).Sid
            text = ctypes.c_void_p()
            if not self.advapi.ConvertSidToStringSidW(sid, ctypes.byref(text)):
                raise ctypes.WinError(ctypes.get_last_error())
            try:
                return ctypes.wstring_at(text)
            finally:
                self.kernel.LocalFree(text)
        finally:
            self.kernel.CloseHandle(token)

    def connect(self) -> None:
        event = self.kernel.CreateEventW(None, True, False, None)
        if not event:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            operation = OVERLAPPED(hEvent=event)
            if not self.kernel.ConnectNamedPipe(self.handle, ctypes.byref(operation)):
                error = ctypes.get_last_error()
                if error == ERROR_PIPE_CONNECTED:
                    return
                if error != ERROR_IO_PENDING:
                    raise ctypes.WinError(error)
                self._finish(operation, 0xFFFFFFFF)
        finally:
            self.kernel.CloseHandle(event)

    def verify_client(self) -> bool:
        pid = wintypes.ULONG()
        if not self.kernel.GetNamedPipeClientProcessId(self.handle, ctypes.byref(pid)) or not pid.value:
            return False
        process = self.kernel.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
        if not process:
            return False
        try:
            buffer = ctypes.create_unicode_buffer(32768)
            size = wintypes.DWORD(len(buffer))
            if not self.kernel.QueryFullProcessImageNameW(process, 0, buffer, ctypes.byref(size)):
                return False
            path = self._canonical_image(buffer.value)
            return path == self.trusted_ui_image and self._process_sid(process) == self.user_sid
        except (OSError, ValueError):
            return False
        finally:
            self.kernel.CloseHandle(process)

    def _finish(self, operation: OVERLAPPED, timeout_ms: int) -> int:
        result = self.kernel.WaitForSingleObject(operation.hEvent, timeout_ms)
        if result != WAIT_OBJECT_0:
            self.kernel.CancelIoEx(self.handle, ctypes.byref(operation))
            transferred = wintypes.DWORD()
            completed = self.kernel.GetOverlappedResult(
                self.handle, ctypes.byref(operation), ctypes.byref(transferred), True)
            if completed:
                return transferred.value
            if result == WAIT_TIMEOUT:
                raise TimeoutError("pipe I/O timeout")
            raise ctypes.WinError(ctypes.get_last_error())
        transferred = wintypes.DWORD()
        if not self.kernel.GetOverlappedResult(self.handle, ctypes.byref(operation), ctypes.byref(transferred), False):
            raise ctypes.WinError(ctypes.get_last_error())
        return transferred.value

    def _io(self, data: bytes | int, *, write: bool, timeout_ms: int) -> bytes | int:
        event = self.kernel.CreateEventW(None, True, False, None)
        if not event:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            operation = OVERLAPPED(hEvent=event)
            buffer = ctypes.create_string_buffer(data if isinstance(data, bytes) else data)
            length = len(data) if isinstance(data, bytes) else data
            action = self.kernel.WriteFile if write else self.kernel.ReadFile
            immediate = wintypes.DWORD()
            if action(self.handle, buffer, length, ctypes.byref(immediate), ctypes.byref(operation)):
                count = immediate.value
            elif ctypes.get_last_error() == ERROR_IO_PENDING:
                count = self._finish(operation, timeout_ms)
            else:
                raise ctypes.WinError(ctypes.get_last_error())
            return count if write else buffer.raw[:count]
        finally:
            self.kernel.CloseHandle(event)

    def read_frame(self, idle_ms: int = 500) -> dict | None:
        try:
            first = self._io(4, write=False, timeout_ms=idle_ms)
        except TimeoutError:
            return None
        if not first:
            raise EOFError
        started = time.monotonic()
        prefix = bytearray(first)
        while len(prefix) < 4:
            remaining = int(1000 - (time.monotonic() - started) * 1000)
            if remaining <= 0:
                raise TimeoutError("frame timeout")
            chunk = self._io(4 - len(prefix), write=False, timeout_ms=remaining)
            if not chunk:
                raise EOFError
            prefix.extend(chunk)
        length = struct.unpack("<I", prefix)[0]
        if not 1 <= length <= MAX_FRAME:
            raise ValueError("invalid frame length")
        payload = bytearray()
        while len(payload) < length:
            remaining = max(1, int(1000 - (time.monotonic() - started) * 1000))
            if time.monotonic() - started >= 1:
                raise TimeoutError("frame timeout")
            chunk = self._io(length - len(payload), write=False, timeout_ms=remaining)
            if not chunk:
                raise EOFError
            payload.extend(chunk)
        return parse(bytes(payload))

    def send(self, value: dict) -> None:
        encoded = frame(value)
        offset = 0
        while offset < len(encoded):
            count = self._io(encoded[offset:], write=True, timeout_ms=1000)
            if not count:
                raise EOFError
            offset += count

    def close(self) -> None:
        self.kernel.CloseHandle(self.handle)


class ControlSession:
    """Observe the trusted native peer and keep its input explicitly neutral."""

    def __init__(self, trusted_server_image: str) -> None:
        self._trusted_server_image = trusted_server_image
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._client: ControlClient | None = None
        self._status: ControlStatus | None = None
        self._thread: threading.Thread | None = None

    @property
    def status(self) -> ControlStatus | None:
        with self._lock:
            return self._status

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="ETS2AP-Control-Status", daemon=True)
        self._thread.start()

    def disengage(self, safety_flags: SafetyFlags | int = 0) -> None:
        with self._lock:
            client = self._client
        if client is None:
            return
        try:
            client.send_command(0.0, 0.0, 0.0, False, safety_flags=safety_flags)
        except ControlClientError:
            pass

    def close(self) -> None:
        self._stop.set()
        self.disengage()
        thread = self._thread
        if thread is not None:
            thread.join(timeout=1.0)
        with self._lock:
            client = self._client
            self._client = None
            self._status = None
        if client is not None:
            client.close()

    def _run(self) -> None:
        while not self._stop.is_set():
            client: ControlClient | None = None
            heartbeat_stop: threading.Event | None = None
            heartbeat_thread: threading.Thread | None = None
            try:
                client = ControlClient(
                    self._trusted_server_image,
                    authorize_engage=lambda: False,
                )
                client.connect(timeout_ms=250)
                with self._lock:
                    self._client = client
                    self._status = None
                heartbeat_stop = threading.Event()
                heartbeat_thread = threading.Thread(
                    target=self._send_neutral_heartbeats,
                    args=(client, heartbeat_stop),
                    name="ETS2AP-Control-Neutral-Heartbeat",
                    daemon=True,
                )
                heartbeat_thread.start()
                while not self._stop.is_set():
                    status = client.read_status()
                    with self._lock:
                        self._status = status
            except (ControlClientError, OSError, ValueError):
                pass
            finally:
                if heartbeat_stop is not None:
                    heartbeat_stop.set()
                if heartbeat_thread is not None:
                    heartbeat_thread.join(timeout=1.0)
                with self._lock:
                    if self._client is client:
                        self._client = None
                    self._status = None
                if client is not None:
                    client.close()
            self._stop.wait(0.5)

    def _send_neutral_heartbeats(self, client: ControlClient,
                                 stop: threading.Event) -> None:
        while not self._stop.is_set() and not stop.wait(CONTROL_NEUTRAL_HEARTBEAT_INTERVAL_S):
            try:
                # Keep the 100 ms native watchdog alive independently of status updates.
                client.send_command(0.0, 0.0, 0.0, False)
            except (ControlClientError, OSError, ValueError):
                return


def perception_payload(frame: PerceptionFrame | None, now_ns: int,
                       lane_confidence: float | None = None,
                       front_clear: bool | None = None) -> dict:
    age_ns = None if frame is None or frame.timestamp_ns > now_ns else now_ns - frame.timestamp_ns
    age_s = age_ns / 1_000_000_000 if age_ns is not None else None
    fresh = age_ns is not None and age_ns <= MAX_FRAME_AGE_NS
    valid = bool(frame and frame.frame_valid and fresh)
    if not valid:
        lane_confidence = None
    return {
        "frame_valid": valid,
        "frame_age_s": age_s,
        "game_focused": bool(frame and frame.game_focused),
        "input_monitor_healthy": bool(frame and frame.input_monitor_healthy),
        "manual_input_active": bool(frame and frame.manual_input_active),
        "lane_confidence": lane_confidence,
        "front_clear": front_clear,
    }


def control_payload(status: ControlStatus | None, *, configured: bool, now_ns: int) -> dict:
    if not configured:
        return {"connection": "not_configured", "state": None, "reason": None}
    if (status is None or not isinstance(status.state, ControlState) or
            not isinstance(status.reason, ControlReason) or
            status.timestamp_ns > now_ns or now_ns - status.timestamp_ns > MAX_STATUS_AGE_NS):
        return {"connection": "disconnected", "state": None, "reason": None}
    return {
        "connection": "connected",
        "state": status.state.name.lower(),
        "reason": status.reason.name.lower(),
    }


def status_payload(summary: TelemetrySummary | None, revision: int,
                   perception: dict | None = None,
                   control: dict | None = None, *,
                   version: str = VERSION, now_ns: int | None = None) -> dict:
    if summary is None or summary.speed_mps is None or summary.paused is None:
        error = "TELEMETRY_UNAVAILABLE"
    elif summary.telemetry_age_s is None or summary.telemetry_age_s > 0.25:
        error = "TELEMETRY_STALE"
    else:
        error = "CONTROL_UNAVAILABLE"
    payload = {"state": "fault", "error_code": error, "state_revision": revision,
            "telemetry": {"speed_mps": summary.speed_mps if summary else None,
                          "speed_limit_mps": summary.speed_limit_mps if summary else None,
                          "navigation_distance_m": summary.navigation_distance_m if summary else None,
                          "telemetry_age_s": summary.telemetry_age_s if summary else None}}
    if perception is not None and version in ("1.1", "1.2", "1.3"):
        payload["perception"] = perception
    if control is not None and version in ("1.2", "1.3"):
        payload["control"] = control
    if version == "1.3":
        navigation_time = summary.navigation_time_s if summary else None
        sample_ns = summary.snapshot_timestamp_ns if summary else None
        current_ns = time.perf_counter_ns() if now_ns is None else now_ns
        try:
            valid_time = (isinstance(navigation_time, (int, float)) and
                          not isinstance(navigation_time, bool) and
                          math.isfinite(navigation_time) and navigation_time >= 0)
        except OverflowError:
            valid_time = False
        valid_sample = (isinstance(sample_ns, int) and not isinstance(sample_ns, bool) and
                        isinstance(current_ns, int) and not isinstance(current_ns, bool) and
                        0 < sample_ns <= current_ns <= (1 << 64) - 1 and
                        current_ns - sample_ns <= MAX_NAVIGATION_AGE_NS)
        fresh = valid_time and valid_sample
        payload["telemetry"]["navigation_time_s"] = navigation_time if fresh else None
        payload["telemetry"]["navigation_time_sample_timestamp_ns"] = str(sample_ns) if fresh else None
    return payload

def serve(trusted_ui_image: str, trusted_control_server_image: str | None = None) -> None:
    pipe = WinPipe(trusted_ui_image)
    reader = TelemetryReader()
    perception_reader = PerceptionReader()
    driver_runtime = DriverAssistRuntime(front_detector_validated=False)
    control_session = (ControlSession(trusted_control_server_image)
                       if trusted_control_server_image else None)
    if control_session is not None:
        control_session.start()
    try:
        while True:
            pipe.connect()
            try:
                if not pipe.verify_client():
                    continue
                hello = pipe.read_frame(1000)
                if (hello is None or hello["type"] != "hello" or
                        hello["version"].split(".")[0] != "1" or
                        not hello["id"].startswith("u-")):
                    continue
                versions = hello["payload"].get("supported_versions")
                if (not isinstance(versions, list) or not versions or
                        any(not isinstance(version, str) for version in versions) or
                        not any(version in SUPPORTED_VERSIONS for version in versions)):
                    pipe.send(message("error", hello["id"], {"code": "UNSUPPORTED_VERSION"}))
                    continue
                selected_version = next(version for version in SUPPORTED_VERSIONS
                                        if version in versions)
                pipe.send(message("hello_ack", hello["id"],
                                  {"selected_version": selected_version},
                                  version=selected_version))
                counter = 0
                pipe.send(message("config", "p-1", {"config": {"target_speed_mps": 0}},
                                  version=selected_version))
                last_state = None
                revision = 0
                latest_frame = None
                last_status_event_ns = None
                seen_requests = {hello["id"]}
                while True:
                    summary = reader.read()
                    fresh_frame = perception_reader.read()
                    if fresh_frame is not None:
                        latest_frame = fresh_frame
                    now_ns = time.perf_counter_ns()
                    driver_tick = driver_runtime.evaluate(summary, latest_frame, now_ns)
                    native_status = control_session.status if control_session is not None else None
                    if native_status is not None and native_status.state is ControlState.ENGAGED:
                        control_session.disengage(driver_tick.safety_flags)
                    perception = (perception_payload(
                        latest_frame, now_ns, driver_tick.lane_confidence, driver_tick.front_clear)
                        if selected_version in ("1.1", "1.2", "1.3") else None)
                    control = control_payload(
                        native_status,
                        configured=control_session is not None,
                        now_ns=now_ns,
                    ) if selected_version in ("1.2", "1.3") else None
                    candidate_now_ns = time.perf_counter_ns()
                    candidate = status_payload(summary, revision, perception, control,
                                               version=selected_version, now_ns=candidate_now_ns)
                    state_key = (candidate["state"], candidate["error_code"])
                    if last_state is not None and state_key != last_state:
                        revision += 1
                    candidate["state_revision"] = revision
                    last_state = state_key
                    if (selected_version != "1.3" or last_status_event_ns is None or
                            candidate_now_ns < last_status_event_ns or
                            candidate_now_ns - last_status_event_ns >= UI_V13_STATUS_INTERVAL_NS):
                        counter += 1
                        pipe.send(message("status", f"p-{counter + 1}", candidate,
                                          version=selected_version))
                        last_status_event_ns = time.perf_counter_ns()
                    request = pipe.read_frame(50 if selected_version == "1.3" else 250)
                    if request is None:
                        continue
                    if not request["id"].startswith("u-") or request["id"] in seen_requests:
                        raise ValueError("invalid request envelope")
                    seen_requests.add(request["id"])
                    if request["version"].split(".")[0] != "1":
                        pipe.send(message("error", request["id"], {"code": "UNSUPPORTED_VERSION"}, version=selected_version))
                        raise ValueError("unsupported version")
                    kind = request["type"]
                    if kind == "get_status" and request["payload"] == {}:
                        response = status_payload(summary, revision, perception, control,
                                                  version=selected_version, now_ns=time.perf_counter_ns())
                        pipe.send(message("status", request["id"], response,
                                          version=selected_version))
                    elif kind == "disengage" and request["payload"] == {}:
                        if control_session is not None:
                            control_session.disengage()
                        response = status_payload(summary, revision, perception, control,
                                                  version=selected_version, now_ns=time.perf_counter_ns())
                        pipe.send(message("status", request["id"], response,
                                          version=selected_version))
                    elif kind == "engage":
                        if control_session is not None:
                            control_session.disengage()
                        requested_revision = request["payload"].get("state_revision")
                        code = ("CONTROL_UNAVAILABLE" if type(requested_revision) is int and requested_revision >= 0
                                else "MALFORMED_MESSAGE")
                        pipe.send(message("error", request["id"], {"code": code}, version=selected_version))
                    else:
                        pipe.send(message("error", request["id"], {"code": "MALFORMED_MESSAGE"}, version=selected_version))
            except (EOFError, OSError, ValueError, TimeoutError):
                pass
            finally:
                pipe.kernel.DisconnectNamedPipe(pipe.handle)
    finally:
        if control_session is not None:
            control_session.close()
        pipe.close()


def main() -> None:
    parser = argparse.ArgumentParser(description="ETS2AP UI status service")
    parser.add_argument("--trusted-ui-image", required=True, help="canonical path of the trusted UI executable")
    parser.add_argument("--trusted-control-server-image",
                        help="canonical path of the selected ETS2 executable")
    args = parser.parse_args()
    serve(args.trusted_ui_image, args.trusted_control_server_image)


if __name__ == "__main__":
    main()
