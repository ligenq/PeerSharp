using PeerSharp.Internals;

namespace PeerSharp.PieceWriter;

/// <summary>
/// Composition root for piece writer components.
/// </summary>
internal static class PieceWriterModule
{
    public static Files CreateFiles(Torrent torrent, string downloadPath, IFileHandleCache handleCache)
    {
        return Files.Create(torrent, handleCache, downloadPath);
    }
}
