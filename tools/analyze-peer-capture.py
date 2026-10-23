"""Independently check plaintext peer streams reassembled by TShark; Python stdlib only."""
import argparse
import collections
import hashlib
import json
import re
import struct
import subprocess
from pathlib import Path


def bdecode(data, pos=0):
    first = data[pos:pos + 1]
    if first == b"i":
        end = data.index(b"e", pos)
        return int(data[pos + 1:end]), end + 1
    if first in (b"d", b"l"):
        result = {} if first == b"d" else []
        pos += 1
        while data[pos:pos + 1] != b"e":
            value, pos = bdecode(data, pos)
            if isinstance(result, dict):
                item, pos = bdecode(data, pos)
                result[value] = item
            else:
                result.append(value)
        return result, pos + 1
    colon = data.index(b":", pos)
    end = colon + 1 + int(data[pos:colon])
    return data[colon + 1:end], end


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("torrent", type=Path)
    parser.add_argument("--local-address", required=True)
    parser.add_argument("--tshark", default=r"C:\Program Files\Wireshark\tshark.exe")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--reassembled-streams", type=Path,
                        help="Directory of numbered Follow-style plaintext streams, e.g. reassembled uTP")
    args = parser.parse_args()
    metainfo = args.torrent.read_bytes()
    pos = 1
    info = None
    info_hash = None
    while metainfo[pos:pos + 1] != b"e":
        key, pos = bdecode(metainfo, pos)
        start = pos
        value, pos = bdecode(metainfo, pos)
        if key == b"info":
            info = value
            info_hash = hashlib.sha1(metainfo[start:pos]).digest()
    if info is None:
        raise ValueError("Torrent has no info dictionary")
    piece_size = info[b"piece length"]
    total = info.get(b"length") or sum(f[b"length"] for f in info[b"files"])
    hashes = info[b"pieces"]
    piece_count = len(hashes) // 20
    blocks = collections.defaultdict(dict)
    verified = set()
    violations = []
    counts = collections.Counter()
    streams = []

    def issue(kind, stream, **detail):
        counts[kind] += 1
        if len(violations) < 100:
            violations.append(dict(kind=kind, stream=stream, **detail))

    def tshark(*extra):
        return [args.tshark, "-n", "-r", str(args.capture), *extra]

    if args.reassembled_streams:
        ids = sorted(int(path.stem) for path in args.reassembled_streams.glob("*.txt") if path.stem.isdigit())
    else:
        stream_text = subprocess.check_output(tshark("-T", "fields", "-e", "tcp.stream"), text=True)
        ids = sorted({int(s) for s in stream_text.splitlines() if s.strip()})
    for stream in ids:
        buffers = [bytearray(), bytearray()]
        handshakes = [None, None]
        pending = collections.Counter()
        cancelled = collections.Counter()
        replies = collections.Counter()
        allowed_fast = set()
        choked = True
        engine_side = None
        first_engine_message = True
        messages = collections.Counter()
        incomplete = False
        process = None
        if args.reassembled_streams:
            stream_lines = (args.reassembled_streams / f"{stream}.txt").open(encoding="ascii")
        else:
            process = subprocess.Popen(tshark("-q", "-z", f"follow,tcp,raw,{stream}"),
                                       stdout=subprocess.PIPE, text=True)
            stream_lines = process.stdout
        for line in stream_lines:
            if line.startswith("Node "):
                node = int(line[5])
                address = line.split(": ", 1)[1].strip().rsplit(":", 1)[0]
                if address == args.local_address:
                    engine_side = node
                continue
            raw = line.strip()
            if not raw or not re.fullmatch(r"[0-9a-fA-F]+", raw) or len(raw) % 2:
                if "bytes missing" in raw:
                    incomplete = True
                continue
            side = 1 if line.startswith("\t") else 0
            if engine_side is None:
                incomplete = True
                continue
            outgoing = side == engine_side
            buffer = buffers[side]
            buffer.extend(bytes.fromhex(raw))
            if handshakes[side] is None:
                if len(buffer) < 68:
                    continue
                if buffer[:20] != b"\x13BitTorrent protocol":
                    incomplete = True
                    break
                handshakes[side] = bytes(buffer[:68])
                del buffer[:68]
                if handshakes[side][28:48] != info_hash:
                    issue("handshake_infohash_mismatch", stream, outgoing=outgoing)
            while len(buffer) >= 4:
                length = struct.unpack_from(">I", buffer)[0]
                if length > 4 * 1024 * 1024:
                    issue("invalid_frame_length", stream, outgoing=outgoing, length=length)
                    incomplete = True
                    break
                if len(buffer) < 4 + length:
                    break
                body = bytes(buffer[4:4 + length])
                del buffer[:4 + length]
                if not body:
                    continue
                msg, payload = body[0], body[1:]
                messages[("sent_" if outgoing else "received_") + str(msg)] += 1
                fast = all(h is not None and h[27] & 4 for h in handshakes)
                if outgoing:
                    if msg == 5 and not first_engine_message:
                        issue("late_engine_bitfield", stream)
                    first_engine_message = False
                    if 13 <= msg <= 17 and not fast:
                        issue("unnegotiated_engine_fast_message", stream, message=msg)
                    if msg in (6, 8):
                        if len(payload) != 12:
                            issue("invalid_engine_request_shape", stream, message=msg)
                            continue
                        key = struct.unpack(">III", payload)
                        piece, offset, size = key
                        piece_length = min(piece_size, total - piece * piece_size)
                        if piece >= piece_count or size == 0 or size > 16384 or offset + size > piece_length:
                            issue("invalid_engine_request_range", stream, request=key)
                        if msg == 6:
                            counts["requests"] += 1
                            if choked and not (fast and piece in allowed_fast):
                                issue("engine_request_while_choked", stream, request=key)
                            if pending[key]:
                                issue("duplicate_outstanding_engine_request", stream, request=key)
                            pending[key] += 1
                        else:
                            counts["cancels"] += 1
                            if pending[key]:
                                pending[key] -= 1
                            cancelled[key] += 1
                else:
                    if msg == 0:
                        choked = True
                        # Legacy choke implicitly cancels pending requests (BEP 3).
                        if not fast:
                            cancelled.update(pending)
                            pending.clear()
                    elif msg == 1:
                        choked = False
                    elif msg == 17 and len(payload) == 4:
                        allowed_fast.add(struct.unpack(">I", payload)[0])
                    elif msg in (7, 16):
                        if msg == 16 and len(payload) == 12:
                            key = struct.unpack(">III", payload)
                            counts["rejects"] += 1
                        elif msg == 7 and len(payload) >= 8:
                            piece, offset = struct.unpack(">II", payload[:8])
                            data = payload[8:]
                            key = (piece, offset, len(data))
                            counts["piece_responses"] += 1
                            counts["piece_payload_bytes"] += len(data)
                            if piece < piece_count and piece not in verified:
                                previous = blocks[piece].get(offset)
                                if previous is not None and previous != data:
                                    issue("conflicting_peer_block_bytes", stream, piece=piece, offset=offset)
                                blocks[piece][offset] = data
                                expected = min(piece_size, total - piece * piece_size)
                                parts = blocks[piece]
                                position = 0
                                for start in sorted(parts):
                                    if start != position:
                                        break
                                    position += len(parts[start])
                                if position == expected:
                                    digest = hashlib.sha1(b"".join(parts[start] for start in sorted(parts))).digest()
                                    if digest != hashes[piece * 20:piece * 20 + 20]:
                                        issue("captured_piece_hash_mismatch", stream, piece=piece)
                                    else:
                                        verified.add(piece)
                                    del blocks[piece]
                        else:
                            issue("invalid_peer_response_shape", stream, message=msg)
                            continue
                        if pending[key]:
                            pending[key] -= 1
                        elif cancelled[key]:
                            counts["responses_after_cancel_or_choke"] += 1
                            counts[("piece_responses" if msg == 7 else "rejects") + "_after_cancel_or_choke"] += 1
                            cancelled[key] -= 1
                        elif replies[key]:
                            counts["duplicate_peer_responses"] += 1
                        else:
                            issue("unmatched_peer_response", stream, response=key, message=msg)
                        replies[key] += 1
            if incomplete:
                break
        stream_lines.close()
        if process:
            if process.poll() is None and incomplete:
                process.terminate()
            code = process.wait()
            if code and not incomplete:
                raise RuntimeError(f"TShark failed on stream {stream}: {code}")
        streams.append(dict(stream=stream, engineSide=engine_side, completeHandshake=all(handshakes),
                            incomplete=incomplete, trailingBytes=[len(b) for b in buffers],
                            pendingRequests=sum(pending.values()), messages=dict(messages)))
    report = dict(scope="Captured plaintext peer subset; pending requests at shutdown are not a failure",
                  streamSource="reassembled" if args.reassembled_streams else "TCP",
                  streams=streams, counts=dict(counts), capturedWholePiecesVerified=len(verified),
                  verifiedPieceIndexes=sorted(verified), partiallyCapturedPieces=len(blocks),
                  issueSamples=violations)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(streams=len(streams), counts=dict(counts),
                          capturedWholePiecesVerified=len(verified), output=str(args.output)), indent=2))


if __name__ == "__main__":
    main()
