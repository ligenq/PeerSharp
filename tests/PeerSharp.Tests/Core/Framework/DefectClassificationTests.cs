using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Tests.Core.Framework;

/// <summary>
/// Which exceptions count as this library's own fault.
/// </summary>
/// <remarks>
/// The line drawn here decides what the suite is allowed to ignore, so it is worth stating rather
/// than leaving to whoever next adds a catch block. On one side, mistakes only this code can make.
/// On the other, everything reachable from a stranger's bytes, a missing file, or a socket that went
/// away - none of which mean the code is wrong.
/// </remarks>
[ReportsDefectsOnPurpose]
public class DefectClassificationTests
{
    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(DivideByZeroException))]
    [InlineData(typeof(NotImplementedException))]
    public void MistakesOnlyThisCodeCanMakeAreDefects(Type type)
    {
        Assert.True(((Exception)Activator.CreateInstance(type)!).IsDefect());
    }

    [Theory]
    // A malformed torrent, a bad packet, a peer that hung up, a disk that is not there. Every one of
    // these arrives from outside and is the ordinary business of a BitTorrent engine.
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public void FailuresThatComeFromOutsideAreNot(Type type)
    {
        Assert.False(((Exception)Activator.CreateInstance(type)!).IsDefect());
    }

    [Fact]
    public void AShutdownRaceIsNotADefect()
    {
        // Separate because it has no parameterless constructor. A loop reading from a stream another
        // loop has just closed is how shutdown looks from the inside, not a mistake.
        Assert.False(new ObjectDisposedException("stream").IsDefect());
    }

    [Fact]
    public void ADefectIsStillADefectInsideAWrapper()
    {
        // Crossing a task or a reflection boundary wraps it, and a wrapper must not launder a bug
        // into ordinary weather.
        Assert.True(new AggregateException(new NullReferenceException()).IsDefect());
        Assert.False(new AggregateException(new IOException()).IsDefect());
    }

    [Fact]
    public void AnAggregateOfSeveralFailuresIsNotClaimedEitherWay()
    {
        // More than one inner exception has no single answer, so it is left to the general case
        // rather than guessed at from the first one.
        Assert.False(new AggregateException(new NullReferenceException(), new IOException()).IsDefect());
    }

    [Fact]
    public void ReportingHandsTheDefectToObservers()
    {
        var observer = new RecordingObserver();
        using (Defect.Observe(observer))
        {
            Defect.ReportIfDefect(new NullReferenceException("x"), "SomeLoop", NullLogger.Instance);
            Defect.ReportIfDefect(new IOException("peer hung up"), "SomeLoop", NullLogger.Instance);
        }

        var reported = Assert.Single(observer.Seen);
        Assert.Equal("SomeLoop", reported.Context);
        Assert.IsType<NullReferenceException>(reported.Exception);
    }

    [Fact]
    public void AnUnregisteredObserverHearsNothingMore()
    {
        var observer = new RecordingObserver();
        Defect.Observe(observer).Dispose();

        Defect.ReportIfDefect(new NullReferenceException("x"), "SomeLoop", NullLogger.Instance);

        Assert.Empty(observer.Seen);
    }

    private sealed class RecordingObserver : IDefectObserver
    {
        public List<(Exception Exception, string Context)> Seen { get; } = [];

        public void DefectCaught(Exception exception, string context) => Seen.Add((exception, context));
    }
}
