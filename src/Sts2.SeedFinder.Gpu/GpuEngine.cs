using ILGPU;
using ILGPU.Runtime;

namespace Sts2.SeedFinder.Gpu;

/// <summary>What a probe found, in a shape the CLI and the web app can both report.</summary>
public sealed record GpuStatus(
    bool Available,
    string Backend,
    string DeviceName,
    string Detail)
{
    public static GpuStatus None(string detail) => new(false, "none", "-", detail);
}

/// <summary>
/// Owns the ILGPU context and the chosen accelerator, and decides whether there is a usable
/// GPU at all.
///
/// The whole class is optional by construction. A search never requires it: if
/// <see cref="TryCreate"/> returns null the caller runs the existing CPU path, which stays the
/// reference implementation. That matters more than it sounds, because the people most likely
/// to run this have laptops with an integrated GPU, a driver without an OpenCL runtime, or a
/// Mac where ILGPU has no backend at all.
/// </summary>
public sealed class GpuEngine : IDisposable
{
    private readonly Context _context;

    public Accelerator Accelerator { get; }
    public GpuStatus Status { get; }

    private GpuEngine(Context context, Accelerator accelerator, GpuStatus status)
    {
        _context = context;
        Accelerator = accelerator;
        Status = status;
    }

    /// <summary>
    /// <c>STS2_GPU</c>: <c>off</c> disables it, <c>cuda</c>/<c>opencl</c>/<c>cpu</c> force one
    /// backend, anything else (or unset) picks the best available.
    ///
    /// <c>cpu</c> selects ILGPU's own CPU accelerator, which exists so the kernels can be
    /// developed and differential-tested on a machine with no GPU. It is not a search path:
    /// for a real search with no GPU the ordinary <c>SeedSearcher</c> is both faster and
    /// simpler.
    /// </summary>
    private static string Preference =>
        (Environment.GetEnvironmentVariable("STS2_GPU") ?? "auto").Trim().ToLowerInvariant();

    /// <summary>
    /// Probe for a usable accelerator. Returns null rather than throwing for every reason a
    /// machine might not have one, and reports which reason in <paramref name="status"/>.
    /// </summary>
    public static GpuEngine? TryCreate(out GpuStatus status, bool allowCpuAccelerator = false)
    {
        string pref = Preference;
        if (pref == "off")
        {
            status = GpuStatus.None("disabled by STS2_GPU=off");
            return null;
        }

        Context? context = null;
        try
        {
            context = Context.Create(b => b.AllAccelerators().Optimize(OptimizationLevel.O2));

            // CUDA before OpenCL: on a machine with both, the discrete card is the one worth
            // having, and NVIDIA's OpenCL runtime is consistently slower than its own PTX path.
            var order = new[] { AcceleratorType.Cuda, AcceleratorType.OpenCL, AcceleratorType.CPU };
            var tried = new List<string>();

            foreach (var type in order)
            {
                if (type == AcceleratorType.CPU && !(allowCpuAccelerator || pref == "cpu")) continue;
                if (pref is "cuda" or "opencl" or "cpu" && !pref.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var device in context.Devices.Where(d => d.AcceleratorType == type))
                {
                    Accelerator? acc = null;
                    try
                    {
                        acc = device.CreateAccelerator(context);
                        if (!Fp64IsExact(acc))
                        {
                            tried.Add($"{type}/{device.Name}: no exact FP64");
                            acc.Dispose();
                            continue;
                        }

                        status = new GpuStatus(true, type.ToString(), device.Name,
                            $"{device.NumMultiprocessors} multiprocessors, {device.MemorySize / (1024 * 1024)} MB");
                        return new GpuEngine(context, acc, status);
                    }
                    catch (Exception ex)
                    {
                        tried.Add($"{type}/{device.Name}: {ex.GetType().Name}");
                        acc?.Dispose();
                    }
                }
            }

            context.Dispose();
            status = GpuStatus.None(tried.Count == 0
                ? "no CUDA or OpenCL device found"
                : "no usable device: " + string.Join("; ", tried));
            return null;
        }
        catch (Exception ex)
        {
            context?.Dispose();
            status = GpuStatus.None($"ILGPU could not start: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    /// <summary>
    /// Every bounded draw the game makes goes through <c>NextDouble</c>, so a device that
    /// lacks double precision, or rounds it differently, cannot reproduce a seed. Rather than
    /// infer that from a capability flag (OpenCL makes FP64 an optional extension, and some
    /// drivers advertise it while emulating it badly) this runs the actual expression and
    /// compares the bits.
    /// </summary>
    private static bool Fp64IsExact(Accelerator acc)
    {
        const int n = 64;
        using var buf = acc.Allocate1D<double>(n);
        var kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>>(static (i, v) =>
        {
            var rng = new GpuRandom((ulong)(i.X + 1) * 0x9E3779B97F4A7C15UL);
            v[i] = rng.NextDouble();
        });
        kernel(n, buf.View);
        acc.Synchronize();

        var got = buf.GetAsArray1D();
        for (int i = 0; i < n; i++)
        {
            var reference = new Core.MegaRandom(unchecked((ulong)(i + 1) * 0x9E3779B97F4A7C15UL));
            if (BitConverter.DoubleToInt64Bits(reference.NextDouble()) != BitConverter.DoubleToInt64Bits(got[i]))
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        Accelerator.Dispose();
        _context.Dispose();
    }
}
