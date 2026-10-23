"""Reassemble captured uTP v1 datagrams into plaintext streams for analyze-peer-capture.py."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import struct
import subprocess


class Direction:
    def __init__(self, expected=None):
        self.expected = expected
        self.queued = {}
        self.seen = {}
        self.counts = collections.Counter()

    def accept(self, seq, data):
        if self.expected is None:
            self.counts["data_without_sequence_anchor"] += 1
            return []
        absolute = self.expected + ((seq - self.expected + 32768) % 65536 - 32768)
        digest = hashlib.sha256(data).digest()
        if absolute in self.seen:
            self.counts["duplicate_data_packets"] += 1
            if self.seen[absolute] != digest:
                self.counts["conflicting_retransmission_bytes"] += 1
            return []
        if absolute < self.expected:
            self.counts["data_before_sequence_anchor"] += 1
            return []
        self.seen[absolute] = digest
        if absolute > self.expected:
            self.counts["out_of_order_data_packets"] += 1
        self.queued[absolute] = data
        ready = []
        while self.expected in self.queued:
            ready.append(self.queued.pop(self.expected))
            self.expected += 1
        return ready


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--tshark", default=r"C:\Program Files\Wireshark\tshark.exe")
    args = parser.parse_args()
    args.output_directory.mkdir(parents=True, exist_ok=True)
    fields = ["frame.number", "ip.src", "udp.srcport", "ip.dst", "udp.dstport", "udp.payload"]
    command = [args.tshark, "-n", "-r", str(args.capture), "-Y", "udp", "-T", "fields"]
    for field in fields:
        command += ["-e", field]
    contexts = []
    lookup = {}
    counts = collections.Counter()
    sack_sizes = collections.Counter()
    process = subprocess.Popen(command, stdout=subprocess.PIPE, text=True)
    for line in process.stdout:
        parts = line.rstrip("\r\n").split("\t")
        if len(parts) != 6 or not all(parts[1:]):
            continue
        frame, src, sport, dst, dport, raw = parts
        packet = bytes.fromhex(raw.replace(":", ""))
        if len(packet) < 20 or packet[0] & 15 != 1 or packet[0] >> 4 > 4:
            counts["non_utp_datagrams"] += 1
            continue
        kind, extension, cid, stamp, delay, window, seq, ack = struct.unpack_from(">BBHIIIHH", packet)
        kind >>= 4
        counts["utp_packets"] += 1
        offset = 20
        valid = True
        while extension:
            if offset + 2 > len(packet):
                valid = False
                break
            next_extension, size = packet[offset:offset + 2]
            if offset + 2 + size > len(packet):
                valid = False
                break
            if extension == 1 and (size < 4 or size % 4):
                counts["noncanonical_sack_length"] += 1
                sack_sizes[f"{src}:{sport} length={size}"] += 1
            extension = next_extension
            offset += 2 + size
        if not valid:
            counts["invalid_extension_headers"] += 1
            continue
        forward = (src, sport, dst, dport)
        reverse = (dst, dport, src, sport)
        key = (*forward, cid)
        if kind == 4 and key not in lookup:
            context = dict(nodes=[f"{src}:{sport}", f"{dst}:{dport}"],
                           directions=[Direction(seq + 1), Direction()], events=[], synFrame=int(frame))
            contexts.append(context)
            lookup[key] = (context, 0)
            lookup[(*forward, (cid + 1) % 65536)] = (context, 0)
            lookup[(*reverse, cid)] = (context, 1)
        if key not in lookup:
            counts["packets_without_captured_syn"] += 1
            continue
        context, side = lookup[key]
        direction = context["directions"][side]
        if kind == 2 and side == 1 and direction.expected is None:
            direction.expected = seq  # STATE carries the next DATA sequence; it does not consume it.
        if kind == 3:
            counts["reset_packets"] += 1
        if kind not in (0, 1):
            continue
        data = packet[offset:]
        if kind == 0 and not data:
            counts["empty_data_packets"] += 1
        for contiguous in direction.accept(seq, data):
            if contiguous:
                context["events"].append((side, contiguous))
    if process.wait() != 0:
        raise RuntimeError("TShark UDP extraction failed")
    streams = []
    for index, context in enumerate(contexts):
        path = args.output_directory / f"{index}.txt"
        with path.open("w", encoding="ascii") as output:
            for side, endpoint in enumerate(context["nodes"]):
                output.write(f"Node {side}: {endpoint}\n")
            for side, data in context["events"]:
                output.write(("\t" if side else "") + data.hex() + "\n")
        directions = []
        for direction in context["directions"]:
            directions.append(dict(counts=dict(direction.counts), bufferedPacketsBeyondGap=len(direction.queued)))
        streams.append(dict(stream=index, nodes=context["nodes"], synFrame=context["synFrame"], directions=directions))
    report = dict(scope="uTP flows with captured SYN; unfilled gaps are not skipped", counts=dict(counts),
                  noncanonicalSackSources=dict(sack_sizes), streams=streams)
    (args.output_directory / "transport-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(counts=dict(counts), streams=len(streams), output=str(args.output_directory)), indent=2))


if __name__ == "__main__":
    main()
