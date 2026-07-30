using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sts2.SeedFinder.Web.Assets;

/// <summary>
/// What the game itself says about one relic or card.
///
/// <paramref name="Title"/> is advisory: callers with a better name of their own use that, and
/// only fall back here. Neow's relics are named in <c>NeowRelics</c> and keep those names; shop
/// relics have no hand-written list, so their names come from here — which is what gets the
/// apostrophes right in "Lee's Waffle" and "Dolly's Mirror". Nothing keys ART off a title;
/// that is always the slug.
/// </summary>
public readonly record struct AssetText(string Description, string? Title = null);

/// <summary>
/// Relic descriptions lifted out of the player's own game install.
///
/// Same boundary as the art (docs/web_app_specs.md section 4): the strings are read at runtime from
/// a copy of the game the user already owns, held in memory, and never written into this repo
/// or served from a machine that does not own the game.
///
/// The source is <c>localization/eng/relics.json</c> inside the .pck — a flat map of
/// "SCREAMING_SNAKE.field" to text, where the key stem is the same slug we use, uppercased.
/// </summary>
public static partial class GameText
{
    private const string RelicsJson = "localization/eng/relics.json";
    private const string CardsJson = "localization/eng/cards.json";
    private const string EventsJson = "localization/eng/events.json";

    /// <summary>
    /// Every model title in one localization file, keyed by the id stem, lowercased.
    ///
    /// Used to turn a referenced model id into a readable name: "ENCHANTMENT.SOWN" needs
    /// <c>enchantments.json</c>'s <c>SOWN.title</c>. Only single-dot keys count, so an option's or
    /// a page's own title cannot be mistaken for a model's.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadTitles(GodotPck pck, string kind)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var raw = pck.Read($"localization/eng/{kind}.json");
        if (raw is null) return result;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                const string suffix = ".title";
                if (!prop.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;

                var stem = prop.Name[..^suffix.Length];
                if (stem.Contains('.')) continue;

                var title = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(title)) result[stem] = title;
            }
        }
        catch (JsonException) { /* a reshaped table costs names, not the app */ }

        return result;
    }

    /// <summary>
    /// The localization file each model id kind lives in, and the placeholder names that ask for
    /// one. A placeholder is named after the KIND it wants — "{Enchantment}", "{Potion}" — which
    /// is what makes this mappable at all.
    ///
    /// Curses are cards, so they read from the card table.
    /// </summary>
    private static readonly (string Placeholder, string IdKind, string LocFile)[] NamedRefKinds =
    {
        ("Enchantment", "ENCHANTMENT", "enchantments"),
        ("Potion", "POTION", "potions"),
        ("Relic", "RELIC", "relics"),
        ("Card", "CARD", "cards"),
        ("Curse", "CARD", "cards"),
        ("Monster", "MONSTER", "monsters"),
    };

    /// <summary>
    /// Fills placeholders that want a NAME, using the ids the model's own code mentions.
    ///
    /// Only fires when the model references exactly ONE id of the kind the placeholder asks for,
    /// which is what keeps it honest: Wood Carvings names two cards ({BirdCard}, {ToricCard}) and
    /// nothing here can say which is which, so both keep their X rather than being guessed at.
    /// </summary>
    private static string SubstituteNamedRefs(
        string text,
        IReadOnlyList<string>? refs,
        Func<string, IReadOnlyDictionary<string, string>> titles)
    {
        if (refs is null || refs.Count == 0 || !text.Contains('{')) return text;

        foreach (var (placeholder, idKind, locFile) in NamedRefKinds)
        {
            // Both spellings occur: Royal Stamp's template says {Enchantment}, Pael's Claw's says
            // {EnchantmentName} for the same thing. Accepting only the bare form left Pael's two
            // relics reading X while Royal Stamp resolved, which looked like a deeper limit and
            // was just a second name for the same token.
            string[] tokens = ["{" + placeholder + "}", "{" + placeholder + "Name}"];
            if (!tokens.Any(t => text.Contains(t, StringComparison.Ordinal))) continue;

            var matches = refs.Where(r => r.StartsWith(idKind + ".", StringComparison.Ordinal))
                              .ToList();
            if (matches.Count != 1) continue;

            var stem = matches[0][(idKind.Length + 1)..];
            if (!titles(locFile).TryGetValue(stem, out var title)) continue;

            foreach (var token in tokens) text = text.Replace(token, title, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// Drops vars that carry a zero, so the resolver falls back to X for them.
    ///
    /// EVENTS ONLY, deliberately. A few models hold 0 for a quantity the game works out per run
    /// — Dense Vegetation's gold and HP loss, Spiraling Whirlpool's healing — and "Gain 0 Gold"
    /// is worse than "Gain X Gold", because it reads as a real promise of nothing rather than as
    /// a number we could not obtain. No event option meaningfully offers zero of anything, so
    /// there is nothing true to lose here.
    ///
    /// Relics and cards do NOT get this treatment: a relic can legitimately say 0, and the same
    /// rule there would corrupt text that is currently correct.
    /// </summary>
    private static IReadOnlyDictionary<string, RelicVar>? Meaningful(
        IReadOnlyDictionary<string, RelicVar>? vars)
    {
        if (vars is null) return null;

        // A string-valued var keeps its entry: its number is meaningless, and the resolver reads
        // the text instead.
        var kept = vars.Where(kv => kv.Value.Value != 0 || kv.Value.Text is not null)
                       .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return kept.Count == vars.Count ? vars : kept;
    }

    /// <summary>
    /// Event titles, and the CHOICES each event puts in front of you.
    ///
    /// The file is not shaped like relics.json. An event's keys are its whole branching script:
    /// <c>ZEN_WEAVER.pages.INITIAL.description</c> is scene-setting prose,
    /// <c>pages.INITIAL.options.ARACHNID_ACUPUNCTURE.{title,description}</c> is one of the
    /// choices and what taking it does, and there is a further page per outcome. What is worth
    /// knowing before you commit a search to an event is the options, so those are what this
    /// reads: the prose is flavour, and the outcome pages are spoilers.
    ///
    /// Locked variants are dropped. They are the same option restated for when you cannot afford
    /// it ("Requires X Gold", "Not enough Gold"), so they add a line without adding a choice.
    /// </summary>
    public static IReadOnlyDictionary<string, AssetText> ReadEvents(
        GodotPck pck,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, RelicVar>>? vars = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? modelRefs = null)
    {
        // Title tables are pulled on demand and cached, since most events need none of them.
        var titleCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> Titles(string kind)
        {
            if (!titleCache.TryGetValue(kind, out var t)) titleCache[kind] = t = ReadTitles(pck, kind);
            return t;
        }

        var result = new Dictionary<string, AssetText>(StringComparer.OrdinalIgnoreCase);

        var raw = pck.Read(EventsJson);
        if (raw is null) return result;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            // An event's own title is the one key with a single dot; everything deeper belongs to
            // a page or an option, and those carry titles of their own ("Arachnid Acupuncture").
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                const string suffix = ".title";
                if (!prop.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;

                var stem = prop.Name[..^suffix.Length];
                if (stem.Contains('.')) continue;

                var title = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(title)) titles[stem.ToLowerInvariant()] = title;
            }

            // Option label and effect arrive as separate keys, and in file order, which is the
            // order the game lists them in. Keyed by "<slug>\0<OPTION>" to keep that grouping
            // without a nested dictionary.
            var optionTitles = new Dictionary<string, string>(StringComparer.Ordinal);
            var optionText = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;

                // <STEM>.pages.INITIAL.options.<OPTION>.<title|description>
                const string marker = ".pages.INITIAL.options.";
                int at = prop.Name.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) continue;

                var slug = prop.Name[..at].ToLowerInvariant();
                if (slug.Contains('.')) continue;

                var rest = prop.Name[(at + marker.Length)..];
                int dot = rest.LastIndexOf('.');
                if (dot <= 0) continue;

                var option = rest[..dot];
                var field = rest[(dot + 1)..];
                if (option.Contains('.')) continue;
                if (option.Contains("LOCKED", StringComparison.Ordinal)) continue;

                var value = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;

                // Two thirds of these interpolate a value: "Pay {ArachnidAcupunctureCost} Gold".
                // The numbers come from the event's own model, read out of the assembly exactly as
                // relics are, so they resolve to real figures. Anything the model does not carry
                // falls back to X, and the resolver also handles the plural-branch form,
                // "{Cards:plural:card|{} cards}".
                IReadOnlyDictionary<string, RelicVar>? mine = null;
                vars?.TryGetValue(slug, out mine);

                // Names first, from the ids the model's code mentions, then numbers from its vars.
                // Both are needed: "Obtain {Relic}. Lose {HpLoss} Max HP" wants one of each.
                IReadOnlyList<string>? refs = null;
                modelRefs?.TryGetValue(slug, out refs);
                value = SubstituteNamedRefs(value, refs, Titles);

                value = ResolvePlaceholders(value, Meaningful(mine));

                var key = slug + '\0' + option;
                if (field == "title") { optionTitles[key] = value; }
                else if (field == "description") { optionText[key] = value; }
                else continue;

                if (!order.Contains(key)) order.Add(key);
            }

            foreach (var key in order)
            {
                var slug = key[..key.IndexOf('\0')];

                optionTitles.TryGetValue(key, out var label);
                optionText.TryGetValue(key, out var effect);
                if (label is null && effect is null) continue;

                // The label is bracketed in the game's own [b] so it renders as a heading through
                // the same markup path as everything else, rather than needing its own element.
                var line = label is null ? effect!
                    : effect is null ? $"[b]{label}[/b]"
                    : $"[b]{label}[/b]\n{effect}";

                result.TryGetValue(slug, out var existing);
                var body = string.IsNullOrEmpty(existing.Description) ? line : existing.Description + "\n\n" + line;

                titles.TryGetValue(slug, out var title);
                result[slug] = new AssetText(body, title);
            }

            // An event with no options listed still wants its title, since that is what the picker
            // and the results label with.
            foreach (var (slug, title) in titles)
                if (!result.ContainsKey(slug)) result[slug] = new AssetText("", title);
        }
        catch (JsonException)
        {
            // Same posture as the other two readers: reshaped localization costs tooltips, not
            // the app.
        }

        return result;
    }

    /// <summary>
    /// Card titles and rules text. Same file shape as relics, but cards carry a real ".title"
    /// worth using — "StrikeIronclad" reads as "Strike" in game, and no slug transform gets
    /// there on its own.
    /// </summary>
    public static IReadOnlyDictionary<string, AssetText> ReadCards(
        GodotPck pck,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, RelicVar>>? vars = null)
    {
        var result = new Dictionary<string, AssetText>(StringComparer.OrdinalIgnoreCase);

        var raw = pck.Read(CardsJson);
        if (raw is null) return result;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;

                int dot = prop.Name.LastIndexOf('.');
                if (dot < 0) continue;

                var slug = prop.Name[..dot].ToLowerInvariant();
                var value = prop.Value.GetString() ?? "";

                switch (prop.Name[(dot + 1)..])
                {
                    case "description":
                        IReadOnlyDictionary<string, RelicVar>? mine = null;
                        vars?.TryGetValue(slug, out mine);
                        descriptions[slug] = ResolvePlaceholders(value, mine);
                        break;
                    case "title":
                        titles[slug] = value;
                        break;
                }
            }

            foreach (var slug in descriptions.Keys.Union(titles.Keys, StringComparer.OrdinalIgnoreCase))
                result[slug] = new AssetText(
                    descriptions.GetValueOrDefault(slug, ""),
                    titles.GetValueOrDefault(slug));
        }
        catch (JsonException)
        {
            return new Dictionary<string, AssetText>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Reads and resolves every relic description in the pack. Returns an empty map rather
    /// than throwing — missing or reshaped localization must cost tooltips, not the app.
    /// </summary>
    /// <param name="vars">Per-relic values from <see cref="RelicVars"/>. Without them the
    /// quantities render as "X"; with them the descriptions read as they do in game.</param>
    public static IReadOnlyDictionary<string, AssetText> ReadRelics(
        GodotPck pck,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, RelicVar>>? vars = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? modelRefs = null)
    {
        var result = new Dictionary<string, AssetText>(StringComparer.OrdinalIgnoreCase);

        var titleCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> Titles(string kind)
        {
            if (!titleCache.TryGetValue(kind, out var t)) titleCache[kind] = t = ReadTitles(pck, kind);
            return t;
        }

        var raw = pck.Read(RelicsJson);
        if (raw is null) return result;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            // ".flavor" is a placeholder on nearly every relic, so only these two are read.
            // Titles are collected first because the file interleaves the two suffixes and a
            // description can arrive before its own title.
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                const string suffix = ".title";
                if (!prop.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;

                var title = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(title))
                    titles[prop.Name[..^suffix.Length].ToLowerInvariant()] = title;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                const string suffix = ".description";
                if (!prop.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;

                var slug = prop.Name[..^suffix.Length].ToLowerInvariant();
                IReadOnlyDictionary<string, RelicVar>? mine = null;
                vars?.TryGetValue(slug, out mine);

                // Names first, exactly as events do. A relic that names an enchantment rather than
                // a quantity — Pael's Claw, Pael's Growth, Royal Stamp — cannot get it from the
                // model, because that whole var set is what throws; but the id is in the model's
                // IL and the title is in enchantments.json. Numbers are NOT filtered here the way
                // events are: a relic can legitimately say 0.
                IReadOnlyList<string>? refs = null;
                modelRefs?.TryGetValue(slug, out refs);
                var template = SubstituteNamedRefs(prop.Value.GetString() ?? "", refs, Titles);

                var text = ResolvePlaceholders(template, mine);
                if (text.Length > 0)
                    result[slug] = new AssetText(text, titles.GetValueOrDefault(slug));
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, AssetText>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Stands in for the game's own template expansion.
    ///
    /// Descriptions are templates over a relic's <c>DynamicVars</c>: <c>{Block}</c> is one of
    /// them, <c>{Cards:plural:card|cards}</c> picks a word from that var's value, and
    /// <c>{X.StringValue:cond:a|b}</c> chooses a phrasing. Roughly three quarters of relics
    /// use at least one.
    ///
    /// The game runs these through SmartFormat; this reimplements the three behaviours its
    /// relic strings actually use, with the branch-selection rules read from the same
    /// SmartFormat build the game ships (see the comments on each). Anything unknown falls
    /// back to "X", which sits inside the colour tag the number would have and reads as "some
    /// amount" — the old behaviour for every placeholder, now only the last resort.
    /// </summary>
    internal static string ResolvePlaceholders(
        string template, IReadOnlyDictionary<string, RelicVar>? vars = null) =>
        Expand(template, vars, null).Trim();

    /// <param name="current">The value of the enclosing placeholder, which branches refer back
    /// to as <c>{}</c> — as in "{Combats:plural:combat|{} combats}".</param>
    private static string Expand(string s, IReadOnlyDictionary<string, RelicVar>? vars, RelicVar? current)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;

        while (i < s.Length)
        {
            if (s[i] != '{') { sb.Append(s[i++]); continue; }

            int end = MatchingBrace(s, i);
            if (end < 0) { sb.Append(s[i++]); continue; }   // unbalanced: leave it be

            sb.Append(Resolve(s[(i + 1)..end], vars, current));
            i = end + 1;
        }
        return sb.ToString();
    }

    private static string Resolve(string inner, IReadOnlyDictionary<string, RelicVar>? vars, RelicVar? current)
    {
        // "{}" is the game referring back to the value being formatted.
        if (inner.Length == 0) return current is { } c ? Number(c) : "X";

        int colon = IndexOfTop(inner, ':');
        if (colon < 0) return Bare(inner, vars);

        var field = inner[..colon];
        var rest = inner[(colon + 1)..];

        int next = IndexOfTop(rest, ':');
        var verb = next < 0 ? rest : rest[..next];
        var payload = next < 0 ? "" : rest[(next + 1)..];

        var self = Lookup(field, vars);

        return verb switch
        {
            "plural" => Expand(Plural(payload, self), vars, self),
            "cond" => Expand(Cond(field, payload, self), vars, self),
            "show" => Expand(Show(payload, self), vars, self),
            _ => Formatter(verb, field, self),
        };
    }

    // ---- Branch selection -------------------------------------------------------------------

    /// <summary>
    /// SmartFormat's PluralLocalizationFormatter under English rules ("DualOneOther"): with two
    /// words the value picks singular at exactly 1, otherwise plural; three and four add zero
    /// and negative forms. Without a value, plural is the form that agrees with "X".
    /// </summary>
    private static string Plural(string payload, RelicVar? self)
    {
        var branches = SplitTop(payload, '|');
        if (branches.Count == 0) return payload;
        if (self is not { } v) return branches[^1];

        int i = branches.Count switch
        {
            2 => v.Value == 1m ? 0 : 1,
            3 => v.Value == 0m ? 0 : v.Value == 1m ? 1 : 2,
            4 => v.Value < 0m ? 0 : v.Value == 0m ? 1 : v.Value == 1m ? 2 : 3,
            _ => branches.Count - 1,
        };
        return branches[Math.Clamp(i, 0, branches.Count - 1)];
    }

    /// <summary>
    /// SmartFormat's ConditionalFormatter. For a string it is emptiness that chooses: a blank
    /// value takes the second branch. For a number the value indexes the branches directly,
    /// clamped to the last.
    ///
    /// Relic strings only ever condition on a <c>.StringValue</c>, and those are blank until a
    /// run fills them in, so the second branch is both the correct one and the generic phrasing
    /// we want. The numeric path is here because the formatter allows it, not because anything
    /// uses it today.
    /// </summary>
    private static string Cond(string field, string payload, RelicVar? self)
    {
        var branches = SplitTop(payload, '|');
        if (branches.Count == 0) return payload;

        // SmartFormat also accepts explicit conditions on each branch ("{X:cond:>10?a|b}").
        // Nothing in the relic strings uses that today; if a patch introduces one, fall back to
        // the generic phrasing rather than silently picking the wrong branch.
        if (ComplexCondition().IsMatch(branches[0])) return branches[^1];

        if (self is not { } v) return branches[^1];

        int i = field.EndsWith(".StringValue", StringComparison.Ordinal)
            ? (string.IsNullOrEmpty(v.Text) ? 1 : 0)
            : v.Value < 0m ? branches.Count - 1 : (int)Math.Min(Math.Floor(v.Value), branches.Count - 1);

        return branches[Math.Clamp(i, 0, branches.Count - 1)];
    }

    /// <summary>
    /// A gate rather than a choice: card text uses it as "{IfUpgraded:show:upgraded |}", where a
    /// zero value drops the phrase entirely. Empty when there is no value to consult, which is
    /// the right reading for the cards we describe — a reward card is never upgraded in Act 1,
    /// where the upgrade odds scale with the act index and it is still zero.
    /// </summary>
    private static string Show(string payload, RelicVar? self)
    {
        var branches = SplitTop(payload, '|');
        if (branches.Count == 0) return "";
        return self is { Text: null } v && v.Value != 0m ? branches[0] : branches[^1];
    }

    // ---- Values ------------------------------------------------------------------------------

    /// <summary>
    /// A bare field reference. Nearly all are quantities — Cards, Block, Damage, Heal, Gold.
    /// Icons stand for a thing rather than a count, so those keep their name; string vars are
    /// blank outside a run, and reading their name aloud beats rendering nothing.
    /// </summary>
    private static string Bare(string name, IReadOnlyDictionary<string, RelicVar>? vars)
    {
        if (name.EndsWith("Icons", StringComparison.OrdinalIgnoreCase)) return Humanize(name[..^5]);
        if (name.EndsWith("Icon", StringComparison.OrdinalIgnoreCase)) return Humanize(name[..^4]);

        if (Lookup(name, vars) is not { } v) return "X";
        if (v.Text is null) return Number(v);
        return v.Text.Length > 0 ? v.Text : Humanize(name);
    }

    /// <summary>
    /// A formatter call. <c>energyIcons</c> and <c>starIcons</c> draw that many sprites, which
    /// we cannot, so they become words; <c>percentMore</c> is the increase as a percentage;
    /// <c>diff</c> is just the number, with change highlighting we have no use for.
    ///
    /// The icon formatters come in two shapes, and conflating them reads badly. "{Energy:
    /// energyIcons()}" draws one icon per point, so it is a quantity: "2 energy". But
    /// "{energyPrefix:energyIcons(1)}" passes a colour name and a fixed count of one, so the
    /// icon is standing in for the unit while the sentence supplies its own number — "costs 1
    /// more {icon}" has to become "costs 1 more energy", not "1 more 1 energy".
    /// </summary>
    private static string Formatter(string verb, string field, RelicVar? self)
    {
        var open = verb.IndexOf('(');
        var name = open < 0 ? verb : verb[..open];
        var quantity = self is { Text: null } ? (int)self.Value.Value : (int?)null;
        var counted = self is null && (open < 0 || verb.EndsWith("()", StringComparison.Ordinal));

        return name switch
        {
            "energyIcons" => quantity is { } e ? $"{e} energy" : counted ? "X energy" : "energy",
            "starIcons" => quantity is { } s ? $"{s} star{(s == 1 ? "" : "s")}" : counted ? "X stars" : "stars",
            "percentMore" => self is { } m ? ((int)((m.Value - 1m) * 100m)).ToString(CultureInfo.InvariantCulture) : "X",
            "percentLess" => self is { } l ? ((int)((1m - l.Value) * 100m)).ToString(CultureInfo.InvariantCulture) : "X",
            _ => self is { } d ? Number(d) : Field(field),
        };
    }

    private static RelicVar? Lookup(string field, IReadOnlyDictionary<string, RelicVar>? vars)
    {
        if (vars is null) return null;
        // "StarterCard.StringValue" addresses the StarterCard var.
        var dot = field.IndexOf('.');
        var name = dot < 0 ? field : field[..dot];
        return vars.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>The game renders a var as its integer value (<c>DynamicVar.ToString</c>).</summary>
    private static string Number(RelicVar v) =>
        ((int)v.Value).ToString(CultureInfo.InvariantCulture);

    private static string Field(string name) =>
        name.EndsWith("Icons", StringComparison.OrdinalIgnoreCase) ? Humanize(name[..^5])
        : name.EndsWith("Icon", StringComparison.OrdinalIgnoreCase) ? Humanize(name[..^4])
        : "X";

    /// <summary>"singleStar" to "single star" — a field name read out loud.</summary>
    private static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(c);
        }
        var words = sb.ToString().Trim();
        // Leave an all-caps or single word alone; only lowercase the run-on camelCase names.
        return words.Contains(' ') ? words.ToLowerInvariant() : words;
    }

    // ---- Parsing ------------------------------------------------------------------------------

    /// <summary>Splits on <paramref name="c"/> at brace depth zero.</summary>
    private static List<string> SplitTop(string s, char c)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == c && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>Index of the '}' closing the '{' at <paramref name="open"/>, or -1.</summary>
    private static int MatchingBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>First <paramref name="c"/> that is not nested inside braces.</summary>
    private static int IndexOfTop(string s, char c)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == c && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>SmartFormat's per-branch condition prefix, e.g. "&gt;10?".</summary>
    [GeneratedRegex(@"^\s*[&/]?[<>=!]=?[0-9.-]+.*\?")]
    private static partial Regex ComplexCondition();
}
