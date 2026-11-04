"""Offline checks for telemetry summary and UI framing.

Edited by: OpenAI-Codex_Responsible: Source_Code
Edited by: OpenAI-Codex_Source_Code
"""

import inspect
import struct
import sys
import threading
import time
import unittest
from unittest import mock

from ets2ap.telemetry import parse_snapshot
from ets2ap.control_protocol import ControlReason, ControlState, ControlStatus
from ets2ap.ui_protocol import frame, message, parse
from ets2ap.perception_protocol import FrameFlags, PerceptionFrame, PIXEL_BYTES
from ets2ap import ui_server
from ets2ap.ui_server import perception_payload, status_payload


class UiStatusTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "win32", "Windows QPC only")
    def test_python_clock_shares_qpc_origin(self):
        import ctypes

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        frequency = ctypes.c_int64()
        before = ctypes.c_int64()
        after = ctypes.c_int64()
        self.assertTrue(kernel.QueryPerformanceFrequency(ctypes.byref(frequency)))
        self.assertTrue(kernel.QueryPerformanceCounter(ctypes.byref(before)))
        observed = time.perf_counter_ns()
        self.assertTrue(kernel.QueryPerformanceCounter(ctypes.byref(after)))
        lower = before.value * 1_000_000_000 // frequency.value
        upper = after.value * 1_000_000_000 // frequency.value
        self.assertLessEqual(lower - 1_000_000, observed)
        self.assertLessEqual(observed, upper + 1_000_000)

    def test_frame_round_trip(self):
        encoded = frame(message("get_status", "u-1", {}))
        self.assertEqual(struct.unpack_from("<I", encoded)[0], len(encoded) - 4)
        decoded = parse(encoded[4:])
        self.assertEqual(decoded["type"], "get_status")
        self.assertEqual(decoded["version"], "1.3")
        self.assertEqual(message("get_status", "u-2", {}, version="1.1")["version"], "1.1")
        self.assertEqual(message("get_status", "u-3", {}, version="1.2")["version"], "1.2")
        self.assertEqual(message("get_status", "u-4", {}, version="1.0")["version"], "1.0")

    def test_reject_duplicate_and_nonfinite(self):
        base = b'{"type":"hello","type":"status","version":"1.0","id":"u-1","timestamp":"1","payload":{}}'
        with self.assertRaises(ValueError):
            parse(base)
        with self.assertRaises(ValueError):
            parse(b'{"type":"hello","version":"1.0","id":"u-1","timestamp":"1","payload":{"x":1e999}}')

    def test_telemetry_validity_and_age(self):
        data = bytearray(240)
        data[:4] = b"E2AP"
        struct.pack_into("<HHII", data, 4, 1, 0, 240, 1 | (1 << 1) | (1 << 10) | (1 << 12))
        struct.pack_into("<Q", data, 24, 1_000_000_000)
        struct.pack_into("<d", data, 48, 10.0)
        struct.pack_into("<d", data, 208, 1250.0)
        struct.pack_into("<d", data, 224, 20.0)
        summary = parse_snapshot(data, 1_100_000_000)
        self.assertEqual((summary.speed_mps, summary.navigation_distance_m, summary.speed_limit_mps),
                         (10.0, 1250.0, 20.0))
        self.assertAlmostEqual(summary.telemetry_age_s, 0.1)
        self.assertEqual(status_payload(summary, 0)["error_code"], "CONTROL_UNAVAILABLE")
        self.assertEqual(status_payload(None, 0)["error_code"], "TELEMETRY_UNAVAILABLE")
        control = {"connection": "not_configured", "state": None, "reason": None}
        self.assertEqual(status_payload(summary, 0, control=control)["control"], control)
        self.assertNotIn("control", status_payload(summary, 0))

    @staticmethod
    def navigation_snapshot(value, *, valid=True, speed_valid=True):
        data = bytearray(240)
        data[:4] = b"E2AP"
        flags = 1 | (1 << 12) | ((1 << 11) if valid else 0)
        if speed_valid:
            flags |= 1 << 1
        struct.pack_into("<HHII", data, 4, 1, 0, 240, flags)
        struct.pack_into("<Q", data, 24, 1_000_000_000)
        struct.pack_into("<d", data, 48, 10.0)
        struct.pack_into("<d", data, 216, value)
        struct.pack_into("<d", data, 224, 20.0)
        return data

    def test_navigation_time_snapshot_values(self):
        summary = parse_snapshot(self.navigation_snapshot(0.0), 1_100_000_000)
        self.assertEqual(getattr(summary, "navigation_time_s", None), 0.0)
        self.assertEqual(getattr(summary, "snapshot_timestamp_ns", None), 1_000_000_000)
        self.assertEqual(parse_snapshot(self.navigation_snapshot(90_061.5), 1_100_000_000).navigation_time_s,
                         90_061.5)
        self.assertEqual(parse_snapshot(self.navigation_snapshot(1e308), 1_100_000_000).navigation_time_s,
                         1e308)
        self.assertIsNone(parse_snapshot(self.navigation_snapshot(999.0, valid=False),
                                         1_100_000_000).navigation_time_s)
        for invalid in (-1.0, float("nan"), float("inf")):
            with self.subTest(invalid=invalid):
                invalid_summary = parse_snapshot(self.navigation_snapshot(invalid), 1_100_000_000)
                self.assertIsNone(invalid_summary.navigation_time_s)
                self.assertEqual(invalid_summary.speed_mps, 10.0)
        without_speed = parse_snapshot(self.navigation_snapshot(42.0, speed_valid=False),
                                       1_100_000_000)
        self.assertIsNone(without_speed.speed_mps)
        self.assertEqual(without_speed.navigation_time_s, 42.0)

    def test_navigation_status_version_and_freshness(self):
        self.assertIn("version", inspect.signature(status_payload).parameters)
        summary = parse_snapshot(self.navigation_snapshot(0.0, speed_valid=False), 1_100_000_000)
        perception = {"frame_valid": False, "frame_age_s": None, "game_focused": False,
                      "input_monitor_healthy": False, "manual_input_active": False,
                      "lane_confidence": None, "front_clear": None}
        control = {"connection": "not_configured", "state": None, "reason": None}
        for version in ("1.0", "1.1", "1.2", "1.3"):
            with self.subTest(version=version):
                payload = status_payload(summary, 0, perception, control,
                                         version=version, now_ns=1_250_000_000)
                telemetry = payload["telemetry"]
                self.assertEqual("perception" in payload, version != "1.0")
                self.assertEqual("control" in payload, version in ("1.2", "1.3"))
                if version == "1.3":
                    self.assertEqual(telemetry["navigation_time_s"], 0.0)
                    self.assertEqual(telemetry["navigation_time_sample_timestamp_ns"], "1000000000")
                else:
                    self.assertNotIn("navigation_time_s", telemetry)
                    self.assertNotIn("navigation_time_sample_timestamp_ns", telemetry)
        for now_ns in (999_999_999, 1_250_000_001):
            with self.subTest(now_ns=now_ns):
                telemetry = status_payload(summary, 0, version="1.3", now_ns=now_ns)["telemetry"]
                self.assertIsNone(telemetry["navigation_time_s"])
                self.assertIsNone(telemetry["navigation_time_sample_timestamp_ns"])
        telemetry = status_payload(None, 0, version="1.3", now_ns=1_100_000_000)["telemetry"]
        self.assertIsNone(telemetry["navigation_time_s"])
        self.assertIsNone(telemetry["navigation_time_sample_timestamp_ns"])

    def test_navigation_version_negotiation_and_delayed_get_status(self):
        self.assertIn("1.3", ui_server.SUPPORTED_VERSIONS)
        summary = parse_snapshot(self.navigation_snapshot(75.0), 1_100_000_000)

        class EndSession(Exception):
            pass

        class FakePipe:
            def __init__(self, versions, clock):
                self.versions, self.clock, self.sent, self.read_count = versions, clock, [], 0
                self.kernel, self.handle = self, 0

            def connect(self):
                pass

            def verify_client(self):
                return True

            def read_frame(self, timeout):
                self.read_count += 1
                if self.read_count == 1:
                    return {"type": "hello", "version": self.versions[0], "id": "u-1",
                            "payload": {"supported_versions": self.versions}}
                if self.read_count == 2:
                    self.clock[0] = 1_300_000_001
                    return {"type": "get_status", "version": self.versions[0],
                            "id": "u-2", "payload": {}}
                raise EndSession()

            def send(self, value):
                self.sent.append(value)
                if value["type"] == "status" and value["id"] == "u-2":
                    raise EndSession()

            def DisconnectNamedPipe(self, handle):
                return True

            def close(self):
                pass

        for versions, selected in ((["1.3", "1.2", "1.1", "1.0"], "1.3"),
                                   (["1.2", "1.1", "1.0"], "1.2"),
                                   (["1.1", "1.0"], "1.1"), (["1.0"], "1.0")):
            with self.subTest(selected=selected):
                clock = [1_100_000_000]
                pipe = FakePipe(versions, clock)
                reader = mock.Mock()
                reader.read.return_value = summary
                perception_reader = mock.Mock()
                perception_reader.read.return_value = None
                with mock.patch.object(ui_server, "WinPipe", return_value=pipe), \
                     mock.patch.object(ui_server, "TelemetryReader", return_value=reader), \
                     mock.patch.object(ui_server, "PerceptionReader", return_value=perception_reader), \
                     mock.patch.object(ui_server.time, "perf_counter_ns", side_effect=lambda: clock[0]):
                    with self.assertRaises(EndSession):
                        ui_server.serve("trusted-ui")
                ack = next(item for item in pipe.sent if item["type"] == "hello_ack")
                self.assertEqual(ack["payload"]["selected_version"], selected)
                statuses = [item for item in pipe.sent if item["type"] == "status"]
                self.assertEqual(len(statuses), 2)
                for status in statuses:
                    self.assertEqual("perception" in status["payload"], selected != "1.0")
                    self.assertEqual("control" in status["payload"], selected in ("1.2", "1.3"))
                if selected == "1.3":
                    self.assertEqual(statuses[0]["payload"]["telemetry"]["navigation_time_s"], 75.0)
                    self.assertIsNone(statuses[1]["payload"]["telemetry"]["navigation_time_s"])
                    self.assertIsNone(statuses[1]["payload"]["telemetry"]["navigation_time_sample_timestamp_ns"])
                else:
                    self.assertNotIn("navigation_time_s", statuses[0]["payload"]["telemetry"])

    @staticmethod
    def run_navigation_session(version, *, fresh_updates, burst=False, slow_first_event=False, evaluation_delay_ns=0):
        clock = [1_000_000_000]

        class EndSession(Exception):
            pass

        class FakePipe:
            def __init__(self):
                self.kernel, self.handle = self, 0
                self.sent, self.waits, self.read_count = [], [], 0

            def connect(self):
                pass

            def verify_client(self):
                return True

            def read_frame(self, timeout):
                self.read_count += 1
                if self.read_count == 1:
                    return {"type": "hello", "version": version, "id": "u-1",
                            "payload": {"supported_versions": [version]}}
                self.waits.append(timeout)
                if burst:
                    if len(self.waits) > 1:
                        previous_id = "u-" + str(len(self.waits))
                        if not any(item["id"] == previous_id for item in self.sent):
                            raise AssertionError("A request was delayed until the next read.")
                    if len(self.waits) > 10:
                        raise EndSession()
                    kind = "disengage" if len(self.waits) % 2 == 0 else "get_status"
                    return {"type": kind, "version": version,
                            "id": "u-" + str(len(self.waits) + 1), "payload": {}}
                if len(self.waits) >= 7:
                    raise EndSession()
                clock[0] += timeout * 1_000_000
                return None

            def send(self, value):
                self.sent.append(value)
                if (slow_first_event and value["type"] == "status" and
                        value["id"].startswith("p-") and not getattr(self, "delayed", False)):
                    self.delayed = True
                    clock[0] += 200_000_000

            def DisconnectNamedPipe(self, handle):
                return True

            def close(self):
                pass

        class FakeTelemetryReader:
            def read(self):
                data = UiStatusTests.navigation_snapshot(75.0)
                if fresh_updates:
                    struct.pack_into("<Q", data, 24, clock[0])
                return parse_snapshot(data, clock[0])

        pipe = FakePipe()
        perception_reader = mock.Mock()
        perception_reader.read.return_value = None
        control_session = mock.Mock()
        control_session.status = None
        pipe.control_session = control_session
        driver = ui_server.DriverAssistRuntime()
        actual_evaluate = driver.evaluate

        def delayed_evaluate(*args):
            result = actual_evaluate(*args)
            clock[0] += evaluation_delay_ns
            return result

        driver.evaluate = delayed_evaluate
        with mock.patch.object(ui_server, "WinPipe", return_value=pipe), \
             mock.patch.object(ui_server, "TelemetryReader", return_value=FakeTelemetryReader()), \
             mock.patch.object(ui_server, "PerceptionReader", return_value=perception_reader), \
             mock.patch.object(ui_server, "ControlSession", return_value=control_session), \
             mock.patch.object(ui_server, "DriverAssistRuntime", return_value=driver), \
             mock.patch.object(ui_server.time, "perf_counter_ns", side_effect=lambda: clock[0]):
            try:
                ui_server.serve("trusted-ui", "trusted-game" if burst else None)
            except EndSession:
                pass
        return pipe

    def test_navigation_status_cadence_and_expiry(self):
        fresh = self.run_navigation_session("1.3", fresh_updates=True)
        self.assertEqual(fresh.waits, [50] * 7)
        events = [item for item in fresh.sent if item["type"] == "status"]
        self.assertEqual(len(events), 7)
        self.assertTrue(all(item["payload"]["telemetry"]["navigation_time_s"] == 75.0
                            for item in events))
        timestamps = [int(item["timestamp"]) for item in events]
        self.assertEqual([later - earlier for earlier, later in zip(timestamps, timestamps[1:])],
                         [50_000_000] * 6)

        stopped = self.run_navigation_session("1.3", fresh_updates=False)
        values = [item["payload"]["telemetry"]["navigation_time_s"]
                  for item in stopped.sent if item["type"] == "status"]
        self.assertEqual(values, [75.0] * 6 + [None])
        for version in ("1.0", "1.1", "1.2"):
            with self.subTest(version=version):
                legacy = self.run_navigation_session(version, fresh_updates=True)
                self.assertEqual(legacy.waits, [250] * 7)

    def test_navigation_unsolicited_status_not_overproduced(self):
        pipe = self.run_navigation_session("1.3", fresh_updates=True, burst=True)
        events = [item for item in pipe.sent if item["type"] == "status"
                  and item["id"].startswith("p-")]
        responses = [item for item in pipe.sent if item["type"] == "status"
                     and item["id"].startswith("u-")]
        self.assertEqual(len(events), 1)
        self.assertEqual(len(responses), 10)
        self.assertEqual(pipe.control_session.disengage.call_count, 5)

    def test_navigation_status_rechecks_after_slow_evaluation(self):
        pipe = self.run_navigation_session("1.3", fresh_updates=False,
                                           evaluation_delay_ns=300_000_000)
        first = next(item for item in pipe.sent if item["type"] == "status")
        telemetry = first["payload"]["telemetry"]
        self.assertIsNone(telemetry["navigation_time_s"])
        self.assertIsNone(telemetry["navigation_time_sample_timestamp_ns"])

    def test_navigation_status_throttle_uses_send_completion(self):
        pipe = self.run_navigation_session("1.3", fresh_updates=True, burst=True,
                                           slow_first_event=True)
        events = [item for item in pipe.sent if item["type"] == "status"
                  and item["id"].startswith("p-")]
        responses = [item for item in pipe.sent if item["type"] == "status"
                     and item["id"].startswith("u-")]
        self.assertEqual(len(events), 1)
        self.assertEqual(len(responses), 10)
        self.assertEqual(pipe.control_session.disengage.call_count, 5)

    def test_navigation_legacy_error_responses_keep_negotiated_version(self):
        summary = parse_snapshot(self.navigation_snapshot(75.0), 1_100_000_000)

        class EndSession(Exception):
            pass

        class FakePipe:
            def __init__(self, version, request):
                self.version, self.request, self.sent, self.read_count = version, request, [], 0
                self.kernel, self.handle = self, 0

            def connect(self):
                pass

            def verify_client(self):
                return True

            def read_frame(self, timeout):
                self.read_count += 1
                if self.read_count == 1:
                    return {"type": "hello", "version": self.version, "id": "u-1",
                            "payload": {"supported_versions": [self.version]}}
                return self.request

            def send(self, value):
                self.sent.append(value)
                if value["type"] == "error":
                    raise EndSession()

            def DisconnectNamedPipe(self, handle):
                return True

            def close(self):
                pass

        cases = (("engage", "1", {"state_revision": 0}, "CONTROL_UNAVAILABLE"),
                 ("engage", "1", {}, "MALFORMED_MESSAGE"),
                 ("unknown", "1", {}, "MALFORMED_MESSAGE"),
                 ("get_status", "2", {}, "UNSUPPORTED_VERSION"))
        for version in ("1.0", "1.1", "1.2", "1.3"):
            for kind, major, payload, expected_code in cases:
                with self.subTest(version=version, kind=kind):
                    request = {"type": kind, "version": major + ".0", "id": "u-2",
                               "payload": payload}
                    pipe = FakePipe(version, request)
                    reader = mock.Mock()
                    reader.read.return_value = summary
                    perception_reader = mock.Mock()
                    perception_reader.read.return_value = None
                    with mock.patch.object(ui_server, "WinPipe", return_value=pipe), \
                         mock.patch.object(ui_server, "TelemetryReader", return_value=reader), \
                         mock.patch.object(ui_server, "PerceptionReader", return_value=perception_reader), \
                         mock.patch.object(ui_server.time, "perf_counter_ns", return_value=1_100_000_000):
                        with self.assertRaises(EndSession):
                            ui_server.serve("trusted-ui")
                    error = next(item for item in pipe.sent if item["type"] == "error")
                    self.assertEqual(error["version"], version)
                    self.assertEqual(error["payload"]["code"], expected_code)

    def test_perception_status_keeps_unvalidated_gates_explicit(self):
        frame_value = PerceptionFrame(2, 1_000_000_000,
            FrameFlags.GAME_FOCUSED | FrameFlags.FRAME_VALID, bytes(PIXEL_BYTES))
        payload = perception_payload(frame_value, 1_040_000_000,
                                     lane_confidence=0.5, front_clear=None)
        self.assertTrue(payload["frame_valid"])
        self.assertTrue(payload["game_focused"])
        self.assertFalse(payload["input_monitor_healthy"])
        self.assertIsNone(payload["front_clear"])
        self.assertEqual(payload["lane_confidence"], 0.5)

    def test_stale_perception_status_does_not_publish_lane_confidence(self):
        frame_value = PerceptionFrame(2, 1_000_000_000,
            FrameFlags.GAME_FOCUSED | FrameFlags.FRAME_VALID, bytes(PIXEL_BYTES))
        payload = perception_payload(frame_value, 1_200_000_001)
        self.assertFalse(payload["frame_valid"])
        self.assertIsNone(payload["lane_confidence"])
        self.assertIsNone(payload["front_clear"])

    def test_control_payload_distinguishes_unconfigured_and_disconnected(self):
        build = getattr(ui_server, "control_payload", lambda *args, **kwargs: None)
        self.assertEqual(build(None, configured=False, now_ns=100), {
            "connection": "not_configured", "state": None, "reason": None,
        })
        self.assertEqual(build(None, configured=True, now_ns=100), {
            "connection": "disconnected", "state": None, "reason": None,
        })

    def test_control_payload_includes_only_fresh_native_status(self):
        build = getattr(ui_server, "control_payload", lambda *args, **kwargs: None)
        status = ControlStatus(1, 90, ControlState.ENGAGED, ControlReason.NONE,
                               0.0, 0.0, 0.0)
        self.assertEqual(build(status, configured=True, now_ns=100), {
            "connection": "connected", "state": "engaged", "reason": "none",
        })
        self.assertEqual(build(status, configured=True, now_ns=300_000_091), {
            "connection": "disconnected", "state": None, "reason": None,
        })
        self.assertEqual(build(status, configured=True, now_ns=89), {
            "connection": "disconnected", "state": None, "reason": None,
        })

    def test_control_session_neutral_heartbeat_is_independent_of_status_rate(self):
        class FastStatusClient:
            def __init__(self):
                self.status_reads = 0
                self.commands = []
                self.lock = threading.Lock()

            def connect(self, timeout_ms):
                self.send_command(0.0, 0.0, 0.0, False)

            def read_status(self):
                with self.lock:
                    self.status_reads += 1
                time.sleep(0.001)
                return None

            def send_command(self, steering, throttle, brake, engage, **kwargs):
                with self.lock:
                    self.commands.append(time.monotonic())

            def close(self):
                pass

        client = FastStatusClient()
        with mock.patch.object(ui_server, "ControlClient", return_value=client):
            session = ui_server.ControlSession("unused-for-fake-client")
            session.start()
            time.sleep(0.15)
            session.close()

        self.assertGreaterEqual(client.status_reads, 20)
        self.assertLessEqual(len(client.commands), 6)


if __name__ == "__main__":
    unittest.main()
