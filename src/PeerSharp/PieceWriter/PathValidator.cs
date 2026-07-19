namespace PeerSharp.PieceWriter;

/// <summary>
/// Result of path validation.
/// </summary>
internal readonly record struct PathValidationResult(
    bool IsValid,
    string? SanitizedPath,
    PathValidationError Error = PathValidationError.None);

/// <summary>
/// Types of path validation errors.
/// </summary>
internal enum PathValidationError
{
    None,
    EmptyOrWhitespace,
    PathTraversalAttempt,
    InvalidCharacters,
    WindowsReservedName,
    EscapesRootDirectory,
    NoValidComponents
}

/// <summary>
/// Interface for path validation to allow testing and dependency injection.
/// </summary>
internal interface IPathValidator
{
    /// <summary>
    /// Checks if a filename is a Windows reserved name.
    /// </summary>
    bool IsWindowsReservedName(string name);

    /// <summary>
    /// Validates and sanitizes a relative path from torrent metadata.
    /// Returns a result indicating whether the path is valid and the sanitized absolute path.
    /// </summary>
    PathValidationResult ValidatePath(string relativePath);
}

/// <summary>
/// Validates and sanitizes file paths from torrent metadata to prevent path traversal attacks.
/// This class is stateless and thread-safe.
/// </summary>
internal sealed class PathValidator : IPathValidator
{
    // Windows reserved names that cannot be used as filenames
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _rootPath;
    private readonly string _rootPathNormalized;

    public PathValidator(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
        _rootPathNormalized = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Creates a PathValidator with a temporary root for testing purposes.
    /// </summary>
    public static PathValidator CreateForTesting(string rootPath)
    {
        return new PathValidator(rootPath);
    }

    /// <inheritdoc />
    public bool IsWindowsReservedName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return WindowsReservedNames.Contains(name);
    }

    /// <inheritdoc />
    public PathValidationResult ValidatePath(string relativePath)
    {
        // Check for empty or whitespace
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new PathValidationResult(false, null, PathValidationError.EmptyOrWhitespace);
        }

        // Normalize path separators to current platform
        string normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        // Split into components and validate each
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var safeParts = new List<string>();

        foreach (var part in parts)
        {
            // Skip empty parts and current directory references
            if (string.IsNullOrWhiteSpace(part) || part == ".")
            {
                continue;
            }

            // Reject path traversal attempts
            if (part == "..")
            {
                return new PathValidationResult(false, null, PathValidationError.PathTraversalAttempt);
            }

            // Check for invalid filename characters
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return new PathValidationResult(false, null, PathValidationError.InvalidCharacters);
            }

            // Check for Windows reserved names
            string nameWithoutExt = Path.GetFileNameWithoutExtension(part);
            if (IsWindowsReservedName(nameWithoutExt))
            {
                return new PathValidationResult(false, null, PathValidationError.WindowsReservedName);
            }

            safeParts.Add(part);
        }

        // Must have at least one valid component
        if (safeParts.Count == 0)
        {
            return new PathValidationResult(false, null, PathValidationError.NoValidComponents);
        }

        // Reconstruct the safe relative path
        string safePath = Path.Combine([.. safeParts]);

        // Combine with root and get full path
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, safePath));

        // Final validation: ensure path stays within root directory
        // Use Path.GetRelativePath for robust containment check
        string relativeTorRoot = Path.GetRelativePath(_rootPathNormalized, fullPath);

        // If the relative path starts with ".." or is rooted, it escapes the root
        if (relativeTorRoot.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativeTorRoot))
        {
            return new PathValidationResult(false, null, PathValidationError.EscapesRootDirectory);
        }

        return new PathValidationResult(true, fullPath, PathValidationError.None);
    }
}
