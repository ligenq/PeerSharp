namespace PeerSharp.Tests.Integration;

/// <summary>
/// Groups the integration suites into a single xunit collection so their test classes run
/// sequentially rather than in parallel.
///
/// Each integration test spins up two full <c>ClientEngine</c> instances (real sockets,
/// timers and peer-message pumps). Running many classes in parallel on a constrained CI
/// runner (e.g. a 2-core GitHub Actions host) starves the thread pool, which stalls the
/// peer message pump and makes timing-sensitive downloads time out spuriously. Serialising
/// the classes keeps at most one pair of engines alive at a time. All integration test
/// classes must be annotated with <c>[Collection("Integration")]</c>.
/// </summary>
[CollectionDefinition("Integration")]
public sealed class IntegrationTestCollection;
