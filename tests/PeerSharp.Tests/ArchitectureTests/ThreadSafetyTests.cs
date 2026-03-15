using System.Text.RegularExpressions;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// Architecture tests to enforce Thread-Safe Paradigms and Concurrency Best Practices.
/// </summary>
public class ThreadSafetyTests
{
    private static readonly string SourceDirectory = ArchitectureHelper.SourceDirectory;
    private static readonly string[] SourceFiles = ArchitectureHelper.SourceFiles;

    /// <summary>
    /// Enforces the avoidance of the 'volatile' keyword.
    /// Motivation: 'volatile' is often misunderstood and insufficient for complex thread synchronization.
    /// Use Interlocked.* methods, lock statements, or memory barriers (if absolutely necessary) instead.
    /// Recent refactorings explicitly removed volatile in favor of Interlocked/Channels.
    /// </summary>
    [Fact]
    public void Volatile_Keyword_Should_Not_Be_Used()
    {
        var errors = new List<string>();

        // Regex to find 'volatile' keyword, ensuring it's a whole word and not inside a comment/string (heuristic)
        // Note: Simple regex might have false positives in comments/strings, but we assume code is clean.
        // We look for " volatile " with surrounding whitespace/punctuation.
        var regex = new Regex(@"\bvolatile\b", RegexOptions.Compiled);

        foreach (var file in SourceFiles)
        {
            if (Path.GetFileName(file).Contains("AssemblyInfo"))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var matches = regex.Matches(content);

            foreach (Match match in matches)
            {
                // Simple filter to ignore comments (lines starting with //)
                // This is not a full C# parser but handles single-line comments.
                int lineIndex = content.Substring(0, match.Index).Count(c => c == '\n');
                var line = content.Split('\n')[lineIndex];
                if (line.TrimStart().StartsWith("//"))
                {
                    continue;
                }

                errors.Add($"{Path.GetFileName(file)} (Line {lineIndex + 1}): Usage of 'volatile' keyword detected.");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"'volatile' keyword usage detected (use Interlocked or lock instead):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Enforces the avoidance of synchronous Task waits (.Result, .Wait(), .GetAwaiter().GetResult()).
    /// Motivation: Blocking threads on async tasks can lead to thread pool starvation and deadlocks.
    /// Always use 'await'.
    /// </summary>
    [Fact]
    public void Task_Blocking_Operations_Should_Be_Avoided()
    {
        var errors = new List<string>();

        // Patterns to look for
        var patterns = new[]
        {
            (Pattern: @"\.Result\b", Description: ".Result property"),
            (Pattern: @"\.Wait\(\)", Description: ".Wait() method"),
            (Pattern: @"\.GetAwaiter\(\)\.GetResult\(\)", Description: ".GetAwaiter().GetResult()")
        };

        foreach (var file in SourceFiles)
        {
            // Skip tests/temporary files if they happen to be in the source dir (unlikely for strict structure)
            if (Path.GetFileName(file).Contains("AssemblyInfo"))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//") || line.StartsWith("*"))
                {
                    continue; // Skip basic comments
                }

                // Skip lines explicitly marked as acceptable (e.g., in Dispose patterns)
                if (line.Contains("// Blocking OK") || line.Contains("VSTHRD002"))
                {
                    continue;
                }

                foreach (var (pattern, description) in patterns)
                {
                    if (Regex.IsMatch(line, pattern))
                    {
                        errors.Add($"{Path.GetFileName(file)} (Line {i + 1}): usage of {description} detected.");
                    }
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Synchronous Task waits detected (use 'await' instead):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Enforces the avoidance of Thread.Sleep.
    /// Motivation: Thread.Sleep blocks the current thread. Use Task.Delay for async waiting.
    /// </summary>
    [Fact]
    public void Thread_Sleep_Should_Be_Avoided()
    {
        var errors = new List<string>();

        foreach (var file in SourceFiles)
        {
            var content = File.ReadAllText(file);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//"))
                {
                    continue;
                }

                if (line.Contains("Thread.Sleep"))
                {
                    errors.Add($"{Path.GetFileName(file)} (Line {i + 1}): usage of Thread.Sleep detected.");
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Thread.Sleep detected (use Task.Delay instead):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Enforces the avoidance of 'async void' methods (except event handlers).
    /// Motivation: async void methods cannot be awaited, their exceptions cannot be caught,
    /// and they make control flow unpredictable. Always use 'async Task' instead.
    /// Event handlers are the only valid exception, but this codebase should not use them.
    /// </summary>
    [Fact]
    public void Async_Void_Methods_Should_Be_Avoided()
    {
        var errors = new List<string>();

        // Regex to match 'async void MethodName(' pattern
        // This catches method declarations like: async void Foo(, private async void Bar(, etc.
        var regex = new Regex(@"\basync\s+void\s+\w+\s*\(", RegexOptions.Compiled);

        foreach (var file in SourceFiles)
        {
            if (Path.GetFileName(file).Contains("AssemblyInfo"))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//") || line.StartsWith("*"))
                {
                    continue;
                }

                if (regex.IsMatch(line))
                {
                    errors.Add($"{Path.GetFileName(file)} (Line {i + 1}): 'async void' method detected - use 'async Task' instead.");
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"'async void' methods detected (use 'async Task' instead):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Enforces locking best practices: Avoid lock(this) and lock(typeof(...)).
    /// Motivation: Locking on public objects ('this', types) can cause deadlocks if external code also locks on them.
    /// Always lock on a private, readonly object instance (e.g., _lock = new object()).
    /// </summary>
    [Fact]
    public void Locking_On_This_Or_Type_Should_Be_Avoided()
    {
        var errors = new List<string>();
        var patterns = new[]
        {
            (Pattern: @"lock\s*\(\s*this\s*\)", Description: "lock(this)"),
            (Pattern: @"lock\s*\(\s*typeof\(", Description: "lock(typeof(...))"),
            (Pattern: @"lock\s*\(\s*""[^""]*""\s*\)", Description: "lock(\"string\")") // Lock on string literal
        };

        foreach (var file in SourceFiles)
        {
            var content = File.ReadAllText(file);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//"))
                {
                    continue;
                }

                foreach (var (pattern, description) in patterns)
                {
                    if (Regex.IsMatch(line, pattern))
                    {
                        errors.Add($"{Path.GetFileName(file)} (Line {i + 1}): usage of {description} detected.");
                    }
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Unsafe locking patterns detected (use private readonly object):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }
}






