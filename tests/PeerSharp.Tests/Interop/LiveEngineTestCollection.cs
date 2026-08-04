namespace PeerSharp.Tests.Interop;

/// <summary>
/// Groups tests that stand several real engines up on loopback into one collection that never runs
/// in parallel, with each other or with the rest of the assembly.
///
/// <para>
/// These are not unit tests wearing a disguise. Each one starts multiple <c>ClientEngine</c>
/// instances with real sockets, listeners and background loops, and then waits for something to
/// happen between them. What they need is for their timers to fire roughly when asked, which is a
/// claim on the thread pool rather than on CPU - and the assembly runs eight collections at once, any
/// of which may be doing the same thing.
/// </para>
///
/// <para>
/// The symptom is a test that passes alone and every time it is rerun, and fails perhaps one full run
/// in five: TwoLeechersLearnAboutEachOther_OnlyViaTheSeeder did exactly that twice in one afternoon,
/// each time costing a full rerun to establish it was nothing. Its budget is already generous - sixty
/// seconds against a two second PEX interval, some thirty chances - so the answer is not a longer
/// deadline but not competing for the pool in the first place.
/// </para>
/// </summary>
[CollectionDefinition("LiveEngine", DisableParallelization = true)]
public sealed class LiveEngineTestCollection;
