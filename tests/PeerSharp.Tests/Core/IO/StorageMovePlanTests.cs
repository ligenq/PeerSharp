using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public sealed class StorageMovePlanTests
{
    [Fact]
    public async Task RecoveryValidatesTheEntirePlanBeforeMovingAnyFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("PeerSharpStorageMovePlan-");
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(root.FullName, "source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(root.FullName, "destination")).FullName;
            string destinationFile = Path.Combine(destination, "payload.bin");
            await File.WriteAllTextAsync(destinationFile, "verified data", ct);
            var persistence = new SessionPersistence(Path.Combine(root.FullName, "session"), NullLogger<SessionPersistence>.Instance);
            var hash = new InfoHash(Enumerable.Repeat((byte)1, 20).ToArray());
            var entry = new SavedTorrentEntry(hash, Options: new SavedTorrentOptions(source));
            await persistence.SaveAsync(entry, ct);
            await persistence.RecordStorageMoveAsync(hash, new StorageMovePlan(source, destination,
                [Path.Combine("..", "outside.bin"), "payload.bin"]), ct);

            await using var manager = new SessionManager(persistence, new TorrentRegistry(), TimeProvider.System,
                NullLogger<SessionManager>.Instance);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.RecoverStorageMoveAsync(entry, ct));
            Assert.False(File.Exists(Path.Combine(source, "payload.bin")));
            Assert.Equal("verified data", await File.ReadAllTextAsync(destinationFile, ct));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
