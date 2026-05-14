using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PeerSharp.Tests.Analyzers;

/// <summary>
/// Analyzes constructors for nullable parameters with default null values that are then null-checked.
///
/// This pattern suggests the null is being used to encode behavior branching, which should
/// instead be moved to a static factory method with explicit logic.
///
/// Bad pattern:
/// <code>
/// public MyClass(string? path = null)
/// {
///     if (path == null)  // Behavior change based on null!
///         path = GetDefault();
///     _path = path;
/// }
/// </code>
///
/// Good pattern:
/// <code>
/// public static MyClass Create(string? customPath = null)
/// {
///     var path = customPath ?? GetDefault();  // Decision in factory
///     return new MyClass(path);
/// }
///
/// private MyClass(string path)  // Non-nullable, just stores
/// {
///     _path = path;
/// }
/// </code>
/// </summary>
public sealed class NullDefaultParameterAnalyzer
{
    /// <summary>
    /// Represents a detected violation where a nullable default parameter is null-checked.
    /// </summary>
    public record NullCheckViolation(
        string FilePath,
        string ClassName,
        string ParameterName,
        string ParameterType,
        int LineNumber,
        string NullCheckPattern);

    /// <summary>
    /// Patterns that indicate null-checking behavior.
    /// </summary>
    private static readonly string[] NullCheckPatterns =
    {
        "== null",
        "!= null",
        "is null",
        "is not null",
        "??",
        "IsNullOrEmpty",
        "IsNullOrWhiteSpace"
    };

    /// <summary>
    /// Analyzes all C# source files in a directory for the null-checked default parameter pattern.
    /// </summary>
    public IReadOnlyList<NullCheckViolation> AnalyzeDirectory(string sourceDirectory)
    {
        var violations = new List<NullCheckViolation>();

        if (!Directory.Exists(sourceDirectory))
        {
            return violations;
        }

        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);

        foreach (var file in sourceFiles)
        {
            var fileViolations = AnalyzeFile(file);
            violations.AddRange(fileViolations);
        }

        return violations;
    }

    /// <summary>
    /// Analyzes a single C# source file.
    /// </summary>
    public static IReadOnlyList<NullCheckViolation> AnalyzeFile(string filePath)
    {
        var violations = new List<NullCheckViolation>();

        try
        {
            var sourceText = File.ReadAllText(filePath);
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath);
            var root = tree.GetRoot();

            // Find all constructor declarations
            var constructors = root.DescendantNodes().OfType<ConstructorDeclarationSyntax>();

            foreach (var ctor in constructors)
            {
                var ctorViolations = AnalyzeConstructor(ctor, filePath);
                violations.AddRange(ctorViolations);
            }
        }
        catch
        {
            // If we can't parse the file, skip it
        }

        return violations;
    }

    /// <summary>
    /// Analyzes a single constructor for null-checked default parameters.
    /// </summary>
    private static IEnumerable<NullCheckViolation> AnalyzeConstructor(ConstructorDeclarationSyntax ctor, string filePath)
    {
        var violations = new List<NullCheckViolation>();

        // Get the class name
        var classDecl = ctor.Parent as ClassDeclarationSyntax;
        var className = classDecl?.Identifier.Text ?? "<unknown>";

        // Find parameters with nullable type and default value of null
        var nullDefaultParams = ctor.ParameterList.Parameters
            .Where(IsNullableParameterWithNullDefault)
            .ToList();

        if (nullDefaultParams.Count == 0)
        {
            return violations;
        }

        // Get the constructor body text for analysis
        var bodyText = GetConstructorBodyText(ctor);
        if (string.IsNullOrEmpty(bodyText))
        {
            return violations;
        }

        // Check each nullable default parameter for null-checking in the body
        foreach (var param in nullDefaultParams)
        {
            var paramName = param.Identifier.Text;
            var nullCheckPattern = FindNullCheckPattern(bodyText, paramName);

            if (nullCheckPattern != null)
            {
                var lineNumber = ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var paramType = param.Type?.ToString() ?? "unknown";

                violations.Add(new NullCheckViolation(
                    FilePath: filePath,
                    ClassName: className,
                    ParameterName: paramName,
                    ParameterType: paramType,
                    LineNumber: lineNumber,
                    NullCheckPattern: nullCheckPattern));
            }
        }

        return violations;
    }

    /// <summary>
    /// Checks if a parameter is nullable and has a default value of null.
    /// </summary>
    private static bool IsNullableParameterWithNullDefault(ParameterSyntax param)
    {
        // Must have a default value
        if (param.Default == null)
        {
            return false;
        }

        // Default value must be null
        var defaultValue = param.Default.Value;
        if (defaultValue is not LiteralExpressionSyntax literal)
        {
            return false;
        }

        if (!literal.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return false;
        }

        // Type must be nullable (T? or Nullable<T>)
        var type = param.Type;
        if (type == null)
        {
            return false;
        }

        // Check for nullable reference type (T?)
        if (type is NullableTypeSyntax)
        {
            return true;
        }

        // Check for Nullable<T>
        if (type is GenericNameSyntax genericName &&
            genericName.Identifier.Text == "Nullable")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the constructor body as text for pattern matching.
    /// </summary>
    private static string GetConstructorBodyText(ConstructorDeclarationSyntax ctor)
    {
        // Handle block body
        if (ctor.Body != null)
        {
            return ctor.Body.ToString();
        }

        // Handle expression body
        if (ctor.ExpressionBody != null)
        {
            return ctor.ExpressionBody.ToString();
        }

        return string.Empty;
    }

    /// <summary>
    /// Finds if a parameter is null-checked in the given code text.
    /// Returns the pattern found, or null if no null-check detected.
    /// </summary>
    private static string? FindNullCheckPattern(string bodyText, string paramName)
    {
        foreach (var pattern in NullCheckPatterns)
        {
            // Check for patterns like "paramName == null" or "paramName??"
            if (pattern == "??")
            {
                // For null-coalescing, look for "paramName ??" or "paramName??"
                if (bodyText.Contains($"{paramName} ??") || bodyText.Contains($"{paramName}??"))
                {
                    return $"{paramName} ?? ...";
                }
            }
            else if (pattern.Contains("IsNull"))
            {
                // For string.IsNullOrEmpty(paramName) etc.
                if (bodyText.Contains($"IsNullOrEmpty({paramName})") ||
                    bodyText.Contains($"IsNullOrWhiteSpace({paramName})"))
                {
                    return $"string.{pattern}({paramName})";
                }
            }
            else
            {
                // For "paramName == null", "paramName is null", etc.
                if (bodyText.Contains($"{paramName} {pattern}") ||
                    bodyText.Contains($"({paramName} {pattern}") ||
                    (bodyText.Contains($"!{paramName}") && pattern == "!= null"))
                {
                    return $"{paramName} {pattern}";
                }
            }
        }

        return null;
    }
}





