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
            .Where(t => !TypesWithoutATestClass.Contains(TypeKey(t)))
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
                    || onlyThrows.Contains(CallGraph.Key(TypeKey(type), member))
                    || MembersNoTestReaches.Contains($"{TypeKey(type)}.{member}"))
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
    /// Keeps the debt lists honest: an entry that is no longer a gap has to be deleted.
    /// </summary>
    /// <remarks>
    /// Without this the lists only ever grow, and a rule whose exception list grows is a rule that
    /// has been switched off slowly. Failing here is the good outcome - something got covered, and
    /// the line recording that it was not needs to go.
    /// </remarks>
    [Fact]
    public void TheKnownGaps_AreStillGaps()
    {
        var testTypeNames = TestAssemblyPaths()
            .SelectMany(CallGraph.TypeNames)
            .ToHashSet(StringComparer.Ordinal);

        var graph = new CallGraph();
        foreach (var path in ProductionAssemblyPaths().Concat(TestAssemblyPaths()))
        {
            graph.Add(path);
        }

        var reached = graph.Reachable(graph.TestEntryPoints);
        var production = ProductionTypes().ToDictionary(TypeKey, type => type, StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (var entry in TypesWithoutATestClass)
        {
            if (!production.TryGetValue(entry, out var type))
            {
                stale.Add($"  {entry} no longer exists or no longer carries logic");
            }
            else if (testTypeNames.Contains(ExpectedTestClassName(type)))
            {
                stale.Add($"  {entry} now has {ExpectedTestClassName(type)}");
            }
        }

        foreach (var entry in MembersNoTestReaches)
        {
            int split = entry.LastIndexOf('.');
            var typeName = entry[..split];
            var member = entry[(split + 1)..];

            if (!production.TryGetValue(typeName, out var type) || !TestableMembers(type).Contains(member))
            {
                stale.Add($"  {entry} no longer exists or no longer needs a test");
            }
            else if (IsReached(type, member, reached))
            {
                stale.Add($"  {entry} is now reached by a test");
            }
        }

        stale.Sort(StringComparer.Ordinal);

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} entries in the known-gap lists are no longer gaps. Delete them:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, stale));
    }

    // ------------------------------------------------------------------------- the known gaps --

    /// <summary>
    /// Types carrying logic that had no test class when these rules were adopted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Debt, not exclusions. Every entry here is a test somebody still owes; nothing on this list is
    /// here because there is nothing to test, and nothing should be added to it. It exists because
    /// the rules were adopted onto a codebase that predates them, and a rule that cannot be turned
    /// on until the backlog is cleared is a rule that never gets turned on - meanwhile every type
    /// written from now on is covered from its first commit.
    /// </para>
    /// <para>
    /// <see cref="TheKnownGaps_AreStillGaps"/> makes the list one-way: the moment one of these is
    /// covered, the entry has to be deleted, so the list can only shrink.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> TypesWithoutATestClass = new(StringComparer.Ordinal)
    {
        "PeerSharp.Internals.NullAlertsManager",
        "PeerSharp.Internals.TorrentServices",
        "PeerSharp.Internals.TorrentWebSeeds",
        "PeerSharp.Internals.Trackers.TrackerBase",
        "PeerSharp.Internals.Utilities.NatPmpPortMapping",
        "PeerSharp.Internals.Utilities.UpnpDiscovery",
        "PeerSharp.Internals.Utilities.UpnpPortMapping",
        "PeerSharp.Internals.Utp.Utils",
        "PeerSharp.Internals.Utp.UtpManager",
        "PeerSharp.Internals.Utp.UtpStream",
        "PeerSharp.PiecePicking.PeerCommunicationAdapter",
        "PeerSharp.PiecePicking.PiecePickingModule",
        "PeerSharp.PiecePicking.TorrentPiecePickerContext",
        "PeerSharp.PieceWriter.PieceWriterModule",
        "PeerSharp.PieceWriter.SparseFileHelper",
        "PeerSharp.Streaming.HttpStreamRequestHandler",
    };

    /// <summary>
    /// Members no test reached when these rules were adopted. Debt on the same terms as
    /// <see cref="TypesWithoutATestClass"/>.
    /// </summary>
    private static readonly HashSet<string> MembersNoTestReaches = new(StringComparer.Ordinal)
    {
        "PeerSharp.Core.InfoHash.get_Memory",
        "PeerSharp.Core.TorrentFile.LoadAsync",
        "PeerSharp.Core.TorrentFile.get_IsMerkle",
        "PeerSharp.Internals.AlertsManager.GetAlertsAsync",
        "PeerSharp.Internals.ClientEngine.DiscoverInfoHashesAsync",
        "PeerSharp.Internals.ClientEngine.GetMagnetMetadataWithProgressAsync",
        "PeerSharp.Internals.ClientEngine.get_Bandwidth",
        "PeerSharp.Internals.ClientEngine.get_BlocklistEnabled",
        "PeerSharp.Internals.ClientEngine.get_BoundTcpPort",
        "PeerSharp.Internals.ClientEngine.get_GeoIpEnabled",
        "PeerSharp.Internals.ClientEngine.set_BlocklistEnabled",
        "PeerSharp.Internals.ClientEngine.set_GeoIpEnabled",
        "PeerSharp.Internals.Dht.DhtManager.ScrapeInfoHash",
        "PeerSharp.Internals.FileTransfer.DecrementAvailability",
        "PeerSharp.Internals.FileTransfer.InvalidateSelection",
        "PeerSharp.Internals.FileTransfer.PiecesAvailabilityChanged",
        "PeerSharp.Internals.FileTransfer.RefreshSelection",
        "PeerSharp.Internals.Framework.FileSelectionManager.CalculateFinishedSelectedBytes",
        "PeerSharp.Internals.Framework.FileSelectionManager.SetFileSelectionAsync",
        "PeerSharp.Internals.MerkleHashRequestSelection`1.Selected",
        "PeerSharp.Internals.MerkleHashRequestSelection`1.Throttled",
        "PeerSharp.Internals.NullAlertsManager.ConfigAlert",
        "PeerSharp.Internals.NullAlertsManager.GetAlertsAsync",
        "PeerSharp.Internals.NullAlertsManager.get_DroppedAlertCount",
        "PeerSharp.Internals.Peers.PeerManager.AnnounceUploadOnlyAsync",
        "PeerSharp.Internals.Peers.PeerPriority.Compare",
        "PeerSharp.Internals.Torrent.SetAllFilesPriorityAsync",
        "PeerSharp.Internals.Torrent.get_DataDownloaded",
        "PeerSharp.Internals.Torrent.get_DataUploaded",
        "PeerSharp.Internals.Torrent.get_DiskReadLimitBytesPerSecond",
        "PeerSharp.Internals.Torrent.get_DiskWriteLimitBytesPerSecond",
        "PeerSharp.Internals.Torrent.get_HasStreamableFiles",
        "PeerSharp.Internals.Torrent.get_LsdManager",
        "PeerSharp.Internals.Torrent.get_PeerId",
        "PeerSharp.Internals.Torrent.get_StateTimestamp",
        "PeerSharp.Internals.Torrent.get_StreamableFileIndices",
        "PeerSharp.Internals.Torrent.set_DiskReadLimitBytesPerSecond",
        "PeerSharp.Internals.Torrent.set_DiskWriteLimitBytesPerSecond",
        "PeerSharp.Internals.TorrentConfiguration.set_MaxConnections",
        "PeerSharp.Internals.TorrentConfiguration.set_MaxUploadSlots",
        "PeerSharp.Internals.TorrentFileInfo.GetPiecePriority",
        "PeerSharp.Internals.Trackers.TrackerBase.get_IsInitialized",
        "PeerSharp.Internals.Utilities.Field25519.SquareRepeatedly",
        "PeerSharp.PiecePicking.PeerCommunicationAdapter.get_Count",
        "PeerSharp.PiecePicking.PiecePickingModule.CreateChecker",
        "PeerSharp.PiecePicking.PiecePickingModule.CreatePicker",
        "PeerSharp.PiecePicking.TorrentPieceCheckerContext.get_FullSize",
        "PeerSharp.PieceWriter.DiskBandwidthLimiter.AssignBandwidth",
        "PeerSharp.PieceWriter.DiskBandwidthLimiter.get_Name",
        "PeerSharp.PieceWriter.Files.MoveFilesAsync",
        "PeerSharp.PieceWriter.Files.RenameFileAsync",
        "PeerSharp.PieceWriter.Files.get_IsInitialized",
        "PeerSharp.PieceWriter.PathValidator.CreateForTesting",
        "PeerSharp.PieceWriter.PieceWriterModule.CreateFiles",
        "PeerSharp.PieceWriter.Storage.MoveAsync",
        "PeerSharp.PieceWriter.Storage.RenameFileAsync",
        "PeerSharp.Streaming.HttpStreamServer.Start",
        "PeerSharp.Streaming.HttpStreamServer.get_Url",
        "PeerSharp.Streaming.StreamingController.OnStreamDisposed",
    };

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
