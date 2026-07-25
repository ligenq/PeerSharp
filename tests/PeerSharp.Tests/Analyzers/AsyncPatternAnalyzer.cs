using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PeerSharp.Tests.Analyzers;

/// <summary>
/// Finds await patterns that are unsafe in a library.
///
/// Both rules exist because both classes of bug have actually shipped here. A missing
/// <c>ConfigureAwait(false)</c> resumes on the caller's <see cref="SynchronizationContext"/>,
/// which deadlocks a UI host that blocks on the returned task. <c>Task.Yield()</c> is the same
/// hazard wearing a disguise: <c>YieldAwaitable</c> has no <c>ConfigureAwait</c> overload at all,
/// so it always posts back to the current context.
///
/// Neither is visible to the test suite, because xUnit runs without a SynchronizationContext -
/// which is exactly why they need to be caught by a rule rather than by a test.
/// </summary>
public sealed class AsyncPatternAnalyzer
{
    /// <summary>A single await that does not configure its continuation.</summary>
    /// <param name="FilePath">Path of the offending file.</param>
    /// <param name="Line">1-based line number.</param>
    /// <param name="Snippet">The offending expression, trimmed for display.</param>
    public record Violation(string FilePath, int Line, string Snippet);

    /// <summary>
    /// Returns every await in <paramref name="sourceFiles"/> whose continuation is not pinned
    /// with <c>ConfigureAwait</c>. Covers plain awaits and <c>await foreach</c>.
    /// </summary>
    /// <remarks>
    /// <c>await using</c> with a variable declaration is deliberately not covered.
    /// <c>ConfigureAwait</c> on an <see cref="IAsyncDisposable"/> returns a
    /// <c>ConfiguredAsyncDisposable</c>, so <c>await using var s = x.ConfigureAwait(false);</c>
    /// changes the type of <c>s</c> and the variable becomes unusable. There is no way to
    /// satisfy the rule there short of restructuring the block, so flagging it would only teach
    /// people to suppress the rule.
    /// </remarks>
    public IReadOnlyList<Violation> FindMissingConfigureAwait(IEnumerable<string> sourceFiles)
    {
        var violations = new List<Violation>();

        foreach (var (filePath, root) in Parse(sourceFiles))
        {
            foreach (var node in root.DescendantNodes())
            {
                var (expression, keyword) = node switch
                {
                    AwaitExpressionSyntax await => (await.Expression, await.AwaitKeyword),
                    CommonForEachStatementSyntax forEach when forEach.AwaitKeyword.RawKind != 0
                        => (forEach.Expression, forEach.AwaitKeyword),
                    // Only the expression form of `await using`; see the remarks above for why
                    // the declaration form cannot satisfy this rule.
                    UsingStatementSyntax @using when @using.AwaitKeyword.RawKind != 0 && @using.Expression is not null
                        => (@using.Expression, @using.AwaitKeyword),
                    _ => (null, default)
                };

                if (expression is null || ConfiguresAwait(expression))
                {
                    continue;
                }

                violations.Add(new Violation(
                    filePath,
                    root.SyntaxTree.GetLineSpan(keyword.Span).StartLinePosition.Line + 1,
                    Trim(expression.ToString())));
            }
        }

        return violations;
    }

    /// <summary>
    /// Returns every <c>Task.Yield()</c> call in <paramref name="sourceFiles"/>. Getting off the
    /// caller's stack is legitimate; the fix is <c>Task.Run</c>, which schedules on
    /// <c>TaskScheduler.Default</c> instead of the ambient context.
    /// </summary>
    public IReadOnlyList<Violation> FindTaskYield(IEnumerable<string> sourceFiles)
    {
        var violations = new List<Violation>();

        foreach (var (filePath, root) in Parse(sourceFiles))
        {
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                    member.Name.Identifier.ValueText != "Yield" ||
                    member.Expression.ToString() is not ("Task" or "System.Threading.Tasks.Task"))
                {
                    continue;
                }

                violations.Add(new Violation(
                    filePath,
                    root.SyntaxTree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1,
                    Trim(invocation.ToString())));
            }
        }

        return violations;
    }

    private static IEnumerable<(string FilePath, CompilationUnitSyntax Root)> Parse(IEnumerable<string> sourceFiles)
    {
        foreach (var filePath in sourceFiles)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), path: filePath);
            yield return (filePath, (CompilationUnitSyntax)tree.GetRoot());
        }
    }

    /// <summary>
    /// True when the expression ends in a ConfigureAwait call. Walks the invocation chain rather
    /// than matching the outermost call, so forms like
    /// <c>x.WaitAsync(t).ConfigureAwait(false)</c> and
    /// <c>foo.WithCancellation(t).ConfigureAwait(false)</c> both satisfy the rule.
    /// </summary>
    private static bool ConfiguresAwait(SyntaxNode expression)
    {
        return expression is InvocationExpressionSyntax invocation &&
               invocation.Expression is MemberAccessExpressionSyntax member &&
               member.Name.Identifier.ValueText == "ConfigureAwait";
    }

    private static string Trim(string text)
    {
        var collapsed = string.Join(' ', text.Split('\n', '\r').Select(static l => l.Trim()).Where(static l => l.Length > 0));
        return collapsed.Length <= 110 ? collapsed : collapsed[..110] + "...";
    }
}
