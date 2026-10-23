"""Stateful protocol checks using independently encoded peer bytes, without a capture driver."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("peer_capture", Path(__file__).with_name("analyze-peer-capture.py"))
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


def frame(message, payload=b""):
    return struct.pack(">I", 1 + len(payload)) + bytes([message]) + payload


class ProtocolCaptureTests(unittest.TestCase):
    def inspect(self, exchange, fast=True):
        data = b"payload"
        info = b"d6:lengthi7e4:name1:x12:piece lengthi8e6:pieces20:" + hashlib.sha1(data).digest() + b"e"
        handshake = (b"\x13BitTorrent protocol" + b"\0" * 7 + bytes([4 if fast else 0])
                     + hashlib.sha1(info).digest() + b"-TEST00-abcdefghijkl")
        lines = ["Node 0: 10.0.0.1:1234\n", "Node 1: 10.0.0.2:5678\n",
                 handshake.hex() + "\n", "\t" + handshake.hex() + "\n"]
        for outgoing, payload in exchange:
            # Fragment both the length prefix and the body over arbitrary TCP chunks.
            for chunk in (payload[:1], payload[1:5], payload[5:]):
                if chunk:
                    lines.append(("" if outgoing else "\t") + chunk.hex() + "\n")

        class FollowProcess:
            def __init__(self):
                self.stdout = io.StringIO("".join(lines))

            def poll(self):
                return 0

            def wait(self):
                return 0

        with tempfile.TemporaryDirectory() as root:
            torrent = Path(root, "input.torrent")
            torrent.write_bytes(b"d4:info" + info + b"e")
            output = Path(root, "report.json")
            args = ["analyze", "unused.pcapng", str(torrent), "--local-address", "10.0.0.1", "--output", str(output)]
            with patch("sys.argv", args), patch.object(capture.subprocess, "check_output", return_value="0\n"), \
                    patch.object(capture.subprocess, "Popen", return_value=FollowProcess()), contextlib.redirect_stdout(io.StringIO()):
                capture.main()
            return json.loads(output.read_text(encoding="utf-8"))

    def test_fragmented_valid_transfer_verifies_captured_piece(self):
        report = self.inspect([(False, frame(1)), (True, frame(6, struct.pack(">III", 0, 0, 7))),
                               (False, frame(7, struct.pack(">II", 0, 0) + b"payload"))])
        self.assertEqual([], report["issueSamples"])
        self.assertEqual(1, report["capturedWholePiecesVerified"])

    def test_allowed_fast_request_is_valid_while_choked(self):
        report = self.inspect([(False, frame(17, struct.pack(">I", 0))),
                               (True, frame(6, struct.pack(">III", 0, 0, 7)))])
        self.assertEqual([], report["issueSamples"])

    def test_detects_range_choke_and_duplicate_request_errors(self):
        request = frame(6, struct.pack(">III", 3, 0, 7))
        report = self.inspect([(True, request), (True, request)])
        self.assertEqual(2, report["counts"]["invalid_engine_request_range"])
        self.assertEqual(2, report["counts"]["engine_request_while_choked"])
        self.assertEqual(1, report["counts"]["duplicate_outstanding_engine_request"])

    def test_cancelled_request_can_still_receive_its_piece(self):
        request = struct.pack(">III", 0, 0, 7)
        report = self.inspect([(False, frame(1)), (True, frame(6, request)), (True, frame(8, request)),
                               (False, frame(7, struct.pack(">II", 0, 0) + b"payload"))])
        self.assertEqual([], report["issueSamples"])
        self.assertEqual(1, report["counts"]["piece_responses_after_cancel_or_choke"])

    def test_hash_mismatch_is_detected_independently_of_request_matching(self):
        report = self.inspect([(False, frame(1)), (True, frame(6, struct.pack(">III", 0, 0, 7))),
                               (False, frame(7, struct.pack(">II", 0, 0) + b"corrupt"))])
        self.assertEqual(1, report["counts"]["captured_piece_hash_mismatch"])

    def test_unsolicited_piece_is_reported(self):
        report = self.inspect([(False, frame(7, struct.pack(">II", 0, 0) + b"payload"))])
        self.assertEqual(1, report["counts"]["unmatched_peer_response"])


if __name__ == "__main__":
    unittest.main()
