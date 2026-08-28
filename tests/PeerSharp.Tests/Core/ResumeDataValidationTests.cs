using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using System.Text.Json;

namespace PeerSharp.Tests.Core;

/// <summary>
/// What a torrent accepts from a resume file.
///
/// <para>
/// Resume data is a claim that certain pieces are already on the disk, and the engine acts on that
/// claim without re-verifying: pieces marked present are never hashed again, they are advertised to
/// the swarm and they are served on request. So the cost of adopting resume data that describes a
/// different torrent is not a wasted download, it is uploading whatever those bytes happen to be.
/// The fields these tests exercise were written on every save and read by nothing.
/// </para>
/// </summary>
public class ResumeDataValidationTests
{
    private const uint PieceSize = 16384;
    private const long FullSize = PieceSize * 4;

    private static TorrentFileMetadata CreateMetadata(uint pieceSize = PieceSize, long fullSize = FullSize)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "resume-subject";
        metadata.Info.PieceSize = pieceSize;
        metadata.Info.FullSize = fullSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "payload.bin", Size = fullSize, Offset = 0 });

        // HasMetadata is what distinguishes a real torrent from a magnet still waiting for one, and
        // it reads the piece hash list. Four pieces, contents irrelevant.
        for (int i = 0; i < fullSize / pieceSize; i++)
        {
            metadata.Info.Pieces.Add(new byte[20]);
        }

        return metadata;
    }

    /// <summary>
    /// Resume data with no hash recorded, so these cases turn on geometry alone. The identity checks
    /// set the hash explicitly.
    /// </summary>
    private static TorrentResumeData Serialize(TorrentStateData state)
    {
        return new TorrentResumeData
        {
            Data = JsonSerializer.SerializeToUtf8Bytes(state, PeerSharpJsonContext.Default.TorrentStateData),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A resume state that agrees with <see cref="CreateMetadata"/> in every respect.</summary>
    private static TorrentStateData CreateMatchingState()
    {
        int pieces = (int)(FullSize / PieceSize);
        var bitfield = new byte[(pieces + 7) / 8];
        bitfield[0] = 0b1111_0000; // all four pieces present

        return new TorrentStateData
        {
            Version = 1,
            Pieces = bitfield,
            Downloaded = FullSize,
            DownloadPath = "C:\\Downloads",
            Info =
            {
                Name = "resume-subject",
                PieceSize = PieceSize,
                FullSize = FullSize
            }
        };
    }

    [Fact]
    public void MatchingResumeData_IsAdopted()
    {
        var torrent = TorrentTestUtility.CreateMinimal(
            CreateMetadata(),
            resumeData: Serialize(CreateMatchingState()));

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataFromANewerFormat_IsDiscarded()
    {
        // The fields we recognise may not mean what they did. Reading them anyway is how a format
        // change turns into silent corruption instead of a re-download.
        var state = CreateMatchingState();
        state.Version = 2;

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: Serialize(state));

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataForADifferentPieceSize_IsDiscarded()
    {
        // Same bitfield length, entirely different meaning: piece 2 under a 32 KiB piece size covers
        // bytes that piece 2 under a 16 KiB one does not.
        var state = CreateMatchingState();
        state.Info.PieceSize = PieceSize * 2;

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: Serialize(state));

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataForADifferentContentLength_IsDiscarded()
    {
        var state = CreateMatchingState();
        state.Info.FullSize = FullSize + PieceSize;

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: Serialize(state));

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataWithAWrongLengthBitfield_IsDiscarded()
    {
        // A truncated write produces exactly this, and the surviving prefix decodes as a perfectly
        // plausible set of completed pieces.
        var state = CreateMatchingState();
        state.Pieces = [0b1111_0000, 0b1111_1111];

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: Serialize(state));

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataThatOnlyDiffersByName_IsStillAdopted()
    {
        // A renamed torrent is not a different torrent, and the piece geometry is what the bitfield
        // depends on. Refusing here would throw away good progress for a cosmetic difference.
        var state = CreateMatchingState();
        state.Info.Name = "renamed-since-the-save";

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: Serialize(state));

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataWithoutRecordedGeometry_IsAdopted()
    {
        // Zero means "not recorded" rather than "recorded as zero", so resume data written before a
        // field existed is not thrown away for lacking it.
        var state = CreateMatchingState();
        state.Info.PieceSize = 0;
        state.Info.FullSize = 0;
        state.Info.Name = string.Empty;

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: Serialize(state));

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataSavedForADifferentTorrent_IsDiscarded()
    {
        // The geometry checks cannot catch this on their own: two torrents of the same total size and
        // piece size agree on every one of them. Without the identity check the bitfield would be
        // believed, and whatever bytes are on disk advertised to the swarm and served on request.
        var resumeData = Serialize(CreateMatchingState());
        var foreign = new TorrentResumeData
        {
            Hash = InfoHash.CreateRandom(),
            Data = resumeData.Data,
            Timestamp = resumeData.Timestamp
        };

        var metadata = CreateMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, resumeData: foreign);

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataSavedForThisTorrent_IsAdopted()
    {
        var metadata = CreateMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();

        var state = CreateMatchingState();
        var resumeData = new TorrentResumeData
        {
            Hash = metadata.Info.Hash,
            Data = Serialize(state).Data,
            Timestamp = DateTimeOffset.UtcNow
        };

        var torrent = TorrentTestUtility.CreateMinimal(metadata, resumeData: resumeData);

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataRecordingTheV2Hash_IsAdopted()
    {
        // A hybrid torrent has two identities and resume data records one of them. Matching only the
        // v1 hash would reject a hybrid torrent's own resume file whenever it happened to be saved
        // under the v2 form.
        var metadata = CreateMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();

        var resumeData = new TorrentResumeData
        {
            Hash = metadata.Info.HashV2,
            Data = Serialize(CreateMatchingState()).Data,
            Timestamp = DateTimeOffset.UtcNow
        };

        var torrent = TorrentTestUtility.CreateMinimal(metadata, resumeData: resumeData);

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void AV2OnlyTorrent_MatchesOnItsV2Hash()
    {
        // A v2-only torrent has no v1 hash at all, so the v2 comparison is the only one that can
        // identify it. Requiring the v1 hash to match would reject its own resume data every time.
        var metadata = CreateMetadata();
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();

        var resumeData = new TorrentResumeData
        {
            Hash = metadata.Info.HashV2,
            Data = Serialize(CreateMatchingState()).Data,
            Timestamp = DateTimeOffset.UtcNow
        };

        Assert.True(metadata.Info.Hash.IsEmpty);

        var torrent = TorrentTestUtility.CreateMinimal(metadata, resumeData: resumeData);

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataMatchingNeitherHashOfAHybridTorrent_IsDiscarded()
    {
        var metadata = CreateMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.HashV2 = InfoHash.CreateRandomV2();

        var resumeData = new TorrentResumeData
        {
            Hash = InfoHash.CreateRandom(),
            Data = Serialize(CreateMatchingState()).Data,
            Timestamp = DateTimeOffset.UtcNow
        };

        var torrent = TorrentTestUtility.CreateMinimal(metadata, resumeData: resumeData);

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataWithNoRecordedHash_IsStillAdopted()
    {
        // An empty hash means "not recorded" - resume data a caller built by hand, or wrote before
        // the field was populated. Refusing it would break those callers for no safety gain, since
        // the geometry checks still apply.
        var metadata = CreateMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();

        var torrent = TorrentTestUtility.CreateMinimal(metadata, resumeData: Serialize(CreateMatchingState()));

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    // ── Magnets ──────────────────────────────────────────────────────────────
    //
    // A magnet is added before it knows its geometry, so the checks above have nothing to compare a
    // bitfield against and let it through. That is only safe if the same checks run again once the
    // metadata arrives - otherwise resume data rejected outright for a .torrent is quietly trusted
    // for the same content fetched by magnet.

    /// <summary>Metadata with an info hash and nothing else: what a magnet looks like before its
    /// metadata arrives.</summary>
    private static TorrentFileMetadata CreateMagnetMetadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = "magnet-subject";
        metadata.Info.Hash = InfoHash.CreateRandom();
        return metadata;
    }

    [Fact]
    public void AMagnetHoldsResumeDataItCannotYetCheck()
    {
        // Not a bug on its own - there is genuinely nothing to check against yet. The state is held
        // rather than discarded so a magnet does not lose its progress on every restart.
        var state = CreateMatchingState();
        state.Info.PieceSize = PieceSize * 2; // wrong for the metadata that will arrive

        var torrent = TorrentTestUtility.CreateMinimal(CreateMagnetMetadata(), resumeData: Serialize(state));

        Assert.False(torrent.HasMetadata);
        Assert.NotEmpty(torrent.LocalState.Pieces);
    }

    [Fact]
    public async Task AMagnetsResumeData_IsRecheckedOnceItsMetadataArrives()
    {
        // The gap this closes. The bitfield rode through the magnet's add unchecked; when the
        // metadata rebuild applies it, the geometry disagrees and it has to be dropped rather than
        // believed.
        var state = CreateMatchingState();
        state.Info.PieceSize = PieceSize * 2;

        var magnet = CreateMagnetMetadata();
        var torrent = TorrentTestUtility.CreateMinimal(magnet, resumeData: Serialize(state));
        Assert.NotEmpty(torrent.LocalState.Pieces);

        // The metadata arrives and disagrees with what the resume file recorded.
        var resolved = CreateMetadata();
        resolved.Info.Hash = magnet.Info.Hash;
        torrent.InfoFile = resolved;
        await torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
        Assert.Empty(torrent.LocalState.Pieces);
    }

    [Fact]
    public async Task AMagnetsResumeData_SurvivesWhenTheMetadataAgrees()
    {
        // The other half: revalidation must not throw away good progress. A magnet that resumes
        // correctly is the normal case and by far the more common one.
        var magnet = CreateMagnetMetadata();
        var torrent = TorrentTestUtility.CreateMinimal(magnet, resumeData: Serialize(CreateMatchingState()));

        var resolved = CreateMetadata();
        resolved.Info.Hash = magnet.Info.Hash;
        torrent.InfoFile = resolved;
        await torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void ResumeDataThatDeserialisesToNothing_LeavesTheTorrentEmpty()
    {
        // Valid JSON, no object: the literal `null`. Distinct from unparseable input, and it reaches
        // a different branch - the deserialiser returns without throwing.
        var resumeData = new TorrentResumeData
        {
            Data = "null"u8.ToArray(),
            Timestamp = DateTimeOffset.UtcNow
        };

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: resumeData);

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }

    [Fact]
    public void UnparseableResumeData_LeavesTheTorrentEmptyRatherThanThrowing()
    {
        var resumeData = new TorrentResumeData
        {
            Hash = InfoHash.CreateRandom(),
            Data = "this is not json"u8.ToArray(),
            Timestamp = DateTimeOffset.UtcNow
        };

        var torrent = TorrentTestUtility.CreateMinimal(CreateMetadata(), resumeData: resumeData);

        Assert.Equal(0, torrent.Pieces.ReceivedCount);
    }
}
