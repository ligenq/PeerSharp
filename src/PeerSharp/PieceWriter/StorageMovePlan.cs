namespace PeerSharp.PieceWriter;

internal sealed record StorageMovePlan(string SourcePath, string DestinationPath, IReadOnlyList<string> RelativePaths,
    Guid MoveId = default)
{
    internal string? GetTemporaryPath(string path, int index) => MoveId == Guid.Empty ? null :
        Path.Combine(Path.GetDirectoryName(path)!, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $".peersharp-move-{MoveId:N}-{index}.tmp"));

    public async Task RecoverAsync(string? savedPath, Func<string, CancellationToken, Task>? onPartialCopy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(savedPath) || string.IsNullOrWhiteSpace(SourcePath) ||
            string.IsNullOrWhiteSpace(DestinationPath) || !Path.IsPathFullyQualified(SourcePath) ||
            !Path.IsPathFullyQualified(DestinationPath) || RelativePaths == null)
        {
            throw new InvalidDataException("The pending storage move has no usable download paths.");
        }

        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(savedPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool committed = string.Equals(current, DestinationPath, comparison);
        if (!committed && !string.Equals(current, SourcePath, comparison))
        {
            throw new InvalidDataException("The pending storage move does not match the saved download path.");
        }

        foreach (string relative in RelativePaths)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                Storage.EscapesRoot(Path.GetRelativePath(SourcePath, Path.Combine(SourcePath, relative))) ||
                Storage.EscapesRoot(Path.GetRelativePath(DestinationPath, Path.Combine(DestinationPath, relative))))
            {
                throw new InvalidDataException("A pending storage move contains a path outside its download directories.");
            }
        }

        // Reverse the move. A copy interrupted before source deletion leaves the old file intact.
        // Keep it and any destination copy. Copy back through a temporary file so another crash
        // during recovery cannot make a partial file at the old path look authoritative.
        for (int i = RelativePaths.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            string relative = RelativePaths[i];
            string original = Path.Combine(SourcePath, relative);
            string moved = Path.Combine(DestinationPath, relative);
            string? recoveryTemporary = GetTemporaryPath(original, i);
            DeleteTemporaryCopy(recoveryTemporary);
            DeleteTemporaryCopy(GetTemporaryPath(moved, i));
            if (committed || File.Exists(original) || !File.Exists(moved)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            await Storage.MoveByCopyingAsync(moved, original, recoveryTemporary, overwrite: false, onPartialCopy, ct)
                .ConfigureAwait(false);
        }
    }

    private static void DeleteTemporaryCopy(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { /* The temporary file does not exist. */ }
    }
}
