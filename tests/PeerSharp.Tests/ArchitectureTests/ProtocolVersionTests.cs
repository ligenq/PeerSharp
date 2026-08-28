using System.Reflection;
using PeerSharp.Internals;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// The version PeerSharp tells the network it is must be the version it actually is.
///
/// <para>
/// <see cref="ProtocolConstants.ClientVersion"/> is the one version string in the library that is not
/// derived from the package. It goes into the BEP 20 peer id every peer sees and into the HTTP user
/// agent every tracker and web seed logs, and nothing compared it to anything - so it sat at "0100"
/// while the package went to 2.0, 3.0 and 4.0, and the 4.0.0 release candidate was still introducing
/// itself to the swarm as version 1.0.
/// </para>
///
/// <para>
/// A release checklist would not have caught it either; it is the kind of thing that is only noticed
/// by whoever reads a packet capture. So the assembly version is the source of truth and this is the
/// comparison, which means bumping one without the other fails here instead of shipping.
/// </para>
/// </summary>
public class ProtocolVersionTests
{
    [Fact]
    public void TheAdvertisedVersionMatchesTheAssemblyVersion()
    {
        Version assembly = typeof(ProtocolConstants).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The assembly has no version to compare against.");

        string expected = FormatForPeerId(assembly);

        Assert.True(
            ProtocolConstants.ClientVersion == expected,
            $"The peer id advertises version '{ProtocolConstants.ClientVersion}' while the assembly is " +
            $"{assembly.Major}.{assembly.Minor}, which is '{expected}'. Every peer, tracker and web seed " +
            "reads this. Update ProtocolConstants.ClientVersion to match the package version.");
    }

    [Fact]
    public void ThePeerIdIsShapedTheWayBep20Requires()
    {
        byte[] peerId = ProtocolConstants.GeneratePeerId();

        Assert.Equal(20, peerId.Length);

        string prefix = System.Text.Encoding.ASCII.GetString(peerId, 0, 8);
        Assert.Equal($"-{ProtocolConstants.ClientId}{ProtocolConstants.ClientVersion}-", prefix);
    }

    [Fact]
    public void TheUserAgentCarriesTheSameVersion()
    {
        // The tracker sees this string as well as the peer id, and a disagreement between the two
        // would be worse than either being stale: it makes the client look like two clients.
        Assert.Contains(
            ProtocolConstants.ClientVersion,
            $"PeerSharp/{ProtocolConstants.ClientVersion}",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders a version the way BEP 20's Azureus style wants it: two digits of major, two of minor.
    /// </summary>
    private static string FormatForPeerId(Version version)
    {
        // Two digits each, so this stops being expressible at major 100. That is a long way off, and
        // an assertion here would fail on the release that reaches it, which is the right time to
        // decide what to do instead.
        return $"{version.Major:D2}{version.Minor:D2}";
    }
}
