# Future Improvements

Known gaps that remain after the August 2026 cleanup. Completed work and measurement history live in
[`INVESTIGATION_NOTES.md`](INVESTIGATION_NOTES.md).

These are ordered by likely impact. None should be implemented without the evidence or consumer
requirement named in its entry: each remaining change is architectural or has an ambiguous public API.

---

## 1. Conditional protocol extensions: BEP 38 and BEP 45

**Impact: low without a consumer.** These were identified during the BEP audit and remain intentionally
unimplemented:

- **BEP 38** (`similar` / `collections`) helps applications manage related torrents and reuse shared
  files.
- **BEP 45** (multiple-address DHT announce) matters for hosts intentionally announcing several public
  addresses.

**Resume when:** a consumer needs either feature. Its workflow should determine the public API rather
than exposing raw protocol fields speculatively.
