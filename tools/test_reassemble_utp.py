"""uTP sequence reconstruction: loss, reordering, duplicate delivery, and wraparound."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("utp_capture", Path(__file__).with_name("reassemble-utp.py"))
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class ReassemblyTests(unittest.TestCase):
    def test_releases_only_contiguous_packets(self):
        direction = capture.Direction(100)
        self.assertEqual([], direction.accept(102, b"C"))
        self.assertEqual([b"A"], direction.accept(100, b"A"))
        self.assertEqual([b"B", b"C"], direction.accept(101, b"B"))
        self.assertEqual({}, direction.queued)

    def test_sequence_wrap_preserves_delivery_order(self):
        direction = capture.Direction(65535)
        self.assertEqual([], direction.accept(0, b"after wrap"))
        self.assertEqual([b"before wrap", b"after wrap"], direction.accept(65535, b"before wrap"))
        self.assertEqual([b"next"], direction.accept(1, b"next"))

    def test_retransmissions_do_not_duplicate_application_bytes(self):
        direction = capture.Direction(5)
        self.assertEqual([b"payload"], direction.accept(5, b"payload"))
        self.assertEqual([], direction.accept(5, b"payload"))
        self.assertEqual(1, direction.counts["duplicate_data_packets"])
        self.assertNotIn("conflicting_retransmission_bytes", direction.counts)

    def test_conflicting_retransmission_is_reported(self):
        direction = capture.Direction(5)
        direction.accept(5, b"original")
        self.assertEqual([], direction.accept(5, b"changed"))
        self.assertEqual(1, direction.counts["conflicting_retransmission_bytes"])

    def test_missing_packet_is_never_silently_skipped(self):
        direction = capture.Direction(5)
        self.assertEqual([], direction.accept(6, b"later"))
        self.assertEqual(5, direction.expected)
        self.assertEqual({6: b"later"}, direction.queued)

    def test_unanchored_data_is_not_treated_as_a_complete_stream(self):
        direction = capture.Direction()
        self.assertEqual([], direction.accept(20, b"partial"))
        self.assertEqual(1, direction.counts["data_without_sequence_anchor"])


if __name__ == "__main__":
    unittest.main()
