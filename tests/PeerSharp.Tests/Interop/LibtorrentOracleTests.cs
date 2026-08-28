using System.Diagnostics;
using PeerSharp.Tests.Integration.Synthetic;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// The synthetic peer's expectations, checked against libtorrent instead of against PeerSharp.
///
/// <para>
/// The synthetic peer is written independently of PeerSharp, which buys a second opinion but not a
/// correct one. Every assertion it makes is still my reading of a BEP, and if a reading is wrong then
/// PeerSharp is being held to an invented standard: the tests pass, and the engine remains broken
/// against everything else. That is the original failure - agreeing with itself and with nothing else
/// - moved up one level, and no amount of care in writing the peer removes it.
/// </para>
///
/// <para>
/// What removes it is asking something that was not written here. These tests put libtorrent through
/// the same <see cref="ExtensionProtocolConformance"/> assertions, over the same synthetic peer, with
/// the engine swapped. libtorrent passing them means they describe conformant behaviour. libtorrent
/// failing one means the expectation is wrong and the matching PeerSharp test is enforcing an
/// invention - which is a finding about this repository's tests, not about libtorrent.
/// </para>
///
/// <para>
/// Opt-in, excluded from CI, gated on <c>PEERSHARP_INTEROP=1</c> and on the <c>client_test</c> the
/// end-to-end harness builds. They are an oracle consulted occasionally to confirm the fast tests are
/// asking for the right thing, not part of the per-commit suite.
/// </para>
/// </summary>
[Collection("Integration")]
public class LibtorrentOracleTests : IDisposable
{
    private readonly string _path;

    public LibtorrentOracleTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpOracle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// The BEP 21 expectation: does a reference implementation with no metadata really leave
    /// <c>upload_only</c> out of its handshake?
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task LibtorrentAlsoLeavesUploadOnlyOutBeforeItHasMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = 3, ["ut_pex"] = 4 },
            MetadataSize = 32 * 1024
        });

        var connection = await RunLibtorrentAgainstAsync(peer, cancellationToken);
        var handshake = await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(30), cancellationToken);

        ExtensionProtocolConformance.AssertNoUploadOnlyBeforeMetadata(handshake, "libtorrent", isReference: true);
    }

    /// <summary>
    /// The BEP 10 expectation: does a reference implementation really address every extension message
    /// using the ids its peer published, including ones it would never have chosen itself?
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task LibtorrentAlsoAddressesOnlyThePublishedExtensionIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const byte UtMetadataId = 7;
        const byte UtPexId = 9;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = UtMetadataId, ["ut_pex"] = UtPexId },
            MetadataSize = 32 * 1024
        });

        var connection = await RunLibtorrentAgainstAsync(peer, cancellationToken);
        await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(30), cancellationToken);

        bool metadataRequestArrived = await connection.WaitForFrameAsync(
            static frame => frame.IsExtended && frame.ExtendedId == UtMetadataId,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        Assert.True(
            metadataRequestArrived,
            $"libtorrent never sent the metadata request whose extension id this test measures. " +
            $"Traffic: {connection.Describe()}");

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        ExtensionProtocolConformance.AssertOnlyPublishedExtensionIdsAreAddressed(
            connection, [0, UtMetadataId, UtPexId], "libtorrent", isReference: true);
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connection, UtMetadataId, 32 * 1024, "libtorrent", isReference: true);
    }

    /// <summary>BEP 10 id zero disables ut_metadata instead of assigning the handshake id to it.</summary>
    [Fact(Timeout = 180000)]
    public async Task LibtorrentAlsoDoesNotAddressADisabledMetadataExtension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = 0 },
            MetadataSize = 32 * 1024
        });

        var connection = await RunLibtorrentAgainstAsync(peer, cancellationToken);
        await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        ExtensionProtocolConformance.AssertNoMetadataRequests(connection, "libtorrent", isReference: true);
    }

    /// <summary>BEP 10 mappings belong to connections, not to the torrent or process.</summary>
    [Fact(Timeout = 180000)]
    public async Task LibtorrentAlsoKeepsMetadataExtensionIdsPerConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const byte FirstId = 7;
        const byte SecondId = 11;
        const int MetadataSize = 32 * 1024;

        await using var firstPeer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = FirstId },
            MetadataSize = MetadataSize
        });
        await using var secondPeer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = SecondId },
            MetadataSize = MetadataSize
        });

        IReadOnlyList<SyntheticConnection> connections = await RunLibtorrentAgainstAsync(
            [firstPeer, secondPeer], cancellationToken);

        await Task.WhenAll(connections.Select(connection =>
            connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(30), cancellationToken)));

        bool[] requestsArrived = await Task.WhenAll(
            connections[0].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == FirstId,
                TimeSpan.FromSeconds(30), cancellationToken),
            connections[1].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == SecondId,
                TimeSpan.FromSeconds(30), cancellationToken));

        Assert.All(requestsArrived, Assert.True);

        ExtensionProtocolConformance.AssertOnlyPublishedExtensionIdsAreAddressed(
            connections[0], [0, FirstId], "libtorrent", isReference: true);
        ExtensionProtocolConformance.AssertOnlyPublishedExtensionIdsAreAddressed(
            connections[1], [0, SecondId], "libtorrent", isReference: true);
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connections[0], FirstId, MetadataSize, "libtorrent", isReference: true);
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connections[1], SecondId, MetadataSize, "libtorrent", isReference: true);
    }

    /// <summary>A reference implementation accepts and assembles every piece served by the synthetic peer.</summary>
    [Fact(Timeout = 180000)]
    public async Task LibtorrentAlsoCompletesMetadataServedByTheSyntheticPeer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        SyntheticMetadataFixture metadata = SyntheticMetadataFixture.Create();

        const byte UtMetadataId = 7;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_metadata"] = UtMetadataId },
            Metadata = metadata.InfoBytes
        });

        var connection = await RunLibtorrentAgainstAsync(
            peer, cancellationToken, metadata.InfoHashHex);
        await connection.WaitForExtensionHandshakeAsync(TimeSpan.FromSeconds(30), cancellationToken);

        bool allPiecesServed = await SyntheticPeer.WaitForAsync(
            () => connection.ServedMetadataPieces.Distinct().Count() == metadata.MetadataPieceCount,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.True(
            allPiecesServed,
            $"libtorrent did not request every metadata piece. Served pieces: " +
            $"[{string.Join(", ", connection.ServedMetadataPieces)}]. Traffic: {connection.Describe()}");

        bool completed = await SyntheticPeer.WaitForAsync(
            () => connection.Frames.Skip(connection.MetadataResponseFrameBoundary)
                .Any(static frame => frame.Id is 5 or 14 or 15),
            TimeSpan.FromSeconds(30),
            cancellationToken);

        Assert.True(
            completed,
            $"libtorrent received every metadata piece but never advertised a resulting piece map. Served pieces: " +
            $"[{string.Join(", ", connection.ServedMetadataPieces)}]. Traffic: {connection.Describe()}");
        Assert.Equal(
            Enumerable.Range(0, metadata.MetadataPieceCount),
            connection.ServedMetadataPieces.Distinct().Order());
        ExtensionProtocolConformance.AssertValidMetadataRequests(
            connection, UtMetadataId, metadata.InfoBytes.Length, "libtorrent", isReference: true);
    }

    /// <summary>A reference client accepts the same independently encoded PEX and dials its target.</summary>
    [Fact(Timeout = 180000)]
    public async Task LibtorrentAlsoDialsAPeerIntroducedOnlyBySyntheticPex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var target = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var source = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_pex"] = 9 },
            PexAdded = { target.EndPoint }
        });

        await RunLibtorrentAgainstAsync(source, cancellationToken);
        SyntheticConnection introduced = await target.WaitForConnectionAsync(
            0, TimeSpan.FromSeconds(60), cancellationToken);

        Assert.True(introduced.StartedWithPlaintextHandshake);
    }

    /// <summary>A reference client emits the same BEP 11 compact endpoints the shared assertion expects.</summary>
    [Fact(Timeout = 240000)]
    public async Task LibtorrentAlsoIntroducesConnectedPeersUsingEachReceiversExtensionId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const byte FirstPexId = 9;
        const byte SecondPexId = 11;

        await using var first = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_pex"] = FirstPexId }
        });
        await using var second = SyntheticPeer.Start(new SyntheticPeerOptions
        {
            Extensions = { ["ut_pex"] = SecondPexId }
        });

        IReadOnlyList<SyntheticConnection> connections = await RunLibtorrentAgainstAsync(
            [first, second], cancellationToken);

        bool[] pexArrived = await Task.WhenAll(
            connections[0].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == FirstPexId,
                TimeSpan.FromSeconds(90), cancellationToken),
            connections[1].WaitForFrameAsync(
                frame => frame.IsExtended && frame.ExtendedId == SecondPexId,
                TimeSpan.FromSeconds(90), cancellationToken));

        Assert.All(pexArrived, Assert.True);
        ExtensionProtocolConformance.AssertPexIntroduces(
            connections[0], FirstPexId, second.EndPoint, "libtorrent", isReference: true);
        ExtensionProtocolConformance.AssertPexIntroduces(
            connections[1], SecondPexId, first.EndPoint, "libtorrent", isReference: true);
    }

    /// <summary>
    /// Skips unless this is an opt-in interoperability run with libtorrent built.
    /// </summary>
    /// <remarks>
    /// Gated the same way as the rest of this folder rather than on the binary alone. These spawn
    /// external processes and wait up to a minute for a dial, and on a machine that has run the
    /// benchmark harness the binary is simply present - so they were joining every full-suite run,
    /// where they timed out three at a time under load and then passed on their own immediately
    /// after. An oracle worth consulting occasionally is not worth a flaky suite.
    /// </remarks>
    private static void SkipWithoutClientTest()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PEERSHARP_INTEROP")))
        {
            Assert.Skip("Set PEERSHARP_INTEROP=1 to run the libtorrent conformance oracle.");
        }

        if (FindClientTest() is null)
        {
            Assert.Skip(
                "libtorrent's client_test is not built. Run the end-to-end harness build first: " +
                "dotnet run -c Release --project benchmarks/PeerSharp.EndToEnd -- build");
        }
    }

    /// <summary>
    /// Starts <c>client_test</c> on a magnet whose only peers are the supplied synthetic ones, and
    /// waits for it to dial. Each peer address travels in the magnet as <c>x.pe</c>, so nothing is
    /// configured about the connections that the magnet does not say.
    /// </summary>
    private async Task<SyntheticConnection> RunLibtorrentAgainstAsync(
        SyntheticPeer peer, CancellationToken cancellationToken, string? infoHash = null)
    {
        IReadOnlyList<SyntheticConnection> connections = await RunLibtorrentAgainstAsync(
            [peer], cancellationToken, infoHash);
        return connections[0];
    }

    /// <summary>Starts one libtorrent torrent whose explicit peers are the supplied synthetic peers.</summary>
    private async Task<IReadOnlyList<SyntheticConnection>> RunLibtorrentAgainstAsync(
        IReadOnlyList<SyntheticPeer> peers,
        CancellationToken cancellationToken,
        string? infoHash = null)
    {
        SkipWithoutClientTest();

        string clientTest = FindClientTest()
            ?? throw new InvalidOperationException("Unreachable: the launch path skips when this is null.");

        infoHash ??= Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        string explicitPeers = string.Concat(
            peers.Select(peer => $"&x.pe=127.0.0.1:{peer.Port}"));

        var start = new ProcessStartInfo(clientTest)
        {
            WorkingDirectory = _path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (string argument in new[]
        {
            $"magnet:?xt=urn:btih:{infoHash}{explicitPeers}",
            "-k",
            "-s", Path.Combine(_path, "save"),
            "-f", Path.Combine(_path, "events.log"),
            "--listen_interfaces=127.0.0.1:0",
            "--enable_dht=0",
            "--enable_lsd=0",
            "--enable_upnp=0",
            "--enable_natpmp=0",
            // x.pe peers are considered uTP-capable by libtorrent. The synthetic oracle listens on
            // TCP because these assertions inspect the TCP BitTorrent stream, so pin that transport
            // instead of waiting for a uTP dial the peer deliberately cannot accept.
            "--enable_outgoing_utp=0",
            "--allow_multiple_connections_per_ip=1",
            "--alert_mask=error,status,connect,peer",

            // pe_disabled. The synthetic peer speaks no MSE, and an encrypted dial would be refused
            // before any of the extension protocol happened - which is not what these are measuring.
            "--out_enc_policy=2"
        })
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{clientTest}'.");

        _process = process;

        // client_test draws a terminal UI. Keep its drained output so a startup failure says why it
        // exited instead of masquerading as a protocol timeout.
        _standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var deadline = Stopwatch.StartNew();
        while (peers.Any(static peer => peer.ConnectionCount == 0) &&
               deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (process.HasExited)
            {
                string output = await _standardOutput.ConfigureAwait(false);
                string error = await _standardError.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"libtorrent client_test exited with code {process.ExitCode} before dialling every peer. " +
                    $"stdout: {output} stderr: {error}");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return await Task.WhenAll(peers.Select(peer =>
            peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(1), cancellationToken)));
    }

    private Process? _process;
    private Task<string>? _standardOutput;
    private Task<string>? _standardError;

    /// <summary>
    /// Locates the <c>client_test</c> the end-to-end harness builds. Absent is the normal case on a
    /// machine that has not run the harness, and is a skip rather than a failure.
    /// </summary>
    internal static string? FindClientTest()
    {
        string root = Path.Combine(
            RepositoryRoot(), "artifacts", "peersharp-e2e", "libtorrent-build");

        if (!Directory.Exists(root))
        {
            return null;
        }

        string name = OperatingSystem.IsWindows() ? "client_test.exe" : "client_test";
        return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10000);
            }
        }
        catch (InvalidOperationException) { /* Already gone. */ }
        catch (System.ComponentModel.Win32Exception) { /* Already gone. */ }

        _process?.Dispose();

        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
