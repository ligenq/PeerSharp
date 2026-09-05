using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Config;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Utp;

/// <summary>
/// The LEDBAT knobs that used to be constants. The point of exposing them is that a caller on a link
/// the defaults were not chosen for can change them; the point of clamping them is that a nonsensical
/// value here does not produce a nonsensical number, it produces a connection that will not yield.
/// </summary>
public class UtpCongestionSettingsTests
{
    [Fact]
    public void Defaults_MatchTheValuesTheseConstantsHeld()
    {
        var settings = new ConnectionSettings();

        Assert.Equal(100000, settings.UtpTargetDelayMicroseconds);
        Assert.Equal(3000, settings.UtpMaxWindowIncreaseBytesPerRtt);
        Assert.Equal(100, settings.UtpWindowDecayIntervalMs);
        Assert.Equal(2, settings.UtpMaxSynRetries);

        var stream = CreateStream(settings);
        Assert.Equal(100000, Knob(stream, "TargetDelay"));
        Assert.Equal(3000, Knob(stream, "MaxCwndIncreaseBytesPerRtt"));
        Assert.Equal(100, Knob(stream, "MaxWindowDecay"));
        Assert.Equal(2, Knob(stream, "MaxSynRetries"));
    }

    [Fact]
    public void Knobs_FollowTheSettingsInstanceWhileTheConnectionIsOpen()
    {
        var settings = new ConnectionSettings();
        var stream = CreateStream(settings);

        settings.UtpTargetDelayMicroseconds = 50000;
        settings.UtpMaxWindowIncreaseBytesPerRtt = 8000;
        settings.UtpWindowDecayIntervalMs = 250;
        settings.UtpMaxSynRetries = 5;

        Assert.Equal(50000, Knob(stream, "TargetDelay"));
        Assert.Equal(8000, Knob(stream, "MaxCwndIncreaseBytesPerRtt"));
        Assert.Equal(250, Knob(stream, "MaxWindowDecay"));
        Assert.Equal(5, Knob(stream, "MaxSynRetries"));
    }

    [Theory]
    [InlineData("UtpTargetDelayMicroseconds", 0, "TargetDelay", 1000)]
    [InlineData("UtpTargetDelayMicroseconds", int.MaxValue, "TargetDelay", 1000000)]
    [InlineData("UtpMaxWindowIncreaseBytesPerRtt", -5, "MaxCwndIncreaseBytesPerRtt", 100)]
    [InlineData("UtpMaxWindowIncreaseBytesPerRtt", int.MaxValue, "MaxCwndIncreaseBytesPerRtt", 1000000)]
    [InlineData("UtpWindowDecayIntervalMs", 0, "MaxWindowDecay", 10)]
    [InlineData("UtpWindowDecayIntervalMs", int.MaxValue, "MaxWindowDecay", 60000)]
    [InlineData("UtpMaxSynRetries", -1, "MaxSynRetries", 0)]
    [InlineData("UtpMaxSynRetries", 1000, "MaxSynRetries", 10)]
    public void Knobs_AreClampedToAUsableRange(string setting, int value, string knob, int expected)
    {
        var settings = new ConnectionSettings();
        typeof(ConnectionSettings).GetProperty(setting)!.SetValue(settings, value);

        Assert.Equal(expected, Knob(CreateStream(settings), knob));
    }

    private static int Knob(UtpStream stream, string name) =>
        (int)typeof(UtpStream)
            .GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(stream)!;

    private static UtpStream CreateStream(ConnectionSettings settings) => new(
        new StubUtpManager(),
        new IPEndPoint(IPAddress.Loopback, 12345),
        idRecv: 100,
        idSend: 101,
        timeProvider: new FakeTimeProvider(),
        loggerFactory: NullLoggerFactory.Instance,
        settings: settings);

    private sealed class StubUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }
        public void CloseStream(UtpStream stream) { }
        public UtpStream CreateStream(IPEndPoint remote) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct) => Task.CompletedTask;
        public void Start(IUdpListener listener) { }
        public void Stop() { }
    }
}
