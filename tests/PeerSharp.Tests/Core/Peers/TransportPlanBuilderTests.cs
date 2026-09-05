using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class TransportPlanBuilderTests
{
    private static ConnectionSettings DefaultSettings(
        bool tcp = true,
        bool utp = true,
        bool preferUtp = true,
        int preferUtpRatio = 70)
    {
        return new ConnectionSettings
        {
            EnableTcpOut = tcp,
            EnableUtpOut = utp,
            PreferUtp = preferUtp,
            PreferUtpRatioPercent = preferUtpRatio
        };
    }

    private static TransportPlanBuilder.Inputs MakeInputs(
        ConnectionSettings settings,
        bool forceUtp = false,
        bool utpAvailable = true,
        bool utpHinted = false,
        int currentUtpRatio = 0,
        Func<int>? currentUtpRatioFn = null)
    {
        return new TransportPlanBuilder.Inputs(
            settings,
            forceUtp,
            utpAvailable,
            utpHinted,
            currentUtpRatioFn ?? (() => currentUtpRatio));
    }

    [Fact]
    public void Build_BothDisabled_ReturnsEmpty()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(tcp: false, utp: false)));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_ForceUtpAndUtpAllowed_ReturnsOnlyUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), forceUtp: true));
        Assert.Equal([TransportPreference.Utp], plan);
    }

    [Fact]
    public void Build_ForceUtpButUtpUnavailable_ReturnsEmpty()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), forceUtp: true, utpAvailable: false));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_ForceUtpButUtpDisabled_ReturnsEmpty()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(utp: false), forceUtp: true));
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_TcpOnly_ReturnsTcpOnly()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(utp: false)));
        Assert.Equal([TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UtpUnavailable_ReturnsTcpOnly()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), utpAvailable: false));
        Assert.Equal([TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UnknownPeer_LeadsWithUtpWhileBelowTheTargetShare()
    {
        // libtorrent assumes a peer speaks uTP until told otherwise, because LEDBAT only yields to
        // the user's other traffic if the transfer actually runs over it.
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), utpHinted: false, currentUtpRatio: 0));
        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UnknownPeer_LetsTcpLeadOnceTheTargetShareIsMet()
    {
        // The ratio bounds how much this client guesses. Past the target another guess buys no more
        // courtesy and costs a capped attempt, so the unknown peer goes to TCP first.
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 70),
            utpHinted: false,
            currentUtpRatio: 90));
        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
    }

    [Fact]
    public void Build_UtpHintedBelowTargetRatio_StartsWithUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 70),
            utpHinted: true,
            currentUtpRatio: 30));
        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UtpHintedAtOrAboveTargetRatio_StillStartsWithUtp()
    {
        // A hinted peer is not a guess, so the quota does not apply to it. Demoting one to keep the
        // share inside a target would spend a dial arguing with evidence already in hand.
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 70),
            utpHinted: true,
            currentUtpRatio: 90));
        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_UtpHinted_DoesNotEvaluateTheRatioAtAll()
    {
        // Not just the same answer - the question is not asked. Sampling the ratio walks every
        // connected peer, and a hinted peer's plan does not depend on it.
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            utpHinted: true,
            currentUtpRatioFn: () => { calls++; return 100; }));

        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Build_PreferUtpFalse_TcpFirstThenUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(preferUtp: false), utpHinted: true));
        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
    }

    [Fact]
    public void Build_PreferUtpRatioGreaterThan100_ClampsTo100()
    {
        // ratio>=100 should always start with UTP when hinted
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 200),
            utpHinted: true,
            currentUtpRatio: 99));
        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_PreferUtpRatioNegative_ClampsToZero()
    {
        // A target of zero means never guess, so an unknown peer goes to TCP first. A hinted peer is
        // not a guess and is unaffected, which is what the other test above covers.
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: -50),
            utpHinted: false,
            currentUtpRatio: 0));
        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
    }

    [Fact]
    public void Build_DoesNotEvaluateRatio_WhenForceUtp()
    {
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            forceUtp: true,
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Equal([TransportPreference.Utp], plan);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Build_DoesNotEvaluateRatio_WhenBothTransportsDisabled()
    {
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(tcp: false, utp: false),
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Empty(plan);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Build_EvaluatesRatio_OnceWhenPeerNotHinted()
    {
        // The unknown peer is exactly where the quota applies, and sampling it walks every connected
        // peer - so it is asked once and not per transport considered.
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            utpHinted: false,
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Build_DoesNotEvaluateRatio_WhenUtpUnavailable()
    {
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            utpAvailable: false,
            utpHinted: true,
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Equal([TransportPreference.Tcp], plan);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Build_PreferUtpFalse_DoesNotEvaluateRatio()
    {
        // With uTP not preferred the share is nobody's business; TCP leads and uTP is the fallback.
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtp: false),
            utpHinted: true,
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
        Assert.Equal(0, calls);
    }
}
