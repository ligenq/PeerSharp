using CsCheck;
using PeerSharp.Streaming;

namespace PeerSharp.Tests.Core.Streaming;

/// <summary>
/// The Range header parser, against whatever a client sends.
/// </summary>
/// <remarks>
/// <para>
/// The header comes from outside the process - a media player, a browser, or whatever else is
/// pointed at the stream server - and the numbers that come out of it are used directly to seek and
/// read. A range that escapes the file is a read past the end of the torrent; a range wrongly called
/// unsatisfiable is a player that stops.
/// </para>
/// <para>
/// The invariant worth stating is one line: whenever the parser says a range is usable, that range
/// lies inside the file. Everything else about the header - which of the RFC's forms it is, how it
/// is malformed - is the parser's business.
/// </para>
/// </remarks>
public class HttpRangePropertyTests
{
    /// <summary>
    /// Headers assembled from the RFC's forms and from the ways clients get them wrong: the units
    /// prefix present or absent, missing or doubled dashes, non-numeric and overflowing positions,
    /// negatives, whitespace, and multi-range.
    /// </summary>
    private static readonly Gen<string> Header = Gen.OneOfConst(
        "bytes=", "bytes=-", "bytes=--", "bytes=5", "bytes=-0", "bytes=--5",
        "bytes=0-", "bytes=-1", "bytes=-5", "bytes=-100000",
        "bytes=0-0", "bytes=0-15", "bytes=0-100000", "bytes=8-7", "bytes=15-15",
        "bytes=16-16", "bytes=16-", "bytes=100000-", "bytes=100000-100001",
        "bytes=0-5,10-15", "bytes=foo-bar", "bytes=1-bar", "bytes=foo-1",
        "bytes= 0-10", "bytes=0 - 10", "bytes=+0-10", "bytes=0-+10",
        "bytes=9223372036854775807-", "bytes=0-9223372036854775807",
        "bytes=99999999999999999999-", "bytes=0-99999999999999999999",
        "items=1-2", "BYTES=0-1", "", " ", "0-1");

    [Fact]
    public void AUsableRangeAlwaysLiesInsideTheFile()
    {
        Gen.Select(Header, Gen.Long[0, 4096]).Sample((header, totalLength) =>
        {
            var range = HttpRangeParser.Parse(header, totalLength);
            if (!range.IsValid)
            {
                return;
            }

            Assert.InRange(range.Start, 0, totalLength - 1);
            Assert.InRange(range.End, range.Start, totalLength - 1);
        }, iter: 20_000);
    }

    [Fact]
    public void AnEmptyFileIsNeverSatisfiable()
    {
        // There is no byte to send, so every form of the header has to be refused rather than
        // producing a range of length zero or a negative one.
        Header.Sample(header => Assert.False(HttpRangeParser.Parse(header, 0).IsValid), iter: 5_000);
    }

    [Fact]
    public void ParsingNeverThrows()
    {
        // Arbitrary bytes in the header, not just the shapes worth naming.
        Gen.Select(Gen.String, Gen.Long[0, 4096]).Sample((header, totalLength) =>
        {
            var range = HttpRangeParser.Parse(header, totalLength);
            Assert.Equal(range, HttpRangeParser.Parse(header, totalLength));
        }, iter: 20_000);
    }

    [Fact]
    public void AStartInsideTheFileIsAlwaysSatisfiable()
    {
        // RFC 7233 §2.1 decides satisfiability on the first byte position. Whatever the client asked
        // for as an end - absent, or past the end of the file - the answer is the remainder, not a
        // refusal, and the range begins exactly where it was asked to.
        Gen.Select(Gen.Long[0, 4095], Gen.Long[0, 100_000], Gen.Bool).Sample((start, end, openEnded) =>
        {
            const long totalLength = 4096;
            string header = openEnded ? $"bytes={start}-" : $"bytes={start}-{start + end}";

            var range = HttpRangeParser.Parse(header, totalLength);

            Assert.True(range.IsValid, $"'{header}' was refused for a {totalLength}-byte file");
            Assert.Equal(start, range.Start);
            Assert.Equal(Math.Min(openEnded ? totalLength - 1 : start + end, totalLength - 1), range.End);
        }, iter: 20_000);
    }

    [Fact]
    public void AStartPastTheEndIsNeverSatisfiable()
    {
        Gen.Select(Gen.Long[4096, 100_000], Gen.Long[0, 100_000]).Sample((start, length) =>
        {
            const long totalLength = 4096;

            Assert.False(HttpRangeParser.Parse($"bytes={start}-", totalLength).IsValid);
            Assert.False(HttpRangeParser.Parse($"bytes={start}-{start + length}", totalLength).IsValid);
        }, iter: 10_000);
    }

    [Fact]
    public void ASuffixNeverAsksForMoreThanTheFileHolds()
    {
        // "bytes=-N" is the last N bytes, and a suffix longer than the file is the whole file rather
        // than a read starting before its beginning.
        Gen.Select(Gen.Long[1, 100_000], Gen.Long[1, 4096]).Sample((suffix, totalLength) =>
        {
            var range = HttpRangeParser.Parse($"bytes=-{suffix}", totalLength);

            Assert.True(range.IsValid);
            Assert.Equal(Math.Max(0, totalLength - suffix), range.Start);
            Assert.Equal(totalLength - 1, range.End);
        }, iter: 10_000);
    }
}
