using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

namespace Sts2.SeedFinder.Cli.Tools;

/// <summary>
/// The game's own content, read out of the installed <c>sts2.dll</c> by running it.
///
/// This is how the data tables are regenerated after a patch, and it is deliberately NOT a
/// parser. The pools are declared as method bodies full of <c>ModelDb.Relic&lt;T&gt;()</c> calls,
/// so the obvious approach is to decompile and scrape them — which is what the original Python
/// generators did, and which fails quietly the moment the compiler emits a shape the regex did
/// not anticipate. That is not hypothetical: <c>Ironclad3Epoch.Relics</c> compiles to
/// <c>CollectionsMarshal.SetCount</c> plus span assignment, the old pattern cut it at the first
/// semicolon, and every epoch gate extracted EMPTY while passing every check we had.
///
/// The alternative is to let the game answer. <c>ModelDb.Inject(Type)</c> is public and does
/// nothing but <c>Activator.CreateInstance</c>, so injecting every <c>AbstractModel</c> subtype
/// populates the database headless — 1,656 models, no failures, no Godot. After that
/// <c>RelicPool&lt;SharedRelicPool&gt;().AllRelics</c> simply returns the right relics in the right
/// order, because it is the game's own code computing it. Nothing left to misread.
///
/// What stays out of reach is anything needing a live run: <c>ActModel.BossEncounter</c> and
/// friends throw <c>NullReferenceException</c> without one. Those are precisely the values this
/// project generates rather than reads, so it costs nothing.
/// </summary>
public sealed class GameModels
{
    private readonly Assembly _sts2;
    private readonly Type _modelDb;
    private readonly IDictionary _contentById;

    public string AssemblyPath { get; }

    /// <summary>How many model types were injected. Reported so a collapse is visible.</summary>
    public int InjectedCount { get; }

    private GameModels(Assembly sts2, string path, Type modelDb, IDictionary contentById, int injected)
    {
        _sts2 = sts2;
        _modelDb = modelDb;
        _contentById = contentById;
        AssemblyPath = path;
        InjectedCount = injected;
    }

    /// <summary>
    /// Loads the game assembly and populates its model database.
    ///
    /// The assembly is loaded into the default context with a resolver pointing at the game's
    /// own folder, because sts2.dll references GodotSharp and friends that live beside it.
    /// </summary>
    public static GameModels Load(string assemblyPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };

        var sts2 = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var modelDb = sts2.GetType("MegaCrit.Sts2.Core.Models.ModelDb", throwOnError: true)!;
        var abstractModel = sts2.GetType("MegaCrit.Sts2.Core.Models.AbstractModel", throwOnError: true)!;
        var inject = modelDb.GetMethod("Inject", BindingFlags.Public | BindingFlags.Static)!;

        // Inject everything rather than only what we think we need: pools reference relics,
        // relics reference other models, and the closure is not ours to predict. Failures are
        // swallowed per type because a model that cannot be constructed headless is simply one
        // we cannot use, not a reason to abandon the rest.
        int injected = 0;
        foreach (var type in sts2.GetTypes())
        {
            if (type.IsAbstract || !abstractModel.IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            try { inject.Invoke(null, [type]); injected++; }
            catch { /* not constructible headless; nothing downstream can want it */ }
        }

        var contentById = (IDictionary)modelDb
            .GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        return new GameModels(sts2, assemblyPath, modelDb, contentById, injected);
    }

    public Type TypeNamed(string fullName) => _sts2.GetType(fullName, throwOnError: true)!;
    public Type? TypeOrNull(string fullName) => _sts2.GetType(fullName, throwOnError: false);

    /// <summary>
    /// The instance ModelDb holds for a type. Constructing a second one throws
    /// <c>DuplicateModelException</c>, because <c>AbstractModel</c>'s constructor registers.
    /// </summary>
    public object Instance(Type type) =>
        _contentById.Values.Cast<object>().FirstOrDefault(v => v.GetType() == type)
        ?? throw new InvalidOperationException($"{type.Name} is not in ModelDb");

    /// <summary>Concrete subtypes of a base, in metadata order.</summary>
    public IEnumerable<Type> SubtypesOf(string baseFullName)
    {
        var b = TypeNamed(baseFullName);
        return _sts2.GetTypes().Where(t => !t.IsAbstract && b.IsAssignableFrom(t));
    }

    /// <summary>A generic static on ModelDb, e.g. <c>RelicPool&lt;SharedRelicPool&gt;()</c>.</summary>
    public object CallGenericStatic(string method, Type arg) =>
        _modelDb.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == method && m.IsGenericMethod)
            .MakeGenericMethod(arg)
            .Invoke(null, null)!;

    public object StaticProperty(string typeFullName, string name) =>
        TypeNamed(typeFullName).GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    // ---- reading members off a model ---------------------------------------------------

    /// <summary>Reads a property or field, public or not. Protected members carry real data
    /// here: <c>BaseNumberOfRooms</c> and <c>NumberOfWeakEncounters</c> are both protected.</summary>
    public static object? Member(object model, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = model.GetType();
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.GetProperty(name, flags) is { } p) return p.GetValue(model);
            if (t.GetField(name, flags) is { } f) return f.GetValue(model);
        }
        throw new MissingMemberException(type.Name, name);
    }

    public static string Str(object model, string name) => Member(model, name)?.ToString() ?? "";
    public static int Int(object model, string name) => Convert.ToInt32(Member(model, name));

    public static IEnumerable<object> Many(object model, string name) =>
        ((IEnumerable)Member(model, name)!).Cast<object>();

    /// <summary>Model type names are what our tables key on, not localized titles.</summary>
    public static IEnumerable<string> Names(object model, string name) =>
        Many(model, name).Select(x => x.GetType().Name);

    // ---- unlock states ------------------------------------------------------------------

    public object UnlockAll => TypeNamed("MegaCrit.Sts2.Core.Unlocks.UnlockState")
        .GetField("all", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    public IReadOnlyList<string> AllEpochIds =>
        ((IEnumerable)StaticProperty("MegaCrit.Sts2.Core.Timeline.EpochModel", "AllEpochIds"))
            .Cast<string>().ToList();

    /// <summary>
    /// An unlock state with everything revealed except one epoch. Diffing a pool against this
    /// is how epoch gates are recovered — the game decides what a locked epoch removes, so
    /// there is no list to transcribe and no way to transcribe it wrongly.
    /// </summary>
    public object UnlockAllExcept(string epochId)
    {
        var unlockState = TypeNamed("MegaCrit.Sts2.Core.Unlocks.UnlockState");
        var modelId = TypeNamed("MegaCrit.Sts2.Core.Models.ModelId");
        var ctor = unlockState.GetConstructors().First(c => c.GetParameters().Length == 3);
        return ctor.Invoke([
            AllEpochIds.Where(e => e != epochId).ToList(),
            Array.CreateInstance(modelId, 0),
            999_999_999,
        ]);
    }
}
