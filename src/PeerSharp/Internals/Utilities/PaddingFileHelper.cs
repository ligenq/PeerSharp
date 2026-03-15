namespace PeerSharp.Internals.Utilities;

internal static class PaddingFileHelper
{
    private const string PaddingDirectoryName = ".pad";

    public static bool IsPaddingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], PaddingDirectoryName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public static string BuildPaddingPath(long length, int index)
    {
        return $"{PaddingDirectoryName}/{length}-{index}";
    }
}
