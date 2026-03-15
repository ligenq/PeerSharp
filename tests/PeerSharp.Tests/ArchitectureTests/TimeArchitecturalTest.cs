using System.Text.RegularExpressions;

namespace PeerSharp.Tests.ArchitectureTests;

public class TimeArchitecturalTest
{
    /// <summary>
    /// Architectural test enforcing deterministic and testable handling of time.
    ///
    /// This test ensures that all production code uses <see cref="TimeProvider"/> for
    /// time-dependent behavior instead of accessing system time or timers directly.
    ///
    /// WHY THIS EXISTS
    /// ----------------
    /// Direct use of system time and timers (e.g. DateTime.Now, Task.Delay, Thread.Sleep,
    /// or system timers) makes code:
    ///   - Hard to test deterministically
    ///   - Dependent on wall-clock time
    ///   - Slower and more brittle in unit tests
    ///
    /// <see cref="TimeProvider"/> allows time to be controlled, advanced, or frozen in tests,
    /// enabling fast, reliable, and deterministic test execution.
    ///
    /// WHAT THIS TEST ENFORCES
    /// -----------------------
    /// The following usages are NOT allowed in production code:
    ///
    ///   - DateTime.Now / DateTime.UtcNow
    ///   - DateTimeOffset.Now / DateTimeOffset.UtcNow
    ///   - Thread.Sleep(...)
    ///   - System timers created directly (System.Threading.Timer, System.Timers.Timer)
    ///
    /// Instead, time must be accessed through an injected <see cref="TimeProvider"/> instance.
    ///
    /// TASK.DELAY RULE
    /// ---------------
    /// Task.Delay(...) is allowed ONLY if a TimeProvider instance is involved.
    ///
    /// ? Allowed:
    ///   await Task.Delay(timeout, timeProvider);
    ///
    /// ? Not allowed:
    ///   await Task.Delay(timeout);
    ///
    /// The goal is to ensure delays can be controlled and skipped in tests via
    /// a fake or test-specific TimeProvider.
    ///
    /// SCOPE AND EXCLUSIONS
    /// --------------------
    /// This test scans all C# source files in the main source directory, excluding:
    ///
    ///   - Generated output (bin/, obj/)
    ///   - Test projects and test helpers
    ///   - Utility/helper folders
    ///   - Program.cs (application entry point)
    ///   - The architecture test files themselves
    ///
    /// These exclusions exist to avoid false positives and allow infrastructure or
    /// bootstrapping code to interact with system time when appropriate.
    ///
    /// FAILURE BEHAVIOR
    /// ----------------
    /// When a violation is found, the test fails with:
    ///   - The file path and line number
    ///   - A description of the forbidden API usage
    ///   - Guidance on how to fix the issue
    ///
    /// HOW TO FIX FAILURES
    /// ------------------
    /// 1. Inject TimeProvider into the class (constructor or method parameter)
    /// 2. Replace direct system time usage with TimeProvider APIs:
    ///
    ///    - DateTime.UtcNow          ? timeProvider.GetUtcNow()
    ///    - DateTime.Now             ? timeProvider.GetLocalNow()
    ///    - Task.Delay(timeout)      ? Task.Delay(timeout, timeProvider)
    ///    - System timers            ? timeProvider.CreateTimer(...)
    ///
    /// INTENTIONAL LIMITATIONS
    /// ----------------------
    /// This test uses text-based analysis rather than a full Roslyn analyzer.
    /// While not perfectly precise, it is intentionally simple, fast, and
    /// effective at preventing accidental misuse of system time.
    ///
    /// If this rule becomes too limiting or produces false positives, a Roslyn
    /// analyzer should be considered as a more precise long-term alternative.
    /// </summary>
    [Fact]
    public void Time_Dependent_Logic_Should_Use_timeProvider()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;
        var sourceFiles = ArchitectureHelper.SourceFiles;

        foreach (var file in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);

            if (!ShouldScan(relativePath))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            content = RemoveComments(content);
            content = RemoveStringLiterals(content);

            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // ---- DateTime / DateTimeOffset ----
                if (ContainsAny(line,
                    "DateTime.Now",
                    "DateTime.UtcNow",
                    "DateTimeOffset.Now",
                    "DateTimeOffset.UtcNow",
                    "System.DateTime.Now",
                    "System.DateTime.UtcNow",
                    "System.DateTimeOffset.Now",
                    "System.DateTimeOffset.UtcNow"))
                {
                    // Allow property declarations
                    if (line.Contains("public DateTime") && !line.Contains("="))
                    {
                        continue;
                    }

                    errors.Add($"{relativePath}:{i + 1}: Uses system time. Inject and use TimeProvider instead.");
                }

                // ---- Thread.Sleep ----
                if (line.Contains("Thread.Sleep("))
                {
                    errors.Add($"{relativePath}:{i + 1}: Uses Thread.Sleep. Use Task.Delay via TimeProvider.");
                }

                // ---- Timers ----
                if (ContainsAny(line,
                    "new Timer(",
                    "new System.Threading.Timer(",
                    "new System.Timers.Timer("))
                {
                    errors.Add($"{relativePath}:{i + 1}: Uses system timer. Use TimeProvider.CreateTimer().");
                }

                // ---- Task.Delay ----
                if (line.Contains("Task.Delay("))
                {
                    // Heuristic: allow only if a TimeProvider is clearly involved
                    if (!MentionsTimeProvider(lines, i))
                    {
                        errors.Add(
                            $"{relativePath}:{i + 1}: Task.Delay used without TimeProvider. " +
                            $"Ensure the delay is created by passing a TimeProvider instance to the overloaded Task.Delay method which accepts a TimeProvider. Note: The instance must have the name 'timeProvider' in its name.");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            Assert.Fail(
                $"Time-dependent logic found without TimeProvider:\n" +
                $"Fix: Inject TimeProvider and use it for all time-based behavior.\n\n" +
                string.Join("\n", errors.Take(30)) +
                (errors.Count > 30 ? $"\n...and {errors.Count - 30} more." : string.Empty));
        }
    }

    private static bool ShouldScan(string relativePath)
    {
        return !relativePath.Contains("obj")
            && !relativePath.Contains("bin")
            && !relativePath.Contains("Test")
            && !relativePath.Contains("Utilities")
            && !relativePath.EndsWith("Program.cs")
            && !relativePath.Contains("ArchitectureTests");
    }

    private static bool ContainsAny(string line, params string[] values)
    {
        return values.Any(line.Contains);
    }

    private static string RemoveComments(string text)
    {
        // Remove block comments
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // Remove line comments
        text = Regex.Replace(text, @"//.*", string.Empty);

        return text;
    }

    private static string RemoveStringLiterals(string text)
    {
        // Very simple string literal removal (handles most cases)
        return Regex.Replace(text, "\"(?:\\\\.|[^\"])*\"", "\"\"");
    }

    private static bool MentionsTimeProvider(string[] lines, int index)
    {
        // Check current line and a few lines above for TimeProvider usage
        for (int i = Math.Max(0, index - 3); i <= index; i++)
        {
            if (lines[i].Contains("TimeProvider") || lines[i].Contains("timeProvider") || lines[i].Contains("_timeProvider"))
            {
                return true;
            }
        }

        return false;
    }
}






