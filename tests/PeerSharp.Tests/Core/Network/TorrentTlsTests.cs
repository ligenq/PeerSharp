using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Reflection;
using PeerSharp.Internals.Peers;
using Microsoft.Extensions.Logging;
using PeerSharp.Tests.Interop;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.BEncoding;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core.Network;

[Collection("Integration")]
public sealed class TorrentTlsTests
{
    [Theory(Timeout = 30000)]
    [InlineData("admission-failure")]
    [InlineData("non-i2p")]
    [InlineData("authentication-failure")]
    public async Task I2pTlsRefusalDisposesTheTlsWrapperAndItsSamStreamExactlyOnce(string scenario)
    {
        const string name = "i2p-tls-ownership.bin";
        var (ca, certificate) = CreateCertificates(name);
        using (ca) using (certificate)
        using (var credential = TorrentTls.PrepareCertificate(certificate))
        {
            await using var engine = await CreateSslEngineAsync(name, ca, certificate);
            var torrent = Assert.IsType<Torrent>(Assert.Single(engine.GetTorrents()));
            torrent.IsI2p = scenario != "non-i2p";
            var factory = new RejectingPeerFactory();
            typeof(PeerManager).GetField("_peerFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(torrent.PeersInternal, factory);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var sam = new DisposalCountingStream(accepted.GetStream());
            Task handling = (Task)typeof(ClientEngine).GetMethod("HandleIncomingI2pAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(engine, [sam, deadline.Token])!;
            await using var tls = new SslStream(client.GetStream(), false,
                (_, remote, _, _) => remote != null && remote.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData));
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = Convert.ToHexStringLower(torrent.Hash.Span),
                EnabledSslProtocols = SslProtocols.Tls12
            };
            if (scenario != "authentication-failure")
            {
                options.ClientCertificates = new X509CertificateCollection { credential };
                options.LocalCertificateSelectionCallback = (_, _, _, _, _) => credential;
            }
            Exception? authError = await Record.ExceptionAsync(() => tls.AuthenticateAsClientAsync(options, deadline.Token));
            await handling.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            if (scenario == "authentication-failure" && authError == null)
                authError = await Record.ExceptionAsync(async () => await tls.ReadExactlyAsync(new byte[1], deadline.Token));
            if (scenario == "authentication-failure") Assert.NotNull(authError);
            else Assert.Null(authError);
            if (scenario == "admission-failure")
            {
                var serverTls = Assert.IsType<SslStream>(factory.Captured);
                Assert.False(serverTls.IsAuthenticated, "The rejected TLS wrapper still owns its authentication state.");
            }
            else Assert.Null(factory.Captured);
            Assert.Equal(1, sam.Disposals);
            Assert.Equal(0, torrent.Peers.ConnectedCount);
        }
    }

    private sealed class RejectingPeerFactory : IPeerCommunicationFactory
    {
        public Stream? Captured { get; private set; }
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, Stream stream, IPEndPoint? endpoint = null)
        {
            Captured = stream;
            throw new InvalidOperationException("Injected peer admission failure.");
        }
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider) => throw new NotSupportedException();
        public PeerCommunication Create(Torrent torrent, IPeerListener listener, TimeProvider timeProvider, TcpClient client) => throw new NotSupportedException();
    }

    private sealed class DisposalCountingStream(Stream inner) : Stream
    {
        public int Disposals { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static (X509Certificate2 Ca, X509Certificate2 Peer) CreateCertificates(string name, string? purpose = null)
    {
        using var issuerKey = RSA.Create(2048);
        var issuer = new CertificateRequest("CN=PeerSharp Test CA", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuer.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var ca = issuer.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return (ca, IssuePeerCertificate(ca, name, [name], purpose));
    }

    private static X509Certificate2 IssuePeerCertificate(X509Certificate2 ca, string commonName, string[]? dnsNames, string? purpose = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        if (dnsNames != null)
        {
            var names = new SubjectAlternativeNameBuilder();
            foreach (string name in dnsNames) names.AddDnsName(name);
            request.CertificateExtensions.Add(names.Build());
        }
        if (purpose != null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(purpose) }, true));
        using var signed = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    [Fact]
    public async Task CertificatesMustMatchBothTorrentAuthorityAndName()
    {
        var (ca, peer) = CreateCertificates("test.bin");
        var (otherCa, otherPeer) = CreateCertificates("test.bin");
        using (ca) using (peer) using (otherCa) using (otherPeer)
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = "test.bin";
            metadata.Info.SslCertificateAuthority = ca.ExportCertificatePem();
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
            Assert.True(TorrentTls.ValidateCertificate(torrent, peer, null));
            Assert.False(TorrentTls.ValidateCertificate(torrent, otherPeer, null));
            Assert.False(TorrentTls.ValidateCertificate(torrent, null, null));
            Assert.False(TorrentTls.MatchesTorrentName(peer, "other.bin"));
            metadata.Info.Name = "other.bin";
            Assert.False(TorrentTls.ValidateCertificate(torrent, peer, null));
        }
    }

    [Fact]
    public async Task ACodeSigningCertificateCannotAuthenticateAsATorrentPeer()
    {
        var (ca, peer) = CreateCertificates("test.bin", "1.3.6.1.5.5.7.3.3");
        using (ca) using (peer)
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = "test.bin";
            metadata.Info.SslCertificateAuthority = ca.ExportCertificatePem();
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
            Assert.False(TorrentTls.ValidateCertificate(torrent, peer, null));
            Assert.False(TorrentTls.ValidateCertificate(torrent, peer, null, serverCertificate: true));
        }
    }

    [Theory(Timeout = 120000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutualTlsTorrentDownloadsOverTcpAndUtp(bool tcp)
    {
        const string name = "tls-payload.bin";
        var (ca, peer) = CreateCertificates(name);
        using (ca) using (peer)
        {
            string root = Path.Combine(Path.GetTempPath(), "peersharp-tls-" + Guid.NewGuid().ToString("N"));
            string seedPath = Path.Combine(root, "seed"); Directory.CreateDirectory(seedPath);
            byte[] payload = RandomNumberGenerator.GetBytes(65536 + 37);
            await File.WriteAllBytesAsync(Path.Combine(seedPath, name), payload, TestContext.Current.CancellationToken);
            using var logs = new TimestampedFileLoggerProvider(Path.Combine(root, "tls.log"));
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            var original = new ApiTorrentFileBuilder().WithName(name).WithPieceLength(16384).AddFile(name, payload).Build();
            var dict = (BDict)BencodeParser.Parse(original.RawData.ToArray());
            ((BDict)dict.Get("info")!).Dict["ssl-cert"] = new BString(System.Text.Encoding.UTF8.GetBytes(ca.ExportCertificatePem()));
            var file = PeerSharp.Core.TorrentFile.Parse(BencodeWriter.Write(dict));
            Assert.True(file.IsSsl);
            await using var seedEngine = ClientEngine.Create(new TorrentClientOptions { Settings = Settings(seedPath, tcp), LoggerFactory = loggerFactory });
            await seedEngine.InitializeAsync();
            var seed = await seedEngine.AddTorrentAsync(file, new AddTorrentOptions { SslCertificate = peer, StartImmediately = false });
            Assert.Equal(file.PieceCount, await seed.ForceRecheckAsync()); await seed.StartAsync();
            await using var leechEngine = ClientEngine.Create(new TorrentClientOptions { Settings = Settings(Path.Combine(root, "leech"), tcp), LoggerFactory = loggerFactory });
            await leechEngine.InitializeAsync();
            var leech = await leechEngine.AddTorrentAsync(file, new AddTorrentOptions { SslCertificate = peer });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(80));
            while (!leech.Finished)
            {
                leechEngine.OnPeersFound(leech.Hash, [new IPEndPoint(IPAddress.Loopback, tcp ? seedEngine.BoundTcpPort : seedEngine.BoundUdpPort)]);
                await Task.Delay(100, deadline.Token);
            }
            var incoming = Assert.Single(seed.Peers.GetConnectedPeers());
            Assert.True(incoming.IsEncrypted);
            Assert.Equal(!tcp, incoming.IsUtp);
            await leech.StopAsync();
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(root, "leech", name), deadline.Token));
        }
    }

    [Theory]
    [InlineData("test.bin", "other.bin", false)] // A matching CN cannot override a conflicting DNS SAN.
    [InlineData("other.bin", "test.bin", true)]
    [InlineData("test.bin", null, true)]
    [InlineData("other.bin", null, false)]
    [InlineData("other.bin", "*", true)]
    [InlineData("test.bin", "TEST.BIN", false)] // Torrent authorization uses exact name matching.
    public async Task TorrentCertificateNameAuthorizationHonorsSanPrecedenceAndCnFallback(string cn, string? san, bool accepted)
    {
        var (ca, original) = CreateCertificates("test.bin");
        using (ca) using (original)
        using (var certificate = IssuePeerCertificate(ca, cn, san == null ? null : [san]))
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = "test.bin";
            metadata.Info.SslCertificateAuthority = ca.ExportCertificatePem();
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
            Assert.Equal(accepted, TorrentTls.ValidateCertificate(torrent, certificate, null));
        }
    }

    [Theory(Timeout = 30000)]
    [InlineData("valid")]
    [InlineData("missing-certificate")]
    [InlineData("wrong-authority")]
    [InlineData("wrong-name")]
    [InlineData("unknown-sni")]
    [InlineData("invalid-sni")]
    public async Task IncomingTlsAuthenticatesTheCertificateAndSniBeforeAdmittingAPeer(string scenario)
    {
        const string name = "tls-admission.bin";
        var (ca, peer) = CreateCertificates(name);
        var (otherCa, otherPeer) = CreateCertificates(name);
        using (ca) using (peer) using (otherCa) using (otherPeer)
        using (var wrongName = IssuePeerCertificate(ca, "another.bin", ["another.bin"]))
        {
            await using var engine = await CreateSslEngineAsync(name, ca, peer);
            var torrent = Assert.IsType<Torrent>(Assert.Single(engine.GetTorrents()));
            using var client = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(IPAddress.Loopback, engine.BoundTcpPort, deadline.Token);
            // Pin the test server certificate so failures measure its client authentication.
            await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate != null && certificate.GetRawCertData().AsSpan().SequenceEqual(peer.RawData));
            using var credential = TorrentTls.PrepareCertificate(scenario switch
            {
                "wrong-authority" => otherPeer,
                "wrong-name" => wrongName,
                _ => peer
            });
            string sni = scenario switch
            {
                "unknown-sni" => Convert.ToHexStringLower(SHA1.HashData("unregistered torrent"u8)),
                "invalid-sni" => "not-an-info-hash",
                _ => Convert.ToHexStringLower(torrent.Hash.Span)
            };
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                // TLS 1.2 reports the certificate rejection during authentication rather than
                // deferring the peer's alert until an application read, as TLS 1.3 may do.
                EnabledSslProtocols = SslProtocols.Tls12
            };
            if (scenario != "missing-certificate")
            {
                options.ClientCertificates = new X509CertificateCollection { credential };
                options.LocalCertificateSelectionCallback = (_, _, _, _, _) => credential;
            }
            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                await tls.AuthenticateAsClientAsync(options, deadline.Token);
                await tls.WriteAsync(PeerHandshake.Create(torrent.InfoFile.Info, RandomNumberGenerator.GetBytes(20)), deadline.Token);
                byte[] reply = new byte[68];
                await tls.ReadExactlyAsync(reply, deadline.Token);
                Assert.True(PeerHandshake.TryParse(reply, torrent.InfoFile.Info, out _));
            });
            if (scenario == "valid")
            {
                Assert.Null(failure);
                await TorrentTestUtility.WaitUntilAsync(() => torrent.Peers.ConnectedCount == 1);
                Assert.True(Assert.Single(torrent.Peers.GetConnectedPeers()).IsEncrypted);
            }
            else
            {
                Assert.NotNull(failure);
                Assert.True(failure is AuthenticationException or IOException, failure.ToString());
                Assert.Equal(0, torrent.Peers.ConnectedCount);
            }
        }
    }

    [Fact(Timeout = 15000)]
    public async Task AnOrdinaryBitTorrentHandshakeCannotBypassAnSslTorrentsAuthentication()
    {
        const string name = "tls-required.bin";
        var (ca, peer) = CreateCertificates(name);
        using (ca) using (peer)
        {
            await using var engine = await CreateSslEngineAsync(name, ca, peer);
            var torrent = Assert.IsType<Torrent>(Assert.Single(engine.GetTorrents()));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, engine.BoundTcpPort, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(PeerHandshake.Create(torrent.InfoFile.Info, RandomNumberGenerator.GetBytes(20)), deadline.Token);
            // Closing the socket, rather than responding with a BT handshake, is the refusal.
            byte[] response = new byte[68];
            int received;
            try { received = await stream.ReadAsync(response, deadline.Token); }
            catch (IOException) { received = 0; }
            Assert.Equal(0, received);
            Assert.Equal(0, torrent.Peers.ConnectedCount);
        }
    }

    [Theory(Timeout = 30000)]
    [InlineData("valid")]
    [InlineData("wrong-authority")]
    [InlineData("wrong-name")]
    public async Task OutgoingTlsAuthenticatesTheServersAuthorityAndTorrentName(string scenario)
    {
        const string name = "outgoing-tls.bin";
        var (ca, peer) = CreateCertificates(name);
        var (otherCa, otherPeer) = CreateCertificates(name);
        using (ca) using (peer) using (otherCa) using (otherPeer)
        using (var wrongName = IssuePeerCertificate(ca, "another.bin", ["another.bin"]))
        using (var credential = TorrentTls.PrepareCertificate(scenario switch
        {
            "wrong-authority" => otherPeer,
            "wrong-name" => wrongName,
            _ => peer
        }))
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = name;
            metadata.Info.Hash = InfoHash.CreateRandom();
            metadata.Info.SslCertificateAuthority = ca.ExportCertificatePem();
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
            torrent.SslCertificate = peer;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Exception?> server = Record.ExceptionAsync(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var tls = new SslStream(accepted.GetStream(), false,
                    (_, certificate, _, _) => certificate != null && certificate.GetRawCertData().AsSpan().SequenceEqual(peer.RawData));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = credential,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12
                }, deadline.Token);
                await tls.WriteAsync(new byte[] { 42 }, deadline.Token);
                await releaseServer.Task.WaitAsync(deadline.Token);
            }).AsTask();
            Exception? failure;
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
                failure = await Record.ExceptionAsync(async () =>
                {
                    await using var tls = await TorrentTls.ConnectAsync(client.GetStream(), torrent, deadline.Token);
                    Assert.True(tls.IsMutuallyAuthenticated);
                    byte[] data = new byte[1];
                    await tls.ReadExactlyAsync(data, deadline.Token);
                    Assert.Equal(42, data[0]);
                });
            }
            finally { releaseServer.TrySetResult(); }
            Exception? serverFailure = await server;
            if (scenario == "valid")
            {
                Assert.Null(failure);
                Assert.Null(serverFailure);
            }
            else
            {
                Assert.NotNull(failure);
                Assert.True(failure is AuthenticationException or IOException, failure.ToString());
            }
        }
    }

    private static async Task<ClientEngine> CreateSslEngineAsync(string name, X509Certificate2 ca, X509Certificate2 peer)
    {
        var original = new ApiTorrentFileBuilder().WithName(name).WithPieceLength(16384).AddFile(name, new byte[1000]).Build();
        var root = (BDict)BencodeParser.Parse(original.RawData.ToArray());
        ((BDict)root.Get("info")!).Dict["ssl-cert"] = new BString(System.Text.Encoding.UTF8.GetBytes(ca.ExportCertificatePem()));
        var file = PeerSharp.Core.TorrentFile.Parse(BencodeWriter.Write(root));
        var settings = Settings(Path.Combine(Path.GetTempPath(), "peersharp-tls-admission-" + Guid.NewGuid().ToString("N")), tcp: true);
        settings.Connection.Encryption = Encryption.Allow; // A normal torrent would allow plaintext here.
        var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
        try
        {
            await engine.InitializeAsync();
            await engine.AddTorrentAsync(file, new AddTorrentOptions { SslCertificate = peer });
            return engine;
        }
        catch { await engine.DisposeAsync(); throw; }
    }

    private static Settings Settings(string path, bool tcp) => new()
    {
        Files = { DefaultDownloadPath = path },
        Dht = { Enabled = false },
        Connection = { TcpPort = 0, UdpPort = 0, EnableTcpIn = tcp, EnableTcpOut = tcp,
            EnableUtpIn = !tcp, EnableUtpOut = !tcp, PreferUtp = !tcp, Encryption = Encryption.Require,
            EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false }
    };
}
