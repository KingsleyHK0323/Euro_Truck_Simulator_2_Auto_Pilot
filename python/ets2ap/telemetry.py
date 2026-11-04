"""Read the v1 native telemetry mapping without creating a missing mapping.

Edited by: OpenAI-Codex_Responsible: Source_Code
Edited by: OpenAI-Codex_Source_Code
"""

from __future__ import annotations

import ctypes
import math
import struct
import sys
import time
from dataclasses import dataclass

MAPPING_NAME = "Local\\ETS2AP_Telemetry_v1"
MAPPING_SIZE = 4096
PREFIX_SIZE = 240
FILE_MAP_READ = 0x0004
MAGIC = b"E2AP"


@dataclass(frozen=True)
class TelemetrySummary:
    speed_mps: float | None
    speed_limit_mps: float | None
    navigation_distance_m: float | None
    telemetry_age_s: float | None
    paused: bool | None
    navigation_time_s: float | None = None
    snapshot_timestamp_ns: int | None = None


def parse_snapshot(data: bytes, now_ns: int) -> TelemetrySummary:
    """Validate the stable known prefix; caller performs the seqlock copy."""
    if len(data) < PREFIX_SIZE or data[:4] != MAGIC:
        raise ValueError("invalid telemetry header")
    major, _minor, size, flags = struct.unpack_from("<HHII", data, 4)
    if major != 1 or not PREFIX_SIZE <= size <= MAPPING_SIZE:
        raise ValueError("unsupported telemetry layout")
    timestamp = struct.unpack_from("<Q", data, 24)[0]
    if not timestamp or timestamp > now_ns:
        raise ValueError("invalid telemetry timestamp")

    def value(bit: int, offset: int) -> float | None:
        if not flags & (1 << bit):
            return None
        number = struct.unpack_from("<d", data, offset)[0]
        if not math.isfinite(number) or number < 0:
            raise ValueError("invalid telemetry value")
        return number

    paused_byte = data[32]
    if flags & 1 and paused_byte not in (0, 1):
        raise ValueError("invalid paused flag")
    navigation_time_s = None
    if flags & (1 << 11):
        candidate = struct.unpack_from("<d", data, 216)[0]
        if math.isfinite(candidate) and candidate >= 0:
            navigation_time_s = candidate

    return TelemetrySummary(
        speed_mps=value(1, 48),
        speed_limit_mps=value(12, 224),
        navigation_distance_m=value(10, 208),
        telemetry_age_s=(now_ns - timestamp) / 1_000_000_000,
        paused=bool(paused_byte) if flags & 1 else None,
        navigation_time_s=navigation_time_s,
        snapshot_timestamp_ns=timestamp,
    )


class TelemetryReader:
    def __init__(self) -> None:
        if sys.platform != "win32":
            raise OSError("Windows is required")
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel32.OpenFileMappingW.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_wchar_p]
        self._kernel32.OpenFileMappingW.restype = ctypes.c_void_p
        self._kernel32.MapViewOfFile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_size_t]
        self._kernel32.MapViewOfFile.restype = ctypes.c_void_p
        self._kernel32.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
        self._kernel32.CloseHandle.argtypes = [ctypes.c_void_p]

    def read(self) -> TelemetrySummary | None:
        handle = self._kernel32.OpenFileMappingW(FILE_MAP_READ, False, MAPPING_NAME)
        if not handle:
            return None
        try:
            view = self._kernel32.MapViewOfFile(handle, FILE_MAP_READ, 0, 0, MAPPING_SIZE)
            if not view:
                return None
            try:
                deadline = time.perf_counter_ns() + 10_000_000
                while time.perf_counter_ns() < deadline:
                    first = ctypes.c_uint64.from_address(view + 16).value
                    if first & 1:
                        continue
                    data = ctypes.string_at(view, PREFIX_SIZE)
                    second = ctypes.c_uint64.from_address(view + 16).value
                    if first != second or second & 1:
                        continue
                    try:
                        return parse_snapshot(data, time.perf_counter_ns())
                    except ValueError:
                        return None
                return None
            finally:
                self._kernel32.UnmapViewOfFile(view)
        finally:
            self._kernel32.CloseHandle(handle)
