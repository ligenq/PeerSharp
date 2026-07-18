namespace PeerSharp.Core;

/// <summary>
/// BEP 47: File attribute flags carried by the "attr" key of a file entry.
/// </summary>
[Flags]
public enum TorrentFileAttributes
{
    /// <summary>No attributes.</summary>
    None = 0,

    /// <summary>The file should be marked executable ('x').</summary>
    Executable = 1,

    /// <summary>The file should be hidden ('h').</summary>
    Hidden = 2,

    /// <summary>The entry is a symbolic link ('l'); its target is in "symlink path".</summary>
    Symlink = 4,

    /// <summary>The entry is a padding file ('p').</summary>
    Padding = 8
}
