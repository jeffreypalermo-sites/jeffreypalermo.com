namespace JeffreyPalermo.Core.Traffic;

/// <summary>
/// One running instance's count of the requests it answered in the last minute (ADR-0025). It lives in the
/// process's memory and nowhere else: a new instance starts at zero.
/// <para>
/// The minute is sixty slots of one second, the running second and the 59 before it, so the window is between 59
/// and 60 seconds long. In the first minute after the start it holds what was answered since the start: the count
/// is never scaled up to a full minute.
/// </para>
/// <para>
/// Counting takes no lock and starts no timer: an idle instance does nothing here. A request increments two or
/// three numbers of its second's slot. The first request of a second replaces the slot that held a second a minute
/// or more ago, which is the only memory ever asked for: about 0.5 KB in a second that has requests, none per request.
/// </para>
/// <para>
/// The 95th percentile comes from a histogram of fixed bounds (<see cref="DurationBounds"/>), not from the
/// durations themselves: the true value and the reported one lie between the same two neighbouring bounds, which
/// are at most a factor 1.5 apart from 2 ms on. Within them the value is placed in proportion to the requests
/// counted there. A request that took longer than the last bound counts as that bound.
/// </para>
/// </summary>
public sealed class RequestTally
{
    /// <summary>The length of the window.</summary>
    public const int WindowSeconds = 60;

    /// <summary>Upper bounds of the histogram's classes, in milliseconds. The last class has no upper bound.</summary>
    private static readonly int[] DurationBounds =
    [
        1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 120, 150, 200, 250, 300, 400, 500, 600, 800,
        1000, 1200, 1500, 2000, 2500, 3000, 4000, 5000, 6000, 8000, 10000, 15000, 20000, 30000, 45000, 60000,
    ];

    private readonly TimeProvider _time;
    private readonly long _startedTimestamp;
    private readonly Second?[] _seconds = new Second?[WindowSeconds];

    public RequestTally(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _startedTimestamp = time.GetTimestamp();
        StartedAt = time.GetUtcNow();
    }

    /// <summary>When this instance began to count: the start of the process.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Counts one answered request.</summary>
    /// <param name="kind">What the request was.</param>
    /// <param name="statusCode">The status it was answered with.</param>
    /// <param name="duration">How long the answer took. Kept for traffic only.</param>
    public void Record(RequestKind kind, int statusCode, TimeSpan duration)
    {
        var second = SecondOf(Now());
        switch (kind)
        {
            case RequestKind.Check:
                Interlocked.Increment(ref second.Checks);
                return;
            case RequestKind.FrontDoorProbe:
                Interlocked.Increment(ref second.FrontDoorProbes);
                return;
            case RequestKind.FrontDoorTraffic:
                Interlocked.Increment(ref second.FrontDoorTraffic);
                break;
            default:
                Interlocked.Increment(ref second.DirectTraffic);
                break;
        }

        if (statusCode >= 500)
        {
            Interlocked.Increment(ref second.Errors);
        }

        Interlocked.Increment(ref second.Durations[ClassOf(duration)]);
    }

    /// <summary>What was answered in the window that ends now.</summary>
    public RequestCounts Read()
    {
        var now = Now();
        long frontDoor = 0, direct = 0, errors = 0, checks = 0, probes = 0;
        var durations = new long[DurationBounds.Length + 1];
        foreach (var slot in _seconds)
        {
            // A slot nobody replaced still holds a second that has left the window.
            if (slot is null || slot.Number <= now - WindowSeconds || slot.Number > now)
            {
                continue;
            }

            frontDoor += Interlocked.Read(ref slot.FrontDoorTraffic);
            direct += Interlocked.Read(ref slot.DirectTraffic);
            errors += Interlocked.Read(ref slot.Errors);
            checks += Interlocked.Read(ref slot.Checks);
            probes += Interlocked.Read(ref slot.FrontDoorProbes);
            for (var index = 0; index < durations.Length; index++)
            {
                durations[index] += Interlocked.Read(ref slot.Durations[index]);
            }
        }

        return new RequestCounts(frontDoor, direct, errors, Percentile95(durations), checks, probes);
    }

    private long Now() => (long)_time.GetElapsedTime(_startedTimestamp).TotalSeconds;

    /// <summary>
    /// The slot of the second <paramref name="number"/>. A slot is never emptied in place: the first request of a
    /// new second puts a new one where the old stood, and whoever loses that race counts in the winner's. So no
    /// request is lost and none is counted in a second it did not belong to.
    /// </summary>
    private Second SecondOf(long number)
    {
        ref var place = ref _seconds[number % WindowSeconds];
        while (true)
        {
            var current = Volatile.Read(ref place);
            if (current is not null && current.Number >= number)
            {
                return current;
            }

            var fresh = new Second(number);
            if (ReferenceEquals(Interlocked.CompareExchange(ref place, fresh, current), current))
            {
                return fresh;
            }
        }
    }

    private static int ClassOf(TimeSpan duration)
    {
        var milliseconds = duration.TotalMilliseconds;
        int low = 0, high = DurationBounds.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (milliseconds <= DurationBounds[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private static int Percentile95(long[] durations)
    {
        var total = durations.Sum();
        if (total == 0)
        {
            return 0;
        }

        // The rank of the request at the 95th percentile, counted from the fastest: the 95th of 100, the 1st of 1.
        var rank = (long)Math.Ceiling(total * 0.95);
        long before = 0;
        for (var index = 0; index < DurationBounds.Length; index++)
        {
            var here = durations[index];
            if (before + here >= rank)
            {
                var lower = index == 0 ? 0 : DurationBounds[index - 1];
                var share = (double)(rank - before) / here;
                return (int)Math.Ceiling(lower + ((DurationBounds[index] - lower) * share));
            }

            before += here;
        }

        return DurationBounds[^1];
    }

    private sealed class Second(long number)
    {
        // Fields, not properties: Interlocked counts in the field itself.
        public readonly long Number = number;
        public readonly long[] Durations = new long[DurationBounds.Length + 1];
        public long FrontDoorTraffic;
        public long DirectTraffic;
        public long Errors;
        public long Checks;
        public long FrontDoorProbes;
    }
}
