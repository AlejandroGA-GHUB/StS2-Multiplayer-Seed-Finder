using ILGPU;
using ILGPU.Runtime;
using Sts2.SeedFinder.Core;

namespace Sts2.SeedFinder.Gpu;

/// <summary>
/// The run stage's device buffers for one search, owned together.
///
/// Ten arrays have to be uploaded, kept alive for exactly as long as the scan, and freed
/// together. Three callers need that — the planner, the verifier and the benchmark — and three
/// copies of the same ten allocations is three chances for one of them to bind a view to a
/// buffer it has already disposed.
/// </summary>
public sealed class GpuRunStage : IDisposable
{
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _candidateCount;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _candidateOffset;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _info;
    private readonly MemoryBuffer1D<ulong, Stride1D.Dense> _conflict;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _bossId;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _ancientId;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _bossCriteria;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _eventCriteria;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _eventStartIndex;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _ancientCriteria;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _shopProbes;

    /// <summary>The scalars, ready to hand to a kernel launch.</summary>
    public RunFilterParams RunParams { get; }

    /// <summary>The stage's scalars wrapped as a whole-kernel argument, for a run-only scan.</summary>
    public SeedFilterParams Params => new() { Run = RunParams };

    public RunFilterView View => new(
        _candidateCount.View, _candidateOffset.View, _info.View, _conflict.View, _bossId.View,
        _ancientId.View, _bossCriteria.View, _eventCriteria.View, _eventStartIndex.View,
        _ancientCriteria.View, _shopProbes.View);

    /// <summary>The stage's views wrapped as a whole-kernel argument, for a run-only scan.</summary>
    public SeedFilterViews Views => new(default, default, default, View);

    /// <summary>Upload the tables for these criteria, or return null when the stage declines.</summary>
    public static GpuRunStage? TryCreate(GpuEngine engine, SearchCriteria criteria) =>
        RunFilterTables.TryBuild(criteria, out var tables) && tables is not null
            ? new GpuRunStage(engine, tables)
            : null;

    private GpuRunStage(GpuEngine engine, RunFilterTables tables)
    {
        var acc = engine.Accelerator;
        RunParams = tables.Params;

        _candidateCount = acc.Allocate1D(tables.CandidateCount);
        _candidateOffset = acc.Allocate1D(tables.CandidateOffset);
        _info = acc.Allocate1D(tables.Info);
        _conflict = acc.Allocate1D(tables.Conflict);
        _bossId = acc.Allocate1D(tables.BossId);

        // A criterion list can legitimately be empty, and a zero-length allocation is not a
        // bindable view. The stage reads none of these: every loop over them is bounded by a
        // count that is zero.
        _ancientId = acc.Allocate1D(NonEmpty(tables.AncientId));
        _bossCriteria = acc.Allocate1D(NonEmpty(tables.BossCriteria));
        _eventCriteria = acc.Allocate1D(NonEmpty(tables.EventCriteria));
        _eventStartIndex = acc.Allocate1D(NonEmpty(tables.EventStartIndex));
        _ancientCriteria = acc.Allocate1D(NonEmpty(tables.AncientCriteria));
        _shopProbes = acc.Allocate1D(NonEmpty(tables.ShopProbes));
    }

    private static int[] NonEmpty(int[] values) => values.Length > 0 ? values : new int[1];

    public void Dispose()
    {
        _candidateCount.Dispose();
        _candidateOffset.Dispose();
        _info.Dispose();
        _conflict.Dispose();
        _bossId.Dispose();
        _ancientId.Dispose();
        _bossCriteria.Dispose();
        _eventCriteria.Dispose();
        _eventStartIndex.Dispose();
        _ancientCriteria.Dispose();
        _shopProbes.Dispose();
    }
}
