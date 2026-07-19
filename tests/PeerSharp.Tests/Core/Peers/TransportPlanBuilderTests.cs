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
        bool inWarmup = false,
        int currentUtpRatio = 0,
        Func<int>? currentUtpRatioFn = null)
    {
        return new TransportPlanBuilder.Inputs(
            settings,
            forceUtp,
            utpAvailable,
            utpHinted,
            inWarmup,
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
    public void Build_UnknownPeer_PrefersTcpFirstThenUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(DefaultSettings(), utpHinted: false));
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
    public void Build_UtpHintedAtOrAboveTargetRatio_StartsWithTcp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 70),
            utpHinted: true,
            currentUtpRatio: 90));
        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
    }

    [Fact]
    public void Build_InWarmupAndPeerNotHinted_DropsUtpEvenIfPreferred()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            utpHinted: false,
            inWarmup: true));
        Assert.Equal([TransportPreference.Tcp], plan);
    }

    [Fact]
    public void Build_InWarmupButPeerHinted_KeepsUtp()
    {
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 70),
            utpHinted: true,
            inWarmup: true,
            currentUtpRatio: 0));
        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
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
        // ratio<=0 means we never need to top up uTP, so TCP-first when hinted.
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: -50),
            utpHinted: true,
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
    public void Build_DoesNotEvaluateRatio_WhenPeerNotHinted()
    {
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            utpHinted: false,
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Equal([TransportPreference.Tcp, TransportPreference.Utp], plan);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Build_DoesNotEvaluateRatio_DuringWarmupForUnknownPeer()
    {
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(),
            utpHinted: false,
            inWarmup: true,
            currentUtpRatioFn: () => { calls++; return 0; }));

        Assert.Equal([TransportPreference.Tcp], plan);
        Assert.Equal(0, calls);
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
    public void Build_EvaluatesRatio_OnceWhenPreferUtpAndHinted()
    {
        int calls = 0;
        var plan = TransportPlanBuilder.Build(MakeInputs(
            DefaultSettings(preferUtpRatio: 70),
            utpHinted: true,
            currentUtpRatioFn: () => { calls++; return 30; }));

        Assert.Equal([TransportPreference.Utp, TransportPreference.Tcp], plan);
        Assert.Equal(1, calls);
    }
}
