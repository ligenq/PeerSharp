using CsCheck;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Seeding;
using System.Net;

namespace PeerSharp.Tests.Core.Seeding;

/// <summary>
/// Assembling a piece out of a multi-file torrent's HTTP ranges.
/// </summary>
/// <remarks>
/// <para>
/// A web seed serves the torrent's files, not its pieces, so every piece has to be cut back into
/// per-file ranges and stitched together again. That is the same arithmetic as the piece writer's,
/// against a different set of edges - a piece can start mid-file, end mid-file, span several whole
/// files, or land on a padding file that is never fetched at all - and it is arithmetic that already
/// hid a data-loss bug elsewhere in this engine.
/// </para>
/// <para>
/// The property is the only one that matters: whatever the file layout and whichever piece is asked
/// for, the bytes returned are the bytes at that offset in the torrent. Anything else is a piece that
/// fails its hash, or worse, one that does not.
/// </para>
/// </remarks>
public class WebSeedPieceAssemblyPropertyTests
{
    /// <summary>
    /// Layouts with the awkward cases in them by construction: empty files, padding files, files far
    /// smaller than a piece and files far larger.
    /// </summary>
    private static readonly Gen<(int Size, bool Padding)[]> Layouts =
        Gen.Select(Gen.Int[0, 40], Gen.Bool)
            .Select(t => (t.Item1, t.Item2 && t.Item1 > 0))
            .Array[1, 8]
            .Where(files => files.Sum(f => f.Item1) > 0);

    [Fact]
    public async Task EveryPieceAssemblesToTheBytesAtThatOffset()
    {
        await Gen.Select(Layouts, Gen.Int[1, 24]).SampleAsync(async (layout, pieceSize) =>
        {
            var world = new TorrentWorld(layout, pieceSize);
            await using var torrent = world.CreateTorrent();

            var manager = new WebSeedManager(torrent, ["http://seed.example"], TimeProvider.System);
            manager.SetTestClient(world.Client);
            await using (manager)
            {
                var source = new WebSeedManager.WebSeedSource("http://seed.example", true);

                for (int piece = 0; piece < world.PieceCount; piece++)
                {
                    long offset = (long)piece * pieceSize;
                    int length = (int)Math.Min(pieceSize, world.Content.Length - offset);

                    byte[] assembled = await manager.DownloadMultiFilePieceAsync(
                        source, piece, offset, length, TestContext.Current.CancellationToken);

                    Assert.NotNull(assembled);
                    Assert.Equal(world.Content.AsSpan((int)offset, length).ToArray(), assembled);
                }
            }
        }, iter: 400);
    }

    [Fact]
    public async Task PaddingFilesAreZeroFilledWithoutBeingFetched()
    {
        // BEP 47 padding exists so the next real file starts on a piece boundary. It is never served
        // - asking a seed for it is a request for bytes the seed does not have - so it has to be
        // filled in locally, and it still occupies its place in the torrent's byte space.
        var layout = new[] { (Size: 3, Padding: false), (Size: 5, Padding: true), (Size: 4, Padding: false) };
        var world = new TorrentWorld(layout, pieceSize: 12);
        await using var torrent = world.CreateTorrent();

        var manager = new WebSeedManager(torrent, ["http://seed.example"], TimeProvider.System);
        manager.SetTestClient(world.Client);
        await using (manager)
        {
            var source = new WebSeedManager.WebSeedSource("http://seed.example", true);

            byte[] assembled = await manager.DownloadMultiFilePieceAsync(
                source, 0, 0, 12, TestContext.Current.CancellationToken);

            Assert.Equal(world.Content, assembled);
            Assert.All(world.Client.RequestedPaths, path => Assert.DoesNotContain(".pad", path, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AFailedFileRangeAbandonsTheWholePiece()
    {
        // A piece assembled from several files is only worth having whole. Returning what did arrive
        // would hand the verifier a buffer that is partly this piece and partly zeros.
        var layout = new[] { (Size: 6, Padding: false), (Size: 6, Padding: false) };
        var world = new TorrentWorld(layout, pieceSize: 12);
        world.Client.FailPathsContaining = "file1";
        await using var torrent = world.CreateTorrent();

        var manager = new WebSeedManager(torrent, ["http://seed.example"], TimeProvider.System);
        manager.SetTestClient(world.Client);
        await using (manager)
        {
            var source = new WebSeedManager.WebSeedSource("http://seed.example", true);

            byte[] assembled = await manager.DownloadMultiFilePieceAsync(
                source, 0, 0, 12, TestContext.Current.CancellationToken);

            Assert.Null(assembled);
        }
    }

    /// <summary>A torrent, its true bytes, and a seed that serves them.</summary>
    private sealed class TorrentWorld
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public TorrentWorld((int Size, bool Padding)[] layout, int pieceSize)
        {
            Metadata = new TorrentFileMetadata();
            Metadata.Info.Name = "seeded";
            Metadata.Info.PieceSize = (uint)pieceSize;

            var content = new List<byte>();
            long offset = 0;
            for (int i = 0; i < layout.Length; i++)
            {
                var (size, padding) = layout[i];
                string path = padding ? $".pad/{i}" : $"file{i}.bin";

                // Padding is zeros by definition; real files get bytes that identify the file they
                // came from, so a range fetched from the wrong file shows up as wrong content rather
                // than as a length mismatch.
                byte[] bytes = new byte[size];
                if (!padding)
                {
                    for (int b = 0; b < size; b++)
                    {
                        bytes[b] = (byte)((i * 37) + b + 1);
                    }

                    _files[path] = bytes;
                }

                content.AddRange(bytes);
                Metadata.Info.Files.Add(new Internals.TorrentFileEntry
                {
                    Path = path,
                    Size = size,
                    Offset = offset,
                    IsPadding = padding
                });

                offset += size;
            }

            Content = [.. content];
            Metadata.Info.FullSize = Content.Length;
            PieceCount = (int)((Content.Length + pieceSize - 1) / pieceSize);
            Client = new RangeServingClient(_files);
        }

        public byte[] Content { get; }
        public int PieceCount { get; }
        public RangeServingClient Client { get; }
        private TorrentFileMetadata Metadata { get; }

        public Torrent CreateTorrent() => TorrentTestUtility.CreateMinimal(Metadata);
    }

    /// <summary>Serves byte ranges of the named files, and records what was asked for.</summary>
    private sealed class RangeServingClient(Dictionary<string, byte[]> files) : IHttpClient
    {
        public List<string> RequestedPaths { get; } = [];
        public string? FailPathsContaining { get; set; }

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken)
            => Task.FromResult<byte[]>([]);

        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            string url = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
            RequestedPaths.Add(url);

            if (FailPathsContaining != null && url.Contains(FailPathsContaining, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var entry = files.FirstOrDefault(f => url.EndsWith(f.Key, StringComparison.Ordinal));
            if (entry.Value == null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            long from = range?.From ?? 0;
            long to = range?.To ?? entry.Value.Length - 1;
            int length = (int)(to - from + 1);

            if (from < 0 || length < 0 || from + length > entry.Value.Length)
            {
                // The manager asked for bytes outside the file, which is the bug this exists to
                // catch. Refusing is what a real server does.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(entry.Value.AsSpan((int)from, length).ToArray())
            });
        }
    }
}
