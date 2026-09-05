using PeerSharp.Internals;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// Requires that code carrying logic has an obvious place for its tests to live, and that every way
/// into that logic is reached by at least one of them.
/// </summary>
/// <remarks>
/// <para>
/// The class rule is a naming convention: <c>RequestScheduler</c> is tested by
/// <c>RequestSchedulerTests</c>. It is worth enforcing because the cost of a missing test is usually
/// not a decision to skip it - it is nobody noticing there was nothing there.
/// </para>
/// <para>
/// The method rule is not a naming convention. It reads the compiled IL and asks what the tests
/// actually reach, so a test may be named after the behaviour it pins down rather than after the
/// method it happens to enter through. See <see cref="CallGraph"/>.
/// </para>
/// </remarks>
public sealed class TestCoverageTests
{
    [Fact]
    public void EveryTypeWithLogic_HasATestClassNamedAfterIt()
    {
        var testTypeNames = TestAssemblyPaths()
            .SelectMany(CallGraph.TypeNames)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ProductionTypes()
            .Where(t => !testTypeNames.Contains(ExpectedTestClassName(t)))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => $"  {t.FullName} has no {ExpectedTestClassName(t)} ({TestableMembers(t).Count} members needing one)")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} types carry logic and have no test class named after them:{Environment.NewLine}"
                + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryPublicOrInternalMember_IsReachedByATest()
    {
        var graph = new CallGraph();
        foreach (var path in ProductionAssemblyPaths().Concat(TestAssemblyPaths()))
        {
            graph.Add(path);
        }

        Assert.True(
            graph.TestEntryPoints.Count > 0,
            "No test methods were found in the compiled assemblies, so nothing could be judged covered.");

        var reached = graph.Reachable(graph.TestEntryPoints);

        // A member that only throws has nothing to test but the throw.
        var onlyThrows = ProductionAssemblyPaths()
            .SelectMany(CallGraph.MethodsThatOnlyThrow)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var type in ProductionTypes())
        {
            foreach (var member in TestableMembers(type))
            {
                if (IsReached(type, member, reached)
                    || onlyThrows.Contains(CallGraph.Key(TypeKey(type), member)))
                {
                    continue;
                }

                missing.Add($"  {type.FullName}.{member}");
            }
        }

        missing.Sort(StringComparer.Ordinal);

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} public or internal members are never reached by a test:{Environment.NewLine}"
                + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Whether a member is entered by any test, directly or through the interface it is called by.
    /// </summary>
    /// <remarks>
    /// Interfaces matter here because much of this library is called by its contract rather than its
    /// concrete type: a test holds an <see cref="IFileTransfer"/> and the call site in the IL names
    /// the interface method, not the implementation behind it.
    /// </remarks>
    private static bool IsReached(Type type, string member, HashSet<string> reached)
    {
        if (reached.Contains(CallGraph.Key(TypeKey(type), member)))
        {
            return true;
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (reached.Contains(CallGraph.Key(TypeKey(contract), member)))
            {
                return true;
            }
        }

        // A base class declaring the member the caller used, for the same reason as interfaces. This
        // is also how an override of a framework member is credited: a test that reads from a
        // UtpStream through Stream names Stream::ReadAsync in the IL.
        for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
        {
            if (reached.Contains(CallGraph.Key(TypeKey(parent), member)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a test class for this type would be called. Reflection spells a generic as
    /// <c>Selection`1</c>, and no class can be named that, so the arity goes.
    /// </summary>
    private static string ExpectedTestClassName(Type type)
    {
        var name = type.Name;
        int arity = name.IndexOf('`', StringComparison.Ordinal);
        return (arity < 0 ? name : name[..arity]) + "Tests";
    }

    private static string TypeKey(Type type)
    {
        var name = type.FullName ?? type.Name;

        // Reflection writes a nested type as Outer+Inner and a generic as Type`1, both of which is
        // what the metadata reader produces too.
        int generic = name.IndexOf('[');
        return generic < 0 ? name : name[..generic];
    }

    // ---------------------------------------------------------------- the surface under test --

    /// <summary>
    /// The production types a test is expected for.
    /// </summary>
    /// <remarks>
    /// The exclusions are all "there is no logic here to test", never "this would be inconvenient
    /// to test". Anything left out because it is awkward belongs in a test with the awkward part
    /// mocked, not in this list.
    /// </remarks>
    private static IEnumerable<Type> ProductionTypes()
    {
        // Ours, rather than everything that ends up in our assemblies: the coverage collector
        // instruments statically on Linux, which writes a tracker type into the assembly on disk,
        // and the rule then asked for tests for a type nobody wrote and nobody ships.
        return ProductionAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(type => type.Namespace?.StartsWith("PeerSharp", StringComparison.Ordinal) == true)
            .Where(type => !type.IsInterface)
            .Where(type => !type.IsEnum)
            .Where(type => !typeof(Delegate).IsAssignableFrom(type))
            .Where(type => !type.IsNested)
            .Where(type => !IsGenerated(type))
            .Where(type => !typeof(Attribute).IsAssignableFrom(type))
            .Where(type => TestableMembers(type).Count > 0);
    }

    private static bool IsGenerated(Type type)
    {
        return type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || type.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), inherit: false);
    }

    /// <summary>
    /// The members of a type that a test would be written against: what it declares itself, public
    /// or internal, that carries logic of its own.
    /// </summary>
    private static List<string> TestableMembers(Type type)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.IsPublic || method.IsAssembly)
            .Where(IsWorthTesting)
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsWorthTesting(MethodInfo method)
    {
        // An auto-property's accessor and a record's generated members hold no logic. A property
        // that computes something is not compiler-generated and does count - GetAdaptivePipelineDepth
        // decides how deep a peer's request queue goes, and that is exactly the sort of thing that
        // should not go untested for being spelled as a property.
        if (method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            return false;
        }

        // Operators, and the accessors of events.
        if (method.IsSpecialName && !method.Name.StartsWith("get_", StringComparison.Ordinal)
                                 && !method.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------------- the assemblies --

    /// <summary>
    /// The core library. <c>PeerSharp.WebTorrent</c> is judged by the same rule in its own test
    /// project, which is the only one that references it.
    /// </summary>
    private static Assembly[] ProductionAssemblies() => [typeof(Torrent).Assembly];

    private static IEnumerable<string> ProductionAssemblyPaths() =>
        ProductionAssemblies().Select(a => a.Location);

    /// <summary>
    /// Both test assemblies, including the WebTorrent one this project cannot reference.
    /// </summary>
    /// <remarks>
    /// It is found on disk rather than referenced: referencing it would drag the WebRTC stack into
    /// this project and offer its tests up for discovery twice. Not finding it fails loudly, because
    /// quietly carrying on would report whatever only it covers as untested.
    /// </remarks>
    private static IEnumerable<string> TestAssemblyPaths()
    {
        yield return typeof(TestCoverageTests).Assembly.Location;

        var webTorrent = FindSiblingTestAssembly("PeerSharp.WebTorrent.Tests");
        Assert.True(
            webTorrent is not null,
            "PeerSharp.WebTorrent.Tests.dll was not found. Build the whole solution before running this test: "
                + "without it, whatever only those tests cover looks untested.");

        yield return webTorrent!;
    }

    private static string? FindSiblingTestAssembly(string projectName)
    {
        // bin/<Configuration>/<TargetFramework> under each project, so the sibling project's copy
        // is at the same depth from the repository root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var tail = Path.Combine(
            "tests",
            projectName,
            "bin",
            directory.Parent?.Name ?? "Debug",
            directory.Name,
            projectName + ".dll");

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, tail);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
