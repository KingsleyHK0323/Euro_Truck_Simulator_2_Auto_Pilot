"""Bounded UI v1 JSON framing and message validation.

Edited by: OpenAI-Codex_Responsible: Source_Code
Edited by: OpenAI-Codex_Source_Code
"""

from __future__ import annotations

import json
import math
import re
import struct
import time

MAX_FRAME = 65_536
VERSION = "1.3"
SUPPORTED_VERSIONS = ("1.3", "1.2", "1.1", "1.0")
_ID = re.compile(r"^[up]-[A-Za-z0-9._-]{1,62}$", re.ASCII)
_TIME = re.compile(r"^(0|[1-9][0-9]*)$", re.ASCII)
_VERSION = re.compile(r"^[0-9]+\.[0-9]+$", re.ASCII)


def timestamp() -> str:
    return str(time.perf_counter_ns())


def frame(message: dict) -> bytes:
    data = json.dumps(message, allow_nan=False, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if not 1 <= len(data) <= MAX_FRAME:
        raise ValueError("frame size")
    return struct.pack("<I", len(data)) + data


def parse(data: bytes) -> dict:
    if not 1 <= len(data) <= MAX_FRAME:
        raise ValueError("frame size")

    def pairs(items: list[tuple[str, object]]) -> dict:
        if len(items) > 64 or len({key for key, _ in items}) != len(items):
            raise ValueError("object members")
        return dict(items)

    def reject_constant(_value: str) -> None:
        raise ValueError("nonfinite number")

    try:
        value = json.loads(data.decode("utf-8", errors="strict"), object_pairs_hook=pairs,
                           parse_constant=reject_constant)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError("invalid JSON") from error

    def bounded(item: object, depth: int) -> None:
        if depth > 16:
            raise ValueError("JSON depth")
        if isinstance(item, dict):
            for child in item.values():
                bounded(child, depth + 1)
        elif isinstance(item, list):
            for child in item:
                bounded(child, depth + 1)
        elif isinstance(item, float) and not math.isfinite(item):
            raise ValueError("nonfinite number")

    bounded(value, 1)
    if not isinstance(value, dict):
        raise ValueError("root must be object")
    if not isinstance(value.get("type"), str) or not isinstance(value.get("version"), str):
        raise ValueError("invalid envelope")
    if not _VERSION.fullmatch(value["version"]):
        raise ValueError("invalid version")
    if not isinstance(value.get("id"), str) or not _ID.fullmatch(value["id"]):
        raise ValueError("invalid id")
    clock = value.get("timestamp")
    if not isinstance(clock, str) or not _TIME.fullmatch(clock) or int(clock) > 2**64 - 1:
        raise ValueError("invalid timestamp")
    if not isinstance(value.get("payload"), dict):
        raise ValueError("invalid payload")
    return value


def message(kind: str, identifier: str, payload: dict, *, version: str = VERSION) -> dict:
    if version not in SUPPORTED_VERSIONS:
        raise ValueError("unsupported UI protocol version")
    return {"type": kind, "version": version, "id": identifier,
            "timestamp": timestamp(), "payload": payload}
