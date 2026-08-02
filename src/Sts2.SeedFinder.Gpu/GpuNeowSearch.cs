using ILGPU;
using ILGPU.Runtime;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// Host side of the Neow pre-filter: feeds the seed space through the kernel in tiles and
/// yields the indices that survived, in ascending order.
///
/// Ordering is not free (the kernel appends atomically, so a tile comes back shuffled) but it
/// is worth a sort per tile: it keeps a GPU search resumable and reproducible in exactly the
/// way <c>SeedCodec</c>'s index enumeration was designed to be, and the sort is a rounding
/// error next to the scan that produced it.
/// </summary>
public sealed class GpuNeowSearch : IDisposable
{
    /// <summary>
    /// Seeds per launch. Big enough that launch overhead disappears, small enough that a
    /// cancelled search stops promptly and a tile's hits fit in the buffer below.
    /// </summary>
    public const long DefaultTileSize = 64L * 1024 * 1024;

    /// <summary>
    /// Seeds each thread walks serially. Measured to matter: at one seed per thread the launch
    /// is scheduling-bound rather than arithmetic-bound.
    /// </summary>
    private const int PerThread = 64;

    /// <summary>4M hits per tile. Overflow is handled rather than tolerated, see <see cref="Scan"/>.</summary>
    public const int DefaultHitCapacity = 1 << 22;

    private readonly GpuEngine _engine;
    private readonly Action<Index1D, ulong, int, long, ActFilterParams, ActFilterView,
        NeowPrefilterParams, CardFilterParams, CardPoolView, CardCriteriaView,
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

    private ActFilterView EmptyActs => new(_noInts.View, _noInts.View, _noBytes.View);

    private CardPoolView EmptyPools => new(
        _noBytes.View, _noInts.View, _noInts.View, _noInts.View, _noInts.View, _noInts.View);

    private CardCriteriaView EmptyCriteria => new(
        _noInts.View, _noInts.View, _noInts.View, _noInts.View);

    /// <summary>
    /// <paramref name="hitCapacity"/> is settable only so the verifier can force the overflow
    /// path with a buffer small enough to overrun on purpose. Nothing else should change it:
    /// the retry below is correct at any size, but a small buffer turns a dense search into a
    /// long sequence of halvings.
    /// </summary>
    public GpuNeowSearch(GpuEngine engine, int hitCapacity = DefaultHitCapacity)
    {
        _engine = engine;
        _hitCapacity = hitCapacity;
        _kernel = engine.Accelerator.LoadAutoGroupedStreamKernel<
            Index1D, ulong, int, long, ActFilterParams, ActFilterView,
            NeowPrefilterParams, CardFilterParams, CardPoolView, CardCriteriaView,
            ArrayView<ulong>, ArrayView<int>>(SeedFilter.Kernel);
        _hits = engine.Accelerator.Allocate1D<ulong>(hitCapacity);
        _counter = engine.Accelerator.Allocate1D<int>(1);
        _noBytes = engine.Accelerator.Allocate1D<byte>(1);
        _noInts = engine.Accelerator.Allocate1D<int>(1);
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
        Scan(default, EmptyActs, p, default, EmptyPools, EmptyCriteria, start, count, cancellationToken, tileSize);

    /// <summary>
    /// The full form, with every ported stage. A stage whose <c>Active</c> flag is zero is
    /// skipped and its views are never read, so callers using one stage pass stand-ins for the
    /// other.
    /// </summary>
    public IEnumerable<ulong> Scan(
        ActFilterParams actParams,
        ActFilterView acts,
        NeowPrefilterParams p,
        CardFilterParams cards,
        CardPoolView pools,
        CardCriteriaView criteria,
        ulong start,
        long count,
        CancellationToken cancellationToken = default,
        long tileSize = DefaultTileSize)
    {
        // A caller with no use for a stage naturally passes `default`, which is an UNBOUND view
        // and not something a kernel launch will accept. Substituting the stand-ins here rather
        // than making every caller remember them keeps the mistake impossible instead of merely
        // documented. An inactive stage never reads these; they only have to be bindable.
        if (!acts.Accept.IsValid) acts = EmptyActs;
        if (!pools.Rarity.IsValid) pools = EmptyPools;
        if (!criteria.Slot.IsValid) criteria = EmptyCriteria;

        long tile = tileSize;
        long done = 0;

        while (done < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long span = Math.Min(tile, count - done);
            ulong tileStart = start + (ulong)done;

            _counter.MemSetToZero();
            int threads = (int)((span + PerThread - 1) / PerThread);
            _kernel(threads, tileStart, PerThread, span, actParams, acts, p, cards, pools, criteria,
                _hits.View, _counter.View);
            _engine.Accelerator.Synchronize();

            int found = _counter.GetAsArray1D()[0];
            if (found > _hitCapacity)
            {
                // Too dense for this tile size. Halve and redo the same span; never advance,
                // or the hits past the buffer's end are lost for good.
                tile = Math.Max(PerThread, span / 2);
                continue;
            }

            if (found > 0)
            {
                var batch = _hits.View.SubView(0, found).GetAsArray1D();
                Array.Sort(batch);
                foreach (var index in batch) yield return index;
            }

            done += span;
        }
    }

    public void Dispose()
    {
        _hits.Dispose();
        _counter.Dispose();
        _noBytes.Dispose();
        _noInts.Dispose();
    }
}
