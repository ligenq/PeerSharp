using System.Security.Cryptography;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>A valid, multi-piece v1 info dictionary built without PeerSharp's torrent writer.</summary>
internal sealed class SyntheticMetadataFixture
{
    private SyntheticMetadataFixture(byte[] infoBytes)
    {
        InfoBytes = infoBytes;
        InfoHash = SHA1.HashData(infoBytes);
    }

    public byte[] InfoBytes { get; }

    public byte[] InfoHash { get; }

    public string InfoHashHex => Convert.ToHexStringLower(InfoHash);

    public int MetadataPieceCount => (InfoBytes.Length + 16 * 1024 - 1) / (16 * 1024);

    public static SyntheticMetadataFixture Create()
    {
        const int payloadPieceLength = 16 * 1024;
        const int payloadPieceCount = 1800;

        // Piece hashes need only be structurally valid here: the payload is never downloaded. Their
        // size deliberately pushes the info dictionary over two BEP 9 blocks so completion proves
        // that requesting, indexing and assembling more than one metadata piece all work.
        byte[] pieceHashes = new byte[payloadPieceCount * 20];
        for (int i = 0; i < pieceHashes.Length; i++)
        {
            pieceHashes[i] = (byte)((i * 31 + 17) & 0xff);
        }

        byte[] infoBytes = SyntheticBencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["length"] = (long)payloadPieceLength * payloadPieceCount,
            ["name"] = "synthetic-metadata.bin",
            ["piece length"] = (long)payloadPieceLength,
            ["pieces"] = pieceHashes
        });

        return new SyntheticMetadataFixture(infoBytes);
    }
}
