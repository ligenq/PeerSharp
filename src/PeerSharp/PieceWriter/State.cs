namespace PeerSharp.PieceWriter;

/// <summary>
/// Internal model for torrent session state serialization.
/// Used by TorrentResumeData to export/import state.
/// </summary>
internal class TorrentStateData
{
    public long AddedTime { get; set; }

    public ulong Downloaded { get; set; }

    public string DownloadPath { get; set; } = string.Empty;

    public InfoData Info { get; set; } = new();

    public long LastStateTime { get; set; }

    // Bitfield of finished pieces
    public byte[] Pieces { get; set; } = [];

    public long SeedTimeSeconds { get; set; }

    // Selection: File priorities
    public List<FileSelection> Selection { get; set; } = [];

    public bool Started { get; set; }

    // Files the caller renamed. Persisted because the new name is the caller's, not the torrent's:
    // rebuilding paths from the metadata alone would silently undo every rename on the next start.
    public List<RenamedFileData> RenamedFiles { get; set; } = [];

    public List<UnfinishedPieceData> UnfinishedPieces { get; set; } = [];

    public ulong Uploaded { get; set; }

    public uint Version { get; set; } = 1;

    internal class InfoData
    {
        public long FullSize { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint PieceSize { get; set; }
    }

    internal class RenamedFileData
    {
        public int Index { get; set; }
        public string Path { get; set; } = string.Empty;
    }

    internal class UnfinishedPieceData
    {
        public bool[] Blocks { get; set; } = [];
        public byte[] Data { get; set; } = [];
        public int Index { get; set; }
    }
}
