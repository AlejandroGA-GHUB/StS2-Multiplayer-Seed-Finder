using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Host side of the whole GPU search: feeds the seed space through <see cref="SeedFilter"/> in
/// tiles and yields the indices that survived, in ascending order.
///
/// Stage-agnostic, despite having started life as the Neow pre-filter and having been called
/// GpuNeowSearch for it. Everything here is about running a scan safely rather than about what
/// the scan is looking for: tiling, the adaptive launch budget that keeps a launch under the
/// driver watchdog, the hit-buffer overflow retry, and the fallback when a device disappears
/// mid-scan. Which criteria are being tested lives entirely in the kernel it launches.
///
/// Ordering is not free (the kernel appends atomically, so a tile comes back shuffled) but it
/// is worth a sort per tile: it keeps a GPU search resumable and reproducible in exactly the
/// way <c>SeedCodec</c>'s index enumeration was designed to be, and the sort is a rounding
/// error next to the scan that produced it.
/// </summary>
public sealed class GpuSeedScan : IDisposable
{
    /// <summary>
    /// The LARGEST a tile may grow to. Big enough that launch overhead disappears, small enough
    /// that a cancelled search stops promptly and a tile's hits fit in the buffer below.
    /// </summary>
    public const long DefaultTileSize = 64L * 1024 * 1024;

    /// <summary>
    /// How long one launch is allowed to take, in seconds.
    ///
    /// This is the whole reason tiles are timed rather than fixed. Windows watchdogs the GPU:
    /// a single launch that does not return within about two seconds is assumed hung, and the
    /// display driver is RESET. Everything on the device dies with it, including whatever else
    /// the user has open, which for this tool very plausibly means the game they are finding a
    /// seed for.
    ///
    /// A fixed 64M tile invites exactly that. The same tile is 0.06s of Neow work and 1.7s of
    /// run generation on a fast discrete card, and run generation on integrated graphics would
    /// be tens of seconds — a guaranteed reset on the machines least able to afford one. Half a
    /// second leaves a wide margin under the default timeout on any device, and the tile adapts
    /// to whatever the hardware turns out to be rather than to what we guessed it was.
    /// </summary>
    private const double LaunchBudgetSeconds = 0.5;

    /// <summary>
    /// What the first launch of a scan uses, before there is any measurement to go on.
    ///
    /// Deliberately tiny. It has to be safe on the slowest device running the most expensive
    /// stage, since being wrong in that direction is what this is all here to prevent, and the
    /// cost of being wrong the other way is one short launch before the tile adapts upward.
    /// </summary>
    private const long ProbeTileSize = 1L << 18;

    /// <summary>Floor for the adaptive tile, so a slow device still makes progress per launch.</summary>
    private const long MinTileSize = 1L << 16;

    /// <summary>
    /// Seeds each thread walks serially. Measured to matter: at one seed per thread the launch
    /// is scheduling-bound rather than arithmetic-bound.
    /// </summary>
    private const int PerThread = 64;

    /// <summary>4M hits per tile. Overflow is handled rather than tolerated, see <see cref="Scan"/>.</summary>
    public const int DefaultHitCapacity = 1 << 22;

    private readonly GpuEngine _engine;
    private readonly Action<Index1D, ulong, int, long, SeedFilterParams, SeedFilterViews,
        ArrayView<ulong>, ArrayView<int>> _kernel;
    private readonly MemoryBuffer1D<ulong, Stride1D.Dense> _hits;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _counter;
    private readonly int _hitCapacity;

    /// <summary>
    /// One-element stand-ins for the card stage when a search does not use it.
    ///
    /// ILGPU will not accept an unbound <c>ArrayView</c>, and a kernel parameter cannot be
    /// omitted, so an inactive stage still needs something to point at. Allocating these once
    /// beats allocating them per search, and they are never read: the stage is gated on its
    /// <c>Active</c> flag before any view is touched.
    /// </summary>
    private readonly MemoryBuffer1D<byte, Stride1D.Dense> _noBytes;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _noInts;
    private readonly MemoryBuffer1D<ulong, Stride1D.Dense> _noULongs;

    private ActFilterView EmptyActs => new(_noInts.View, _noInts.View, _noBytes.View);

    private CardPoolView EmptyPools => new(
        _noBytes.View, _noInts.View, _noInts.View, _noInts.View, _noInts.View, _noInts.View);

    private CardCriteriaView EmptyCriteria => new(
        _noInts.View, _noInts.View, _noInts.View, _noInts.View);

    private RunFilterView EmptyRun => new(
        _noInts.View, _noInts.View, _noInts.View, _noULongs.View,
        _noInts.View, _noInts.View, _noInts.View, _noInts.View, _noInts.View, _noInts.View,
        _noInts.View);

    /// <summary>
    /// <paramref name="hitCapacity"/> is settable only so the verifier can force the overflow
    /// path with a buffer small enough to overrun on purpose. Nothing else should change it:
    /// the retry below is correct at any size, but a small buffer turns a dense search into a
    /// long sequence of halvings.
    /// </summary>
    public GpuSeedScan(GpuEngine engine, int hitCapacity = DefaultHitCapacity)
    {
        _engine = engine;
        _hitCapacity = hitCapacity;
        _kernel = engine.Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ulong, int, long, SeedFilterParams, SeedFilterViews,
            ArrayView<ulong>, ArrayView<int>>(SeedFilter.Kernel);
        _hits = engine.Accelerator.Allocate1D<ulong>(hitCapacity);
        _counter = engine.Accelerator.Allocate1D<int>(1);
        _noBytes = engine.Accelerator.Allocate1D<byte>(1);
        _noInts = engine.Accelerator.Allocate1D<int>(1);
        _noULongs = engine.Accelerator.Allocate1D<ulong>(1);
    }

    /// <summary>
    /// Scan <paramref name="count"/> seeds from <paramref name="start"/>, yielding matches.
    ///
    /// A tile whose hits overflow the buffer is retried at half the span rather than accepted,
    /// because the alternative is a silent false negative, and a search that quietly drops
    /// valid seeds is worse than one that is slow. Dense criteria (a single player wanting one
    /// relic hits about one seed in nine) reach this readily.
    /// </summary>
    public IEnumerable<ulong> Scan(
        NeowPrefilterParams p,
        ulong start,
        long count,
        CancellationToken cancellationToken = default,
        long tileSize = DefaultTileSize) =>
        Scan(new SeedFilterParams { Neow = p }, default, start, count, cancellationToken, tileSize);

    /// <summary>
    /// The full form, with every ported stage. A stage whose <c>Active</c> flag is zero is
    /// skipped and its views are never read, so callers using one stage pass stand-ins for the
    /// others.
    /// </summary>
    public IEnumerable<ulong> Scan(
        SeedFilterParams p,
        SeedFilterViews v,
        ulong start,
        long count,
        CancellationToken cancellationToken = default,
        long tileSize = DefaultTileSize,
        SearchProgress? progress = null)
    {
        // A caller with no use for a stage naturally passes `default`, which is an UNBOUND view
        // and not something a kernel launch will accept. Substituting the stand-ins here rather
        // than making every caller remember them keeps the mistake impossible instead of merely
        // documented. An inactive stage never reads these; they only have to be bindable.
        v = new SeedFilterViews(
            v.Acts.Accept.IsValid ? v.Acts : EmptyActs,
            v.Pools.Rarity.IsValid ? v.Pools : EmptyPools,
            v.Cards.Slot.IsValid ? v.Cards : EmptyCriteria,
            v.Run.Info.IsValid ? v.Run : EmptyRun);

        long maxTile = tileSize;
        long tile = Math.Min(ProbeTileSize, maxTile);
        long done = 0;

        // The largest tile the hit buffer has not overrun. Starts unbounded and only ever
        // tightens, so the size-from-rate step below cannot keep re-proposing a span that
        // density has already rejected.
        long densityCap = long.MaxValue;

        while (done < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long span = Math.Min(tile, count - done);
            ulong tileStart = start + (ulong)done;

            if (!DeviceLost)
            {
                var result = Launch(tileStart, span, p, v, out int found, out double seconds);

                if (result == Launched.Overflowed)
                {
                    // Too dense for this tile size. Halve and redo the same span; never advance,
                    // or the hits past the buffer's end are lost for good.
                    tile = Math.Max(PerThread, span / 2);
                    densityCap = tile;
                    continue;
                }

                if (result == Launched.Ok)
                {
                    tile = NextTile(span, seconds, maxTile, densityCap);

                    // Reported before the hits are yielded, not after. The consumer may spend a
                    // long time on this batch, and it has already been scanned; holding the
                    // number back until it comes back would make the device look idle.
                    progress?.Advance(span);

                    if (found > 0)
                    {
                        var batch = _hits.View.SubView(0, found).GetAsArray1D();
                        Array.Sort(batch);
                        foreach (var index in batch) yield return index;
                    }

                    done += span;
                    continue;
                }

                DeviceLost = true;
            }

            // The device went away mid-scan — a driver reset, an update, another process. Rather
            // than fail the search or quietly return a short answer, hand the caller every
            // remaining index. It puts all of them through the same criteria chain it was going
            // to run on the survivors anyway, so the RESULT stays complete and only the speed
            // changes. Silently returning fewer seeds than were asked for is the one outcome
            // this design must never produce.
            progress?.Advance(span);
            for (long i = 0; i < span; i++) yield return tileStart + (ulong)i;
            done += span;
        }
    }

    /// <summary>Whether a launch has failed and the device has been given up on for this object.</summary>
    public bool DeviceLost { get; private set; }

    /// <summary>Why, when it has. Null while the device is healthy.</summary>
    public string? DeviceLostReason { get; private set; }

    private enum Launched { Ok, Overflowed, Failed }

    /// <summary>
    /// One kernel launch, timed, with any failure turned into a value rather than an exception.
    ///
    /// Broad catch on purpose. Everything that can go wrong here — a reset driver, a lost
    /// context, an out-of-memory on a device someone else is also using — has the same right
    /// answer, which is to stop using the GPU and let the CPU finish the search. Letting any of
    /// it escape would take down a search that is perfectly able to complete without a device.
    /// </summary>
    private Launched Launch(
        ulong tileStart, long span, SeedFilterParams p, SeedFilterViews v,
        out int found, out double seconds)
    {
        found = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _counter.MemSetToZero();
            int threads = (int)((span + PerThread - 1) / PerThread);
            _kernel(threads, tileStart, PerThread, span, p, v, _hits.View, _counter.View);
            _engine.Accelerator.Synchronize();
            found = _counter.GetAsArray1D()[0];
        }
        catch (Exception ex)
        {
            DeviceLostReason = ex.Message;
            seconds = watch.Elapsed.TotalSeconds;
            return Launched.Failed;
        }

        seconds = watch.Elapsed.TotalSeconds;
        return found > _hitCapacity ? Launched.Overflowed : Launched.Ok;
    }

    /// <summary>
    /// The next tile size: whatever the measured rate says fits the launch budget, held under
    /// both the caller's ceiling and whatever the hit buffer has proved it can take.
    ///
    /// Sized from the rate rather than crept up to by doubling, because the probe is deliberately
    /// tiny and doubling from it would spend most of a short scan ramping — measured at 13% off
    /// the top on a fast card, which is a lot to pay for information one launch already gave us.
    ///
    /// The overflow ceiling is what makes jumping safe. Hit density varies across the space, so a
    /// tile that fitted the buffer here can overrun it there; without a memory of that, timing
    /// would keep proposing a size density keeps rejecting and the two would fight. With it, the
    /// halving above is remembered and the jump simply respects it.
    /// </summary>
    private static long NextTile(long span, double seconds, long maxTile, long densityCap)
    {
        // Guard the divide: a launch too fast to time would otherwise scale the tile by infinity.
        double rate = seconds > 1e-6 ? span / seconds : double.MaxValue;
        long target = rate >= maxTile / LaunchBudgetSeconds
            ? maxTile
            : (long)(rate * LaunchBudgetSeconds);

        // The density ceiling outranks the floor. A buffer that has already overrun at some span
        // cannot take a larger one just because a minimum says so, and clamping to a range whose
        // minimum exceeds its maximum throws outright — which the forced-overflow check found.
        long upper = Math.Min(maxTile, densityCap);
        return Math.Clamp(target, Math.Min(MinTileSize, upper), upper);
    }

    public void Dispose()
    {
        _hits.Dispose();
        _counter.Dispose();
        _noBytes.Dispose();
        _noInts.Dispose();
        _noULongs.Dispose();
    }
}
