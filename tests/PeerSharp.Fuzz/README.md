# PeerSharp fuzz harnesses

The harness has two targets:

- `bencode` exercises `BencodeParser` with arbitrary byte streams.
- `peer-message` exercises repeated `PeerProtocol` decoding and disposes every decoded message.
- `torrent-metadata` exercises `TorrentFileParser.ParseInfoBytes`, which turns a peer's
  `ut_metadata` response into a torrent. That parse necessarily runs before the info hash can
  be checked, so its input is chosen entirely by whoever answered, and its caller catches
  `FormatException` alone - any other exception type is a finding.

CI builds the harness and replays every committed seed with `--self-test`. A weekly workflow runs each target under AFL++ for a bounded period and uploads the findings directory.

On a machine with AFL++ installed, start a local run from the repository root:

```powershell
pwsh tests/PeerSharp.Fuzz/Run-Fuzz.ps1 -Target bencode
pwsh tests/PeerSharp.Fuzz/Run-Fuzz.ps1 -Target peer-message
pwsh tests/PeerSharp.Fuzz/Run-Fuzz.ps1 -Target torrent-metadata
```

The command restores the repository-local SharpFuzz tool, builds the harness, converts the readable seed files to raw AFL inputs, instruments `PeerSharp.dll`, and then runs until interrupted. Output is written below `artifacts/`, which is gitignored.
