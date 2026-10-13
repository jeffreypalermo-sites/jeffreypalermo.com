using JeffreyPalermo.Core.Traffic;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>
/// One instance's count of what it answered in the last minute (ADR-0025): what counts as what, how the minute
/// moves, what the 95th percentile is worth, and that requests answered at the same time are all counted.
/// </summary>
public class RequestTallyTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(3);

    private readonly ManualTime _time = new();

    [Fact]
    public void NothingAnsweredIsAllZeros()
    {
        var tally = new RequestTally(_time);

        Assert.Equal(new RequestCounts(0, 0, 0, 0, 0, 0), tally.Read());
        Assert.Equal(60, RequestTally.WindowSeconds);
    }

    [Fact]
    public void ItSaysWhenItStarted()
    {
        var tally = new RequestTally(_time);
        _time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(ManualTime.Start, tally.StartedAt);
    }

    [Fact]
    public void EachRequestIsCountedAsItsKind()
    {
        var tally = new RequestTally(_time);

        Record(tally, RequestKind.FrontDoorTraffic, 3);
        Record(tally, RequestKind.DirectTraffic, 2);
        Record(tally, RequestKind.Check, 5);
        Record(tally, RequestKind.FrontDoorProbe, 7);

        var counts = tally.Read();
        Assert.Equal(3, counts.FrontDoorTraffic);
        Assert.Equal(2, counts.DirectTraffic);
        Assert.Equal(5, counts.Traffic);
        Assert.Equal(5, counts.Checks);
        Assert.Equal(7, counts.FrontDoorProbes);
        Assert.Equal(0, counts.Errors);
    }

    /// <summary>"Not found" and "gone" are answers this site gives on purpose; only a status from 500 on is an error.</summary>
    [Theory]
    [InlineData(200, 0)]
    [InlineData(301, 0)]
    [InlineData(304, 0)]
    [InlineData(400, 0)]
    [InlineData(404, 0)]
    [InlineData(410, 0)]
    [InlineData(499, 0)]
    [InlineData(500, 1)]
    [InlineData(502, 1)]
    [InlineData(503, 1)]
    public void OnlyTrafficAnsweredFrom500OnIsAnError(int statusCode, int errors)
    {
        var tally = new RequestTally(_time);

        tally.Record(RequestKind.FrontDoorTraffic, statusCode, Fast);
        tally.Record(RequestKind.DirectTraffic, statusCode, Fast);

        Assert.Equal(2 * errors, tally.Read().Errors);
        Assert.Equal(2, tally.Read().Traffic);
    }

    [Fact]
    public void ACheckThatFailsIsNotAnErrorOfTheTraffic()
    {
        var tally = new RequestTally(_time);

        tally.Record(RequestKind.Check, 500, Fast);
        tally.Record(RequestKind.FrontDoorProbe, 503, Fast);

        Assert.Equal(0, tally.Read().Errors);
        Assert.Equal(0, tally.Read().Traffic);
    }

    [Fact]
    public void ARequestLeavesTheCountAMinuteAfterItWasAnswered()
    {
        var tally = new RequestTally(_time);
        tally.Record(RequestKind.DirectTraffic, 200, Fast);

        _time.Advance(TimeSpan.FromSeconds(59.9));
        Assert.Equal(1, tally.Read().Traffic);

        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(new RequestCounts(0, 0, 0, 0, 0, 0), tally.Read());
    }

    [Fact]
    public void TheMinuteMovesSecondBySecond()
    {
        var tally = new RequestTally(_time);
        Record(tally, RequestKind.FrontDoorTraffic, 4);
        _time.Advance(TimeSpan.FromSeconds(30));
        Record(tally, RequestKind.FrontDoorTraffic, 2);
        Record(tally, RequestKind.Check, 1);

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(6, tally.Read().FrontDoorTraffic);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, tally.Read().FrontDoorTraffic);
        Assert.Equal(1, tally.Read().Checks);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, tally.Read().FrontDoorTraffic);
        Assert.Equal(0, tally.Read().Checks);
    }

    /// <summary>The first minute holds what was answered since the start. It is not scaled up to a full minute.</summary>
    [Fact]
    public void InTheFirstMinuteTheCountIsWhatWasAnsweredSinceTheStart()
    {
        var tally = new RequestTally(_time);
        _time.Advance(TimeSpan.FromSeconds(5));
        Record(tally, RequestKind.DirectTraffic, 10);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(10, tally.Read().Traffic);
    }

    /// <summary>Second 0 and second 60 share a place in the window; hours of silence must not bring old counts back.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(3600)]
    [InlineData(86_400 * 30)]
    public void ASecondThatComesAroundAgainStartsEmpty(int secondsLater)
    {
        var tally = new RequestTally(_time);
        Record(tally, RequestKind.DirectTraffic, 9);
        Record(tally, RequestKind.Check, 9);

        _time.Advance(TimeSpan.FromSeconds(secondsLater));
        Assert.Equal(new RequestCounts(0, 0, 0, 0, 0, 0), tally.Read());

        tally.Record(RequestKind.DirectTraffic, 200, Fast);
        Assert.Equal(1, tally.Read().Traffic);
        Assert.Equal(0, tally.Read().Checks);
    }

    [Fact]
    public void ASteadyStreamIsCountedOverExactlyTheLastSixtySeconds()
    {
        var tally = new RequestTally(_time);

        // Two requests a second for five minutes, as the dashboard's traffic button sends them.
        for (var second = 0; second < 300; second++)
        {
            Record(tally, RequestKind.FrontDoorTraffic, 2);
            Assert.Equal(Math.Min(second + 1, 60) * 2, tally.Read().FrontDoorTraffic);
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void TheNinetyFifthPercentileOfOneRequestIsThatRequestsClass()
    {
        var tally = new RequestTally(_time);

        tally.Record(RequestKind.FrontDoorTraffic, 200, TimeSpan.FromMilliseconds(85));

        Assert.InRange(tally.Read().P95Milliseconds, 81, 100);
    }

    /// <summary>
    /// The method's promise: the reported value lies between the same two bounds as the true one, which are at most
    /// a factor 1.5 apart. Ninety-five requests take the fast time and five the slow one, so the true 95th
    /// percentile is the fast time.
    /// </summary>
    [Theory]
    [InlineData(2.5, 3000)]
    [InlineData(7, 9000)]
    [InlineData(85, 400)]
    [InlineData(950, 20_000)]
    [InlineData(4200, 59_000)]
    public void TheNinetyFifthPercentileIsWithinAFactorOfOneAndAHalf(double fastMilliseconds, double slowMilliseconds)
    {
        var tally = new RequestTally(_time);
        for (var request = 0; request < 95; request++)
        {
            tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(fastMilliseconds));
        }

        for (var request = 0; request < 5; request++)
        {
            tally.Record(RequestKind.FrontDoorTraffic, 200, TimeSpan.FromMilliseconds(slowMilliseconds));
        }

        var reported = tally.Read().P95Milliseconds;
        Assert.InRange(reported, fastMilliseconds / 1.5, fastMilliseconds * 1.5);
        Assert.True(reported < slowMilliseconds, $"The slowest five of a hundred decided the 95th percentile: {reported} ms.");
    }

    /// <summary>The histogram cannot tell 130 ms from 131 ms: both are "over 120, up to 150", and so is the answer.</summary>
    [Fact]
    public void RequestsOfOneClassAreReportedWithinThatClass()
    {
        var tally = new RequestTally(_time);
        for (var request = 0; request < 100; request++)
        {
            tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(request < 95 ? 130 : 131));
        }

        Assert.InRange(tally.Read().P95Milliseconds, 121, 150);
    }

    [Fact]
    public void TheSlowestSixOfAHundredDecideTheNinetyFifthPercentile()
    {
        var tally = new RequestTally(_time);
        for (var request = 0; request < 94; request++)
        {
            tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(4));
        }

        for (var request = 0; request < 6; request++)
        {
            tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(700));
        }

        Assert.InRange(tally.Read().P95Milliseconds, 601, 800);
    }

    [Fact]
    public void ARequestSlowerThanTheLastBoundCountsAsTheLastBound()
    {
        var tally = new RequestTally(_time);

        tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMinutes(10));

        Assert.Equal(60_000, tally.Read().P95Milliseconds);
    }

    [Fact]
    public void TheNinetyFifthPercentileIsOfTheTrafficNotOfTheChecks()
    {
        var tally = new RequestTally(_time);
        tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(4));
        for (var check = 0; check < 50; check++)
        {
            tally.Record(RequestKind.Check, 200, TimeSpan.FromSeconds(9));
            tally.Record(RequestKind.FrontDoorProbe, 200, TimeSpan.FromSeconds(9));
        }

        Assert.InRange(tally.Read().P95Milliseconds, 4, 4);
    }

    [Fact]
    public void TheNinetyFifthPercentileForgetsWithTheMinute()
    {
        var tally = new RequestTally(_time);
        tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(60));
        tally.Record(RequestKind.DirectTraffic, 200, TimeSpan.FromMilliseconds(4));

        Assert.Equal(4, tally.Read().P95Milliseconds);
    }

    /// <summary>
    /// Requests answered on many threads while the seconds turn over: none is lost and none is counted twice. The
    /// clock moves 40 seconds in all, so every request is still inside the minute at the end.
    /// </summary>
    [Fact]
    public async Task RequestsAnsweredAtTheSameTimeAreAllCounted()
    {
        const int Threads = 8;
        const int Each = 200_000;
        var tally = new RequestTally(_time);
        using var done = new CancellationTokenSource();
        var clock = Task.Run(() =>
        {
            for (var tick = 0; tick < 4000 && !done.IsCancellationRequested; tick++)
            {
                _time.Advance(TimeSpan.FromMilliseconds(10));
                Thread.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, Threads).Select(thread => Task.Run(() =>
        {
            for (var request = 0; request < Each; request++)
            {
                var kind = (RequestKind)((thread + request) % 4);
                tally.Record(kind, request % 10 == 0 ? 500 : 200, Fast);
                if (request % 1000 == 0)
                {
                    _ = tally.Read();
                }
            }
        })));
        await done.CancelAsync();
        await clock;

        var counts = tally.Read();
        const long OfEachKind = Threads * Each / 4;
        Assert.Equal(OfEachKind, counts.FrontDoorTraffic);
        Assert.Equal(OfEachKind, counts.DirectTraffic);
        Assert.Equal(OfEachKind, counts.Checks);
        Assert.Equal(OfEachKind, counts.FrontDoorProbes);
        Assert.Equal(Threads * Each / 10 / 2, counts.Errors);
        Assert.Equal(3, counts.P95Milliseconds);
    }

    private static void Record(RequestTally tally, RequestKind kind, int times)
    {
        for (var request = 0; request < times; request++)
        {
            tally.Record(kind, 200, Fast);
        }
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
