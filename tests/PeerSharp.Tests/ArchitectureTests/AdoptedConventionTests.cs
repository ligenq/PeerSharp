using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// Conventions adopted from Peerfluence, each because the mistake it catches has already been made
/// here or could be.
/// </summary>
public sealed class AdoptedConventionTests
{
    /// <summary>
    /// A torrent carries a v1 and a v2 hash and almost never has both; the missing one is stored as
    /// <see cref="InfoHash.Empty"/>, an ordinary all-zero value that equals itself.
    ///
    /// <para>
    /// So <c>==</c> on the stored hashes says every torrent lacking a v2 hash is every other torrent
    /// lacking one, and a lookup for the empty hash - which forty zero characters parse into -
    /// answers with the first torrent that has no hash of that version. That is not hypothetical
    /// here: it is what "Stop an absent hash from naming whichever torrent also lacks one" fixed.
    /// </para>
    ///
    /// <para>
    /// <c>TorrentIdentity</c> is the one place that knows this. Everywhere else asks it, and this is
    /// the rule that keeps it that way - the fix is one <c>==</c> away from being undone by someone
    /// who has not read that commit.
    /// </para>
    /// </summary>
    [Fact]
    public void NoTorrentIsIdentifiedByComparingHashesDirectly()
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            // The one file allowed to compare them is the one that knows what an empty hash means.
            if (Path.GetFileName(file) == "TorrentIdentity.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains(".Hash ==", StringComparison.Ordinal)
                    || line.Contains(".Hash !=", StringComparison.Ordinal)
                    || line.Contains(".HashV2 ==", StringComparison.Ordinal)
                    || line.Contains(".HashV2 !=", StringComparison.Ordinal))
                {
                    offenders.Add($"  {Path.GetFileName(file)}:{i + 1}  {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} places compare info hashes directly. Ask TorrentIdentity.SameTorrent or "
                + $"ITorrent.HasSameIdentity instead:{Environment.NewLine}"
                + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheHashComparisonRule_IsActuallyReadingTheSource()
    {
        // A guard is worth nothing while it is looking at an empty set, and this one reads files
        // from a path that a project layout change could quietly empty.
        var files = ProductionSourceFiles().ToList();

        Assert.True(files.Count > 100, $"only found {files.Count} source files to scan");
        Assert.Contains(files, file => Path.GetFileName(file) == "TorrentIdentity.cs");
    }

    /// <summary>
    /// Optional arguments after a token force callers to choose between positional arguments in the
    /// wrong conventional order and named arguments for ordinary data. More importantly, APIs that
    /// forward cancellation compose predictably only when the token is always last.
    /// </summary>
    /// <remarks>
    /// The existing <c>CancellationToken_Must_Be_Last_Parameter</c> checks methods. This one also
    /// checks constructors, which take tokens here too and were not being looked at.
    /// </remarks>
    [Fact]
    public void EveryConstructorPutsItsCancellationTokenLast()
    {
        var offenders = ProductionTypes()
            .SelectMany(type => type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(constructor => (Constructor: constructor, Parameters: constructor.GetParameters()))
            .Where(item => item.Parameters.Any(p => p.ParameterType == typeof(CancellationToken)))
            .Where(item => item.Parameters[^1].ParameterType != typeof(CancellationToken))
            .Select(item =>
                $"  {item.Constructor.DeclaringType?.FullName}("
                + string.Join(", ", item.Parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))
                + ")")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} constructors put an argument after their CancellationToken:{Environment.NewLine}"
                + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Everything the session file holds has to survive a round trip. A property the serializer can
    /// write but not read back is state that silently resets on the next start, which is worse than
    /// state that was never persisted at all - the caller sees it work until the process restarts.
    /// </summary>
    [Fact]
    public void EveryPersistedValue_CanBeReadBack()
    {
        var offenders = new List<string>();

        foreach (var type in PersistedTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                {
                    continue;
                }

                // A get-only property is derived from the others: writing it stores a second answer
                // to a question that already has one, read back into nothing.
                if (property.GetMethod != null && property.SetMethod == null)
                {
                    offenders.Add(
                        $"  {type.Name}.{property.Name} is computed and would be written to the session "
                        + "file with nowhere to land; mark it [JsonIgnore] or give it a setter");
                    continue;
                }

                if (property.SetMethod is { IsPublic: false })
                {
                    offenders.Add($"  {type.Name}.{property.Name} is written but cannot be read back");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ThePersistenceRule_IsActuallyLookingAtWhatIsPersisted()
    {
        // The set comes from the JSON source-generation context, so a type serialized without being
        // registered there would slip past this guard - and past the AOT-safe serializer too.
        var names = PersistedTypes().Select(type => type.Name).ToList();

        Assert.Contains(nameof(TorrentStateData), names);
        Assert.Contains(nameof(SavedTorrentOptions), names);
        Assert.Contains(nameof(SavedPeerPreference), names);
        Assert.True(names.Count >= 4, $"only found {names.Count} persisted types: {string.Join(", ", names)}");
    }

    /// <summary>
    /// The core library has to stay runnable with no WebRTC stack: it is what a console seedbox, a
    /// service and every embedding scenario runs on, and RtcForge drags a media stack behind it.
    /// </summary>
    /// <remarks>
    /// Nothing references it today and the project file makes the dependency go the other way, which
    /// is exactly why this needs saying: a single reference added for convenience would not fail
    /// anything until somebody tried to run the core on a platform the stack does not build for.
    /// </remarks>
    [Fact]
    public void TheCoreLibrary_KnowsNothingOfTheWebRtcStack()
    {
        var forbidden = typeof(Torrent).Assembly
            .GetReferencedAssemblies()
            .Where(reference => reference.Name is not null
                && (reference.Name.StartsWith("RtcForge", StringComparison.OrdinalIgnoreCase)
                    || reference.Name.StartsWith("SIPSorcery", StringComparison.OrdinalIgnoreCase)
                    || reference.Name.Contains("WebRtc", StringComparison.OrdinalIgnoreCase)
                    || reference.Name.StartsWith("PeerSharp.WebTorrent", StringComparison.Ordinal)))
            .Select(reference => $"  PeerSharp references {reference.Name}")
            .ToList();

        Assert.True(forbidden.Count == 0, string.Join(Environment.NewLine, forbidden));
    }

    // ------------------------------------------------------------------------------- the sets --

    private static IEnumerable<Type> ProductionTypes()
    {
        return typeof(Torrent).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith("PeerSharp", StringComparison.Ordinal) == true)
            .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null);
    }

    /// <summary>
    /// The types the session file is written from, taken from the JSON source-generation context so
    /// that the list cannot drift from what is actually serialized.
    /// </summary>
    private static IEnumerable<Type> PersistedTypes()
    {
        var context = typeof(Torrent).Assembly.GetType("PeerSharp.Internals.PeerSharpJsonContext");
        Assert.True(context is not null, "PeerSharpJsonContext is gone; this rule needs updating with it.");

        // Read off the generated JsonTypeInfo properties rather than the attributes: the attribute
        // does not expose its type in a way reflection can ask for, and the generated properties are
        // the thing the serializer actually uses.
        var roots = context!
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.PropertyType)
            .Where(type => type.IsGenericType
                && type.GetGenericTypeDefinition().Name.StartsWith("JsonTypeInfo", StringComparison.Ordinal))
            .Select(type => type.GetGenericArguments()[0])
            .ToList();

        // Nested types reached through those roots are written by the same serializer and reset the
        // same way, so they are in scope too.
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(roots);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type) || type.Namespace?.StartsWith("PeerSharp", StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var candidate = property.PropertyType;
                if (candidate.IsGenericType && candidate.GetGenericArguments().Length == 1)
                {
                    candidate = candidate.GetGenericArguments()[0];
                }

                if (candidate is { IsClass: true, IsAbstract: false } && candidate != typeof(string))
                {
                    queue.Enqueue(candidate);
                }
            }
        }

        return seen.Where(type => type.Namespace?.StartsWith("PeerSharp", StringComparison.Ordinal) == true);
    }

    private static IEnumerable<string> ProductionSourceFiles()
    {
        string root = ArchitectureHelper.SourceDirectory;

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
