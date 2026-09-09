# uTP loopback throughput investigation

Investigated `main` at `73848a0` on 2026-09-06. Changes are on
`codex/utp-throughput-investigation`, uncommitted.

## Conclusion

Some TCP/uTP difference is expected: this uTP implementation processes Ethernet-sized UDP
datagrams, ACKs, reliability buffers, and congestion control in managed code. Loopback particularly
favors the OS TCP stack. However, the investigation found genuine loss-recovery problems;
the original 4.0 s versus 1.9 s observation should not simply be dismissed as normal.

The remaining loopback gap is substantial. These fixes do not establish WAN throughput or
cross-client interoperability, and do not make uTP as fast as TCP.

## Measurements

Windows, Intel Core Ultra 9 285K (24 logical processors), .NET SDK 10.0.400, Release,
256 MiB, one PeerSharp seed and leecher in one process, actual IPv4 loopback sockets,
256 KiB pieces, encryption refused, discovery/NAT mapping disabled. Seed recheck and
fixture creation are outside timing. The timer starts when the peer endpoint is supplied
and ends when the engine reports completion (10 ms polling). Every completed payload was
independently SHA-256 checked outside timing. One full-size warmup per transport is excluded.

| Metric | Original uTP (5 runs) | Fixed uTP (10 runs) | Original TCP (5 runs) | Fixed TCP (10 runs) |
| --- | ---: | ---: | ---: | ---: |
| Median seconds | 3.108 | 2.679 | 0.348 | 0.340 |
| Range, seconds | 2.746–3.348 | 2.643–4.002 | 0.327–0.350 | 0.319–0.369 |
| Mean process CPU seconds | 20.16 | 18.50 | 2.36 | 2.39 |
| Mean managed allocation, MiB | 994.1 | 910.6 | 363.8 | 364.1 |

The uTP median improved about 14%, with about 8% less CPU time and allocation. These are
local before/after samples, not a statistical guarantee. TCP's timing is different from the
reported 1.9 s because that experiment's exact setup/timing boundaries were not supplied.
Both engines' work is included in process counters; allocation is total allocation, not peak RAM.

Original uTP seconds: `3.3480, 3.3121, 2.8420, 2.7462, 3.1080`.
Fixed uTP seconds: `3.6289, 2.6731, 4.0021, 2.6431, 2.6790, 3.4549, 2.6469, 2.6800, 2.6683, 2.6791`.

Separate diagnostic runs were not mixed into that table. An original-code traced run took
24.56 seconds and logged 74,886 duplicate-ACK retransmissions and four timeouts. After limiting
duplicate-ACK retries, intermediate builds exposed 120-second stalls: the former excessive retries
had masked a second recovery problem. A dump of one stall showed 657,502 bytes charged in flight
against a 4,693-byte congestion window, with hundreds of packets still outstanding.

With ACK-clocked recovery implemented, a ten-run diagnostic series completed in 2.62–2.71 s,
and the final ten-run, logging-disabled series in the table also completed and verified every file.
The final series still has several slow samples; this is not a claim that all latency outliers are gone.

The sampled-thread-time trace includes idle/setup time and is not a precise CPU attribution.
Among active stacks, UDP send/receive and synchronization are prominent. No speculative socket,
MTU, ACK-delay, or congestion-target tuning was retained. A bounded receive-pipe batching
experiment showed no clear improvement and was removed.

## Fixes and regression coverage

- Duplicate STATE ACKs now retransmit only `ack_nr + 1`, once per reported hole, sharing the
  SACK fast-resend boundary. Previously every third duplicate walked the next four unresent
  packets, eventually retransmitting the entire tail without evidence those packets were lost.
- The loss-cut boundary advances with cumulative ACKs. After 32,768 loss-free packets, its
  former fixed 16-bit value compared backwards and could suppress a real congestion cut.
- A timeout marks the existing flight as already charged for congestion, preserving recovery
  slow-start when later SACKs identify additional losses from the same flight.
- Timed-out buffers remain retained for reliability but are queued separately from bytes actually
  in flight. One probe starts recovery, then ACKs release window space for queued retries before
  new writes. Previously only four packets were retried per timer, while the entire old flight
  still consumed the reduced window. Late ACKs/SACKs and repeated timeouts do not double-subtract
  byte accounting; queue tests also cross the 16-bit sequence wrap.
- An active retransmission deadline restarts on ACK progress, not unrelated incoming traffic.
  A first send after idle arms a fresh deadline. This intentionally uses a progress-based retry
  deadline: BEP 29's text describes resetting on any packet, which can indefinitely defer recovery
  when reverse-direction requests continue while outgoing data is lost.

The targeted suite covers duplicate reports, SACK/tail interactions, loss after 40,000 ACKed
packets, deadlines, ACK-driven recovery, late ACKs/SACKs, sequence wrap, and repeated timeouts.
The bug-specific regressions were observed failing before their corresponding fixes.

Protocol references: [BEP 29, packet loss and timeouts](https://www.bittorrent.org/beps/bep_0029.html),
and the local libtorrent reference's `src/utp_stream.cpp` timeout handling, which separates
pending retries from in-flight bytes and records the timed-out flight's loss boundary.

## Reproduce

```powershell
dotnet run -c Release --project benchmarks/PeerSharp.Benchmarks -- --loopback 256 10 both
dotnet test tests/PeerSharp.Tests/PeerSharp.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Core.Utp|FullyQualifiedName~Integration.Utp'
```

The benchmark prints its unique results directory. Payloads are retained, and timeouts/hash
failures fail the command. Do not run builds or other benchmarks concurrently with measurements.
The original diagnostic artifacts remain locally under `artifacts/utp-throughput`; the final
series is under `artifacts/transport-loopback/a7a2df38316a4f6ead31d146097edaea`.
