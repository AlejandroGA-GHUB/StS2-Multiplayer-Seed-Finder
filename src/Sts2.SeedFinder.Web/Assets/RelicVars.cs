using System.Reflection;
using System.Text.RegularExpressions;
using System.Runtime.Loader;

namespace Sts2.SeedFinder.Web.Assets;

/// <summary>One value a relic's description can interpolate. <paramref name="Text"/> is
/// non-null only for string-valued vars, which are blank until a run fills them in.</summary>
public readonly record struct RelicVar(decimal Value, string? Text);

/// <summary>
/// The numbers inside relic descriptions, read out of the player's own <c>sts2.dll</c>.
///
/// The localization file only carries templates — "gain {Block} Block". The values live in the
/// assembly, as a <c>CanonicalVars</c> property on each relic class, so a description cannot be
/// rendered in full from the .pck alone. That is why descriptions used to read "gain X Block".
///
/// Getting the real number needs nothing more than constructing the model and asking it, which
/// works headless: <c>AbstractModel</c> is a plain class, its constructor only computes an id
/// and registers, and none of it touches Godot. No <c>ModelDb.Init</c>, no engine, no game
/// running.
///
/// It does need the model DATABASE populated first, though, which is the part that was missing.
/// Models resolve each other through it — Wood Carvings names a card, Tea Master a relic, Pael's
/// Claw an enchantment — so reading vars with only relics and cards constructed threw
/// <c>KeyNotFoundException</c> for every model that referenced a kind we had skipped. Injecting
/// every subtype first, exactly as <c>--refresh</c> does, is what makes those resolve. Costs
/// about a second at startup and is what turns "Pay X Gold" into "Pay 250 Gold".
///
/// A var whose value is a NAME is a separate problem, because the value itself is blank until the
/// game's localization manager fills it in and that only runs inside the engine. Worse, the whole
/// var set is built in one property getter, so a single name-valued var throws and costs that
/// model its NUMBERS too. <see cref="ReferencedModelIds"/> is the way round it: the id of the
/// thing being named is recoverable from the model's IL, and a title is one dictionary lookup
/// from an id. That closes every relic description, including Pael's Claw and Pael's Growth. What
/// it cannot recover is a number the game computes mid-run, which is most of what still reads X.
///
/// Same provenance boundary as the art and the strings (docs/web_app_specs.md section 4): read at
/// runtime from a copy of the game the user already owns, held in memory, never committed here
/// and never served from a machine that does not own the game. Loaded into its own
/// <see cref="AssemblyLoadContext"/> so the game's assemblies stay out of ours.
/// </summary>
/// <summary>
/// The values behind relic, card and event descriptions, keyed by slug.
/// </summary>
/// <param name="ModelRefs">
/// Which other models each model names, as game ids ("ENCHANTMENT.SOWN", "POTION.GLOWWATER_POTION"),
/// keyed by slug. This is how a placeholder that wants a NAME rather than a number gets filled:
/// the value itself is unreachable headless, but the id is, and a title can be looked up from the
/// localization tables we already read.
/// </param>
public readonly record struct ModelVars(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, RelicVar>> Relics,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, RelicVar>> Cards,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, RelicVar>> Events,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ModelRefs)
{
    public static ModelVars Empty { get; } = new(
        new Dictionary<string, IReadOnlyDictionary<string, RelicVar>>(),
        new Dictionary<string, IReadOnlyDictionary<string, RelicVar>>(),
        new Dictionary<string, IReadOnlyDictionary<string, RelicVar>>(),
        new Dictionary<string, IReadOnlyList<string>>());
}

public static partial class RelicVars
{
    /// <summary>slug to (var name to value), for relics and cards. Empty when the assembly cannot be read.</summary>
    public static ModelVars Read(string installDir)
    {
        var dataDir = Directory.EnumerateDirectories(installDir, "data_sts2_*")
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "sts2.dll")));
        if (dataDir is null) return ModelVars.Empty;

        try
        {
            return ReadCore(dataDir);
        }
        catch (Exception)
        {
            // A patch that reshapes the model layer must cost numbers, not the app. The
            // resolver falls back to "X" for anything missing.
            return ModelVars.Empty;
        }
    }

    private static ModelVars ReadCore(string dataDir)
    {
        var alc = new GameAssemblies(dataDir);
        var sts2 = alc.LoadFromAssemblyPath(Path.Combine(dataDir, "sts2.dll"));

        // The common base every model shares, and the database they register themselves into.
        // Populating it is what makes their cross-references resolvable.
        var abstractModel = sts2.GetType("MegaCrit.Sts2.Core.Models.AbstractModel");
        var modelDb = sts2.GetType("MegaCrit.Sts2.Core.Models.ModelDb");
        var relicModel = sts2.GetType("MegaCrit.Sts2.Core.Models.RelicModel");
        var cardModel = sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel");
        // Events keep their numbers the same way relics do, and their model type names are the
        // slugs we already use (ZenWeaver, ByrdonisNest), so nothing extra has to be mapped.
        var eventModel = sts2.GetType("MegaCrit.Sts2.Core.Models.EventModel");
        var stringHelper = sts2.GetType("MegaCrit.Sts2.Core.Helpers.StringHelper")
                           ?? sts2.GetTypes().FirstOrDefault(t => t.Name == "StringHelper");
        var slugify = stringHelper?.GetMethod("Slugify", BindingFlags.Public | BindingFlags.Static);
        if (slugify is null) return ModelVars.Empty;

        var relics = new Dictionary<string, IReadOnlyDictionary<string, RelicVar>>(StringComparer.OrdinalIgnoreCase);
        var cards = new Dictionary<string, IReadOnlyDictionary<string, RelicVar>>(StringComparer.OrdinalIgnoreCase);
        var events = new Dictionary<string, IReadOnlyDictionary<string, RelicVar>>(StringComparer.OrdinalIgnoreCase);

        // Populate the game's own model database FIRST, with everything, exactly as `--refresh`
        // does (see GameModels in the CLI). Models resolve each other through that registry in
        // their constructors and in their lazily built vars — Wood Carvings reaches for
        // CARD.PECK, Tea Master for RELIC.BONE_TEA, Pael's Claw for ENCHANTMENT.GOOPY — so
        // reading a relic's numbers without the database populated throws KeyNotFoundException
        // for every model that depends on one. That is what left 19 events and Pael's two relics
        // showing X. Injecting all of them is what makes those references resolvable, and it
        // needs no Godot and no live run.
        var inject = modelDb?.GetMethod("Inject", BindingFlags.Public | BindingFlags.Static);
        if (inject is not null && abstractModel is not null)
        {
            var pending = sts2.GetTypes()
                .Where(t => !t.IsAbstract && abstractModel.IsAssignableFrom(t))
                .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

            // Injecting CONSTRUCTS, and a constructor that reaches for another model needs that
            // model already registered. Metadata order is not dependency order, so a single pass
            // drops whatever came too early — Wood Carvings wants CARD.PECK, which is injected
            // after it, so it failed and never entered the registry at all. Retrying while any
            // model still succeeds resolves the graph without needing to know its shape. The
            // pass cap only exists so a genuine cycle cannot spin forever.
            for (int pass = 0; pass < 12 && pending.Count > 0; pass++)
            {
                var failed = new List<Type>();
                foreach (var type in pending)
                {
                    // A model that cannot be built headless is one nothing downstream can use,
                    // not a reason to abandon the rest.
                    try { inject.Invoke(null, [type]); }
                    catch { failed.Add(type); }
                }

                // No progress means the rest want something that will never arrive.
                if (failed.Count == pending.Count) break;
                pending = failed;
            }

            if (Environment.GetEnvironmentVariable("STS2_VARS_DEBUG") is not null)
                Console.WriteLine($"[vars] {pending.Count} models never injected");
        }

        // Then read the vars off the instances the database is now holding, rather than building
        // a second copy of each — AbstractModel's constructor registers, so constructing again
        // throws DuplicateModelException.
        var registry = modelDb?
            .GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null) as System.Collections.IDictionary;

        foreach (var model in registry?.Values ?? (System.Collections.ICollection)Array.Empty<object>())
        {
            if (model is null) continue;
            var type = model.GetType();

            var into = relicModel?.IsAssignableFrom(type) == true ? relics
                     : cardModel?.IsAssignableFrom(type) == true ? cards
                     : eventModel?.IsAssignableFrom(type) == true ? events
                     : null;
            if (into is null) continue;

            // Guarded per model: a var can still reach for state only a live run has, and that
            // must cost its own numbers rather than every model's.
            try
            {
                var vars = VarsOf(model);
                // Slugify uppercases, matching the localization keys; we key on the lowercase
                // form the rest of the app uses for slugs.
                if (vars.Count > 0)
                    into[((string)slugify.Invoke(null, [type.Name])!).ToLowerInvariant()] = vars;
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("STS2_VARS_DEBUG") is not null)
                {
                    var root = ex;
                    while (root.InnerException is { } inner) root = inner;
                    Console.WriteLine($"[vars] {type.Name}: {root.GetType().Name}: {root.Message}");
                }
            }
        }

        // Which models each event names. Read from IL rather than from the model, because the
        // property that would answer is the one that throws.
        var refs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in registry?.Values ?? (System.Collections.ICollection)Array.Empty<object>())
        {
            if (model is null) continue;
            var type = model.GetType();
            if (eventModel?.IsAssignableFrom(type) != true
                && relicModel?.IsAssignableFrom(type) != true) continue;

            try
            {
                var ids = ReferencedModelIds(type, abstractModel, slugify);
                if (ids.Count > 0)
                    refs[((string)slugify.Invoke(null, [type.Name])!).ToLowerInvariant()] = ids;
            }
            catch { /* no references readable for this one */ }
        }

        return new ModelVars(relics, cards, events, refs);
    }

    /// <summary>
    /// The game ids a model's own code mentions — "ENCHANTMENT.SOWN", "POTION.GLOWWATER_POTION" —
    /// pulled out of its IL as <c>ldstr</c> operands.
    ///
    /// Needed because a description that names something ("Enchant a card with {Enchantment}")
    /// cannot be filled from the model: the only property that would answer builds its whole set
    /// at once and throws without the engine's localization manager. The id, though, is a plain
    /// string constant sitting in the method that does the lookup, and from an id a title is one
    /// dictionary away.
    ///
    /// The id is NOT a string in the IL. The game looks models up generically —
    /// <c>ModelDb.Relic&lt;BoneTea&gt;()</c>, <c>Enchantment&lt;Sown&gt;()</c> — and derives the key
    /// from the type. So what is read here is the GENERIC ARGUMENT of each call, and the id is
    /// rebuilt from it the same way the game would: kind from the argument's model base class,
    /// name from <c>Slugify</c> of its type name.
    ///
    /// Scanning for the opcode byte rather than walking the instruction stream is the shortcut the
    /// Oracle takes for <c>ldc.r4</c>, and the tolerance is the same: a stray 0x28 inside another
    /// operand gives a token that either fails to resolve or resolves to something with no model
    /// generic argument, and both are dropped.
    /// </summary>
    private static IReadOnlyList<string> ReferencedModelIds(
        Type type, Type? abstractModel, MethodInfo slugify)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Instance | BindingFlags.Static
                                               | BindingFlags.DeclaredOnly))
        {
            byte[]? il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch { continue; }
            if (il is null) continue;

            for (int i = 0; i + 4 < il.Length; i++)
            {
                // call (0x28) and callvirt (0x6F) both carry a 4-byte method token.
                if (il[i] != 0x28 && il[i] != 0x6F) continue;

                MethodBase? called;
                try { called = type.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); }
                catch { continue; }
                if (called is null || !called.IsGenericMethod) continue;

                Type[] args;
                try { args = called.GetGenericArguments(); }
                catch { continue; }

                foreach (var arg in args)
                {
                    if (arg.IsGenericParameter || abstractModel?.IsAssignableFrom(arg) != true) continue;

                    var kind = ModelKindOf(arg, abstractModel);
                    if (kind is null) continue;

                    string name;
                    try { name = (string)slugify.Invoke(null, [arg.Name])!; }
                    catch { continue; }

                    var id = kind + "." + name;
                    if (seen.Add(id)) found.Add(id);
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The registry prefix a model type gets, taken from the nearest <c>*Model</c> base class:
    /// <c>Sown</c> derives from <c>EnchantmentModel</c>, so its id is <c>ENCHANTMENT.SOWN</c>.
    /// </summary>
    private static string? ModelKindOf(Type type, Type? abstractModel)
    {
        for (var t = type.BaseType; t is not null && t != abstractModel; t = t.BaseType)
            if (t.Name.EndsWith("Model", StringComparison.Ordinal) && t.Name.Length > "Model".Length)
                return t.Name[..^"Model".Length].ToUpperInvariant();
        return null;
    }

    private static Dictionary<string, RelicVar> VarsOf(object relic)
    {
        var found = new Dictionary<string, RelicVar>(StringComparer.OrdinalIgnoreCase);


        // DynamicVars is a lazily built IReadOnlyDictionary, so enumerating it directly yields
        // pairs; Values is the one that yields the vars themselves.
        var set = relic.GetType().GetProperty("DynamicVars")?.GetValue(relic);
        if (set?.GetType().GetProperty("Values")?.GetValue(set) is not System.Collections.IEnumerable values)
            return found;

        foreach (var v in values)
        {
            if (v is null) continue;
            var t = v.GetType();
            if (t.GetProperty("Name")?.GetValue(v) is not string name) continue;
            if (t.GetProperty("BaseValue")?.GetValue(v) is not decimal value) continue;

            found[name] = new RelicVar(value, t.GetProperty("StringValue")?.GetValue(v) as string);
        }
        return found;
    }

    /// <summary>
    /// Resolves the game's own assemblies out of its data directory, falling back to the
    /// default context for framework references. Returning null from <c>Load</c> is what
    /// delegates upward, so this never shadows our own copy of the BCL.
    /// </summary>
    private sealed class GameAssemblies(string dir) : AssemblyLoadContext("sts2-relic-vars")
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
