using PeerSharp.Internals.Framework;
using System.Reflection;
using Xunit.v3;

[assembly: PeerSharp.Tests.DefectFree]

namespace PeerSharp.Tests;

/// <summary>
/// Fails any test during which the engine caught an exception that was its own fault.
/// </summary>
/// <remarks>
/// <para>
/// The engine's loops catch broadly on purpose - a peer hanging up must not stop a transfer - so a
/// null dereference inside one of them is logged and stepped over exactly like a dead peer. Measured
/// before this existed: a <see cref="NullReferenceException"/> thrown from the peer manager's
/// maintenance loop let all fifty-nine integration tests pass while it fired on every tick, and only
/// the unit test calling the broken method directly noticed.
/// </para>
/// <para>
/// Applied to the assembly, so a defect on any path fails the test that provoked it. Nothing about
/// production behaviour changes: the engine still catches, still logs, still carries on. This only
/// decides whether the test suite is allowed to ignore it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DefectFreeAttribute : BeforeAfterTestAttribute, IDefectObserver
{
    private static readonly AsyncLocal<List<string>?> Current = new();
    private static readonly Subscription Registration = new();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        ArgumentNullException.ThrowIfNull(methodUnderTest);

        if (IsExempt(methodUnderTest))
        {
            return;
        }

        Registration.EnsureRegistered(this);
        Current.Value = [];
    }

    /// <summary>
    /// Whether this test reports defects on purpose, which is true only of the tests covering this
    /// mechanism itself.
    /// </summary>
    private static bool IsExempt(MethodInfo method)
    {
        return method.GetCustomAttribute<ReportsDefectsOnPurposeAttribute>() != null
            || method.DeclaringType?.GetCustomAttribute<ReportsDefectsOnPurposeAttribute>() != null;
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        ArgumentNullException.ThrowIfNull(methodUnderTest);

        if (IsExempt(methodUnderTest))
        {
            return;
        }

        var defects = Current.Value;
        Current.Value = null;

        if (defects is { Count: > 0 })
        {
            Assert.Fail(
                "The engine caught an exception that was its own fault. It was logged and stepped over, "
                + "which is why the test would otherwise have passed:\n  " + string.Join("\n  ", defects));
        }
    }

    void IDefectObserver.DefectCaught(Exception exception, string context)
    {
        // Flows to whichever test's async context provoked it. A defect raised on a pool thread with
        // no test context - a background loop outliving the test that started it - is not attributed
        // to whatever happens to be running instead.
        Current.Value?.Add($"{context}: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>Registers the observer once for the lifetime of the test run.</summary>
    private sealed class Subscription
    {
        private readonly Lock _lock = new();
        private IDisposable? _handle;

        public void EnsureRegistered(IDefectObserver observer)
        {
            lock (_lock)
            {
                _handle ??= Defect.Observe(observer);
            }
        }
    }
}

/// <summary>
/// Marks a test that provokes a defect deliberately, so <see cref="DefectFreeAttribute"/> leaves it
/// alone. Only the tests covering the defect mechanism itself should carry this.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReportsDefectsOnPurposeAttribute : Attribute;
