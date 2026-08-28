namespace PeerSharp.Tests.Concurrency;

/// <summary>
/// Groups every concurrency-stress suite into a single collection that never runs in parallel -
/// neither with another such suite nor with the rest of the test assembly.
///
/// <para>
/// These tests saturate the thread pool on purpose: each repeats a concurrent scenario many times to
/// give a race a chance to appear. Run alongside each other they compete for the same cores, and what
/// surfaces is scheduling noise rather than a defect - slow tests, spurious timeouts, and failures
/// nobody can reproduce. Serialising them keeps a failure attributable to the code under test.
/// Classes belonging here are annotated <c>[Collection("Concurrency")]</c>.
/// </para>
///
/// <para>
/// The collection predates the removal of Microsoft Coyote, which needed it for a different reason:
/// its testing engine installed a process-wide scheduling controller, so two engines running at once
/// intercepted each other's operations and reported bugs that did not exist. The isolation is still
/// worth having on its own merits.
/// </para>
/// </summary>
[CollectionDefinition("Concurrency", DisableParallelization = true)]
public sealed class ConcurrencyTestCollection;
