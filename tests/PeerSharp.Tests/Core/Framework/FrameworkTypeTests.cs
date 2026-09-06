using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Config;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace PeerSharp.Tests.Core.Framework;

/// <summary>
/// Covers the defect mechanism itself, so it reports defects on purpose and the assembly-wide
/// DefectFree guard has to leave it alone - otherwise every test here would fail on the very thing
/// it is asserting happened.
/// </summary>
[ReportsDefectsOnPurpose]
public class DefectTests
{
    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(DivideByZeroException))]
    [InlineData(typeof(NotImplementedException))]
    [InlineData(typeof(ArrayTypeMismatchException))]
    public void OurOwnMistakesAreDefects(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.True(exception.IsDefect());
    }

    [Theory]
    [InlineData(typeof(FormatException))]          // a malformed torrent or packet
    [InlineData(typeof(InvalidDataException))]     // the same, from a stream
    [InlineData(typeof(ObjectDisposedException))]  // an ordinary shutdown race between loops
    [InlineData(typeof(NotSupportedException))]    // a configuration the engine will not act on
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]
    public void WhatTheNetworkOrADiskHandsUsIsNotADefect(Type exceptionType)
    {
        // The line matters because the loops catch everything: if these counted, a swarm full of
        // dead peers would report a bug in this library on every announce.
        var exception = exceptionType == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("x")
            : (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(exception.IsDefect());
    }

    [Fact]
    public void ADefectIsRecognisedThroughTheWrappersItArrivesInside()
    {
        // A defect thrown inside a task or through reflection reaches the catch block wrapped. If
        // the wrapper hid it, every defect on an async path would read as weather.
        Assert.True(new AggregateException(new NullReferenceException()).IsDefect());
        Assert.True(new TargetInvocationException(new InvalidCastException()).IsDefect());
        Assert.True(new AggregateException(new TargetInvocationException(new KeyNotFoundException())).IsDefect());
    }

    [Fact]
    public void AnAggregateOfSeveralExceptionsIsNotUnwrapped()
    {
        // Only a single-exception aggregate names one culprit. Several could be any mixture, and
        // guessing would report a defect the library may not have.
        var several = new AggregateException(new NullReferenceException(), new SocketException());

        Assert.False(several.IsDefect());
    }

    [Fact]
    public void IsDefect_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ((Exception)null!).IsDefect());
    }

    [Fact]
    public void ReportIfDefect_NotifiesObserversForADefectAndIgnoresEverythingElse()
    {
        var observer = new RecordingObserver();
        using var registration = Defect.Observe(observer);

        Defect.ReportIfDefect(new SocketException(), "network", NullLogger.Instance);
        Assert.Empty(observer.Caught);

        var defect = new NullReferenceException("boom");
        Defect.ReportIfDefect(defect, "peer loop", NullLogger.Instance);

        var (exception, context) = Assert.Single(observer.Caught);
        Assert.Same(defect, exception);
        Assert.Equal("peer loop", context);
    }

    [Fact]
    public void DisposingTheRegistrationStopsTheNotifications()
    {
        // The test suite registers an observer to fail the test that provoked a defect, so an
        // observer that outlived its registration would fail whatever test ran next instead.
        var observer = new RecordingObserver();
        var registration = Defect.Observe(observer);

        registration.Dispose();
        registration.Dispose(); // idempotent

        Defect.ReportIfDefect(new NullReferenceException(), "after", NullLogger.Instance);

        Assert.Empty(observer.Caught);
    }

    [Fact]
    public void EveryRegisteredObserverIsNotified()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        using var a = Defect.Observe(first);
        using var b = Defect.Observe(second);

        Defect.ReportIfDefect(new InvalidCastException(), "both", NullLogger.Instance);

        Assert.Single(first.Caught);
        Assert.Single(second.Caught);
    }

    [Fact]
    public void ReportIfDefect_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => Defect.ReportIfDefect(null!, "c", NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => Defect.ReportIfDefect(new Exception(), "c", null!));
        Assert.Throws<ArgumentNullException>(() => Defect.Observe(null!));
    }

    private sealed class RecordingObserver : IDefectObserver
    {
        public List<(Exception Exception, string Context)> Caught { get; } = [];

        public void DefectCaught(Exception exception, string context) => Caught.Add((exception, context));
    }
}

public class UdpSocketFactoryTests
{
    [Fact]
    public void Create_ByPortBindsAndReportsThatPort()
    {
        var factory = new UdpSocketFactory();

        using var socket = factory.Create(0);

        var local = Assert.IsType<IPEndPoint>(socket.Client.LocalEndPoint);
        Assert.NotEqual(0, local.Port);
    }

    [Fact]
    public void Create_ByFamilyProducesAnUnboundSocketOfThatFamily()
    {
        var factory = new UdpSocketFactory();

        using var v4 = factory.Create(AddressFamily.InterNetwork);
        Assert.Equal(AddressFamily.InterNetwork, v4.Client.AddressFamily);

        if (Socket.OSSupportsIPv6)
        {
            using var v6 = factory.Create(AddressFamily.InterNetworkV6);
            Assert.Equal(AddressFamily.InterNetworkV6, v6.Client.AddressFamily);
        }
    }
}

public class UdpSocketAdapterTests
{
    [Fact]
    public void FromPort_PrefersDualStackSoOneSocketServesBothFamilies()
    {
        // A single dual-mode socket is what lets the DHT and uTP reach IPv4 and IPv6 peers without
        // running two listeners, so this is worth pinning rather than assuming.
        using var socket = UdpSocketAdapter.FromPort(0);

        if (Socket.OSSupportsIPv6)
        {
            Assert.Equal(AddressFamily.InterNetworkV6, socket.Client.AddressFamily);
            Assert.True(socket.Client.DualMode);
        }

        Assert.NotEqual(0, ((IPEndPoint)socket.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task ADatagramSentToTheAdapterComesBackOutOfReceive()
    {
        using var receiver = UdpSocketAdapter.FromPort(0);
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

        using var sender = new UdpSocketAdapter(new UdpClient(AddressFamily.InterNetwork), ownsClient: true);
        byte[] payload = [1, 2, 3, 4];

        var receive = receiver.ReceiveAsync(TestContext.Current.CancellationToken);
        await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

        var result = await receive;
        Assert.Equal(payload, result.Buffer);
    }

    [Fact]
    public async Task ReceiveIsCancellable()
    {
        using var socket = UdpSocketAdapter.FromPort(0);
        using var cts = new CancellationTokenSource();

        var receive = socket.ReceiveAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
    }

    [Fact]
    public void CloseAndDispose_AreBothSafeAndRepeatable()
    {
        var socket = UdpSocketAdapter.FromPort(0);

        socket.Close();
        socket.Dispose();
        socket.Dispose();
    }

    [Fact]
    public void AnAdapterThatDoesNotOwnItsClientLeavesItOpen()
    {
        // NetworkManager hands the same UdpClient to more than one consumer, so a non-owning
        // adapter disposing it would close a socket its siblings are still reading.
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            var adapter = new UdpSocketAdapter(client, ownsClient: false);
            adapter.Dispose();

            // Still usable: binding would throw on a disposed client.
            client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public void JoinMulticastGroup_AcceptsALocalInterfaceWithoutThrowing()
    {
        // Membership and outbound interface are two separate options; the overload sets both. All
        // this asserts is that the pair is accepted on a loopback-bound socket.
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var adapter = new UdpSocketAdapter(client, ownsClient: false);

        try
        {
            adapter.JoinMulticastGroup(IPAddress.Parse("239.192.152.143"), IPAddress.Loopback);
        }
        catch (SocketException)
        {
            // Some hosts refuse a loopback join. The call shape is what is under test.
        }
    }
}

public class TcpListenerAdapterTests
{
    [Fact]
    public void StartMakesTheListenerBindAndReportItsEndpoint()
    {
        using var listener = new TcpListenerAdapter(IPAddress.Loopback, 0, dualMode: false);

        listener.Start();

        var local = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
        Assert.NotEqual(0, local.Port);
        Assert.Equal(IPAddress.Loopback, local.Address);

        listener.Stop();
    }

    [Fact]
    public async Task AcceptReturnsTheConnectionADialerMade()
    {
        using var listener = new TcpListenerAdapter(IPAddress.Loopback, 0, dualMode: false);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint!).Port;

        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        using var dialer = new TcpClient();
        await dialer.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

        using var accepted = await accept;
        Assert.True(accepted.Connected);

        listener.Stop();
    }

    [Fact]
    public async Task AcceptIsCancellable()
    {
        using var listener = new TcpListenerAdapter(IPAddress.Loopback, 0, dualMode: false);
        listener.Start();
        using var cts = new CancellationTokenSource();

        var accept = listener.AcceptTcpClientAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept);
        listener.Stop();
    }

    [Fact]
    public void Dispose_IsSafeAndRepeatable()
    {
        var listener = new TcpListenerAdapter(IPAddress.Loopback, 0, dualMode: false);
        listener.Start();

        listener.Dispose();
        listener.Dispose();
    }

    [Fact]
    public void TheFactoryAsksForDualStackOnlyWhenTheCallerWantsEveryAddress()
    {
        // IPAddress.Any means "everything", and on a host with IPv6 the way to get everything from
        // one socket is a dual-mode IPv6 listener. A specific address means that address only.
        var factory = new TcpListenerFactory();

        using var any = factory.Create(IPAddress.Any, 0);
        any.Start();
        var anyLocal = Assert.IsType<IPEndPoint>(any.LocalEndpoint);
        Assert.Equal(
            Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
            anyLocal.AddressFamily);
        any.Stop();

        using var specific = factory.Create(IPAddress.Loopback, 0);
        specific.Start();
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)specific.LocalEndpoint!).Address);
        specific.Stop();
    }
}

public class PortMapperFactoryTests
{
    [Fact]
    public void NoMappersAreCreatedWhenBothProtocolsAreOff()
    {
        var settings = new Settings();
        settings.Connection.UpnpPortMapping = false;
        settings.Connection.NatPmpPortMapping = false;

        Assert.Empty(new PortMapperFactory().CreateMappers(settings));
    }

    [Fact]
    public void EachEnabledProtocolContributesExactlyOneMapper()
    {
        var settings = new Settings();

        settings.Connection.UpnpPortMapping = true;
        settings.Connection.NatPmpPortMapping = false;
        Assert.IsType<PeerSharp.Internals.Utilities.UpnpPortMapping>(
            Assert.Single(new PortMapperFactory().CreateMappers(settings)));

        settings.Connection.UpnpPortMapping = false;
        settings.Connection.NatPmpPortMapping = true;
        Assert.IsType<PeerSharp.Internals.Utilities.NatPmpPortMapping>(
            Assert.Single(new PortMapperFactory().CreateMappers(settings)));

        settings.Connection.UpnpPortMapping = true;
        Assert.Equal(2, new PortMapperFactory(NullLoggerFactory.Instance).CreateMappers(settings).Count());
    }
}

public class SystemDnsResolverTests
{
    [Fact]
    public async Task AnAddressLiteralResolvesToItself()
    {
        var addresses = await new SystemDnsResolver().GetHostAddressesAsync(
            "127.0.0.1", TestContext.Current.CancellationToken);

        Assert.Contains(IPAddress.Loopback, addresses);
    }

    [Fact]
    public async Task AFailureSurfacesAsSocketExceptionRatherThanAnEmptyList()
    {
        // Callers distinguish "no such host" from "no addresses" - a tracker announce retries on one
        // and gives up on the other - so swallowing this into an empty array would be wrong.
        await Assert.ThrowsAnyAsync<SocketException>(() =>
            new SystemDnsResolver().GetHostAddressesAsync(
                "peersharp-does-not-exist.invalid", TestContext.Current.CancellationToken));
    }
}

public class LifetimeByteTotalsTests
{
    [Fact]
    public void ReadSumsTheLiveTorrentsWithWhatRetiredOnesLeftBehind()
    {
        var live = new List<(long, long)> { (100, 10), (200, 20) };
        var totals = new LifetimeByteTotals(() => live);

        Assert.Equal((300, 30), totals.Read());
    }

    [Fact]
    public void RetiringATorrentKeepsTheTotalFromDipping()
    {
        // The whole reason for the type. A reader between the removal and the increment would see
        // the counter fall, and a metrics backend reads a falling counter as a process restart.
        var live = new List<(long, long)> { (100, 10), (200, 20) };
        var totals = new LifetimeByteTotals(() => live);

        Assert.True(totals.RemoveAndRetire(() => live.Remove((200, 20)), 200, 20));

        Assert.Equal((300, 30), totals.Read());
    }

    [Fact]
    public void ASecondRemovalOfTheSameTorrentAddsNothing()
    {
        // Exactly-once comes from the remove delegate's return value: whoever actually took the
        // torrent out is the one who folds in its bytes.
        var live = new List<(long, long)> { (100, 10) };
        var totals = new LifetimeByteTotals(() => live);

        Assert.True(totals.RemoveAndRetire(() => live.Remove((100, 10)), 100, 10));
        Assert.False(totals.RemoveAndRetire(() => live.Remove((100, 10)), 100, 10));

        Assert.Equal((100, 10), totals.Read());
    }

    [Fact]
    public async Task TheTotalNeverDecreasesWhileTorrentsRetireConcurrently()
    {
        var live = new System.Collections.Concurrent.ConcurrentDictionary<int, (long, long)>();
        for (int i = 0; i < 200; i++) live[i] = (10, 1);

        var totals = new LifetimeByteTotals(() => live.Values);
        long lowest = 0;
        var reader = Task.Run(() =>
        {
            long previous = 0;
            for (int i = 0; i < 2000; i++)
            {
                long current = totals.Read().Downloaded;
                if (current < previous) Interlocked.Exchange(ref lowest, -1);
                previous = current;
            }
        });

        Parallel.For(0, 200, i => totals.RemoveAndRetire(() => live.TryRemove(i, out _), 10, 1));
        await reader;

        Assert.Equal(0, Interlocked.Read(ref lowest));
        Assert.Equal((2000, 200), totals.Read());
    }

    [Fact]
    public void TheConstructorAndRemovalRejectNullDelegates()
    {
        Assert.Throws<ArgumentNullException>(() => new LifetimeByteTotals(null!));
        Assert.Throws<ArgumentNullException>(() => new LifetimeByteTotals(() => []).RemoveAndRetire(null!, 0, 0));
    }
}

public class MerkleHashRequestSelectionTests
{
    [Fact]
    public void NoRequestAndNoPeerCarryNeitherAPeerNorAKey()
    {
        Assert.Equal(MerkleHashRequestSelectionStatus.NoRequest, MerkleHashRequestSelection<object>.NoRequest.Status);
        Assert.Null(MerkleHashRequestSelection<object>.NoRequest.Peer);
        Assert.Null(MerkleHashRequestSelection<object>.NoRequest.RequestKey);

        Assert.Equal(MerkleHashRequestSelectionStatus.NoPeer, MerkleHashRequestSelection<object>.NoPeer.Status);
        Assert.Null(MerkleHashRequestSelection<object>.NoPeer.Peer);
    }

    [Fact]
    public void Selected_CarriesThePeerToAskAndTheKeyToRecordAgainst()
    {
        var peer = new object();

        var selection = MerkleHashRequestSelection<object>.Selected(peer, "piece-3");

        Assert.Equal(MerkleHashRequestSelectionStatus.Selected, selection.Status);
        Assert.Same(peer, selection.Peer);
        Assert.Equal("piece-3", selection.RequestKey);
    }

    [Fact]
    public void Throttled_CarriesTheKeyButNoPeer()
    {
        // The distinction the caller acts on: throttled means the request is already in flight
        // somewhere, so there is a key to wait on but nobody new to ask.
        var selection = MerkleHashRequestSelection<object>.Throttled("piece-3");

        Assert.Equal(MerkleHashRequestSelectionStatus.Throttled, selection.Status);
        Assert.Null(selection.Peer);
        Assert.Equal("piece-3", selection.RequestKey);
    }

    [Fact]
    public void SelectionsWithTheSameContentsAreEqual()
    {
        var peer = new object();

        Assert.Equal(
            MerkleHashRequestSelection<object>.Selected(peer, "k"),
            MerkleHashRequestSelection<object>.Selected(peer, "k"));

        Assert.NotEqual(
            MerkleHashRequestSelection<object>.Selected(peer, "k"),
            MerkleHashRequestSelection<object>.Throttled("k"));
    }
}

public class ProtocolConstantsTests
{
    [Fact]
    public void GeneratePeerId_IsTwentyBytesAndCarriesTheAzureusStyleClientPrefix()
    {
        byte[] id = ProtocolConstants.GeneratePeerId();

        Assert.Equal(20, id.Length);
        Assert.Equal((byte)'-', id[0]);
        Assert.Equal((byte)'-', id[7]);
    }

    [Fact]
    public void GeneratePeerId_ProducesADifferentIdEachTime()
    {
        // The suffix is random per session: two clients sharing a peer id confuse every tracker and
        // peer that keys on it.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 50; i++)
        {
            ids.Add(Convert.ToHexString(ProtocolConstants.GeneratePeerId()));
        }

        Assert.Equal(50, ids.Count);
    }

    [Fact]
    public void GeneratePeerId_KeepsTheSameClientPrefixAcrossCalls()
    {
        byte[] first = ProtocolConstants.GeneratePeerId();
        byte[] second = ProtocolConstants.GeneratePeerId();

        Assert.Equal(first[..8], second[..8]);
    }
}
