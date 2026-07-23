namespace PeerSharp.Tests.Concurrency;

/// <summary>
/// Groups every Coyote systematic-testing suite into a single collection that never runs
/// in parallel — neither with another Coyote suite nor with the rest of the test assembly.
///
/// Coyote's <c>TestingEngine</c> installs a process-wide scheduling controller for the
/// duration of a run. If two engines execute concurrently (which xunit's default
/// per-collection parallelism allows), they intercept each other's controlled operations
/// and report spurious "concurrency bugs" that do not exist in the product code. Serialising
/// them via this collection keeps each run isolated. All Coyote-based test classes must be
/// annotated with <c>[Collection("Coyote")]</c>.
/// </summary>
[CollectionDefinition("Coyote", DisableParallelization = true)]
public sealed class CoyoteTestCollection;
