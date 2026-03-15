using System.Reflection;

namespace PeerSharp.Tests.ArchitectureTests;

internal static class ArchitectureHelper
{
    public static Assembly CoreAssembly { get; } = typeof(Internals.ClientEngine).Assembly;

    public static string RootNamespace { get; } = CoreAssembly.GetName().Name!;

    public static IEnumerable<Type> AllTypes { get; } = CoreAssembly.GetTypes();

    public static string SourceDirectory { get; } = GetSourceDirectory();

    public static string[] SourceFiles { get; } = Directory.Exists(SourceDirectory)
        ? Directory.GetFiles(SourceDirectory, "*.cs", SearchOption.AllDirectories)
        : Array.Empty<string>();

    private static string GetSourceDirectory()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        // Move up from bin/Debug/net10.0 to project root
        // 1: bin/Debug/net10.0 -> bin/Debug
        // 2: bin/Debug -> bin
        // 3: bin -> PeerSharp.Tests
        // 4: PeerSharp.Tests -> tests
        // 5: tests -> Root
        var root = Path.GetFullPath(Path.Combine(current, "..", "..", "..", "..", ".."));
        var sourceDir = Path.Combine(root, "src", "PeerSharp");

        if (!Directory.Exists(sourceDir))
        {
            // Fallback for cases where the structure might be different
            var search = current;
            while (!string.IsNullOrEmpty(search))
            {
                var candidate = Path.Combine(search, "src", "PeerSharp");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                search = Path.GetDirectoryName(search);
            }

            throw new DirectoryNotFoundException($"Could not find PeerSharp source directory. Tried: {sourceDir}");
        }

        return sourceDir;
    }
}
