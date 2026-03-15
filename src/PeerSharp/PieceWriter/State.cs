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
    public byte[] Pieces { get; set; } = Array.Empty<byte>();

    public long SeedTimeSeconds { get; set; }

    // Selection: File priorities
    public List<FileSelection> Selection { get; set; } = new();

    public bool Started { get; set; }

    public List<UnfinishedPieceData> UnfinishedPieces { get; set; } = new();

    public ulong Uploaded { get; set; }

    public uint Version { get; set; } = 1;

    internal class InfoData
    {
        public long FullSize { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint PieceSize { get; set; }
    }

    internal class UnfinishedPieceData
    {
        public bool[] Blocks { get; set; } = Array.Empty<bool>();
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Index { get; set; }
    }
}
