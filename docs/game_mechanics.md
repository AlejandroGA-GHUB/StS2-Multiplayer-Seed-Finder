# Game mechanics, as read out of the game

Everything here was established by decompiling Slay the Spire 2 and checking it against the
game's own code or a real run. It lives outside `CLAUDE.md` because it is reference material:
each section matters intensely when working in that one area and is dead weight otherwise, and
`CLAUDE.md` is re-read on every request.

**`CLAUDE.md` says which of these to read before touching which part of the code.** If you are
about to change generation, draw order, or anything that has to match the game bit for bit, read
the relevant section first. Several of these record mistakes that were made once already.

Anything marked *wiki-sourced* is unverified: confirm against decompiled source or a live run
before relying on it.

---

### Decompiling
`ilspycmd` is installed globally (`~/.dotnet/tools`, may need adding to PATH):
```
ilspycmd -p -o <outdir> "<install>\data_sts2_windows_x86_64\sts2.dll"   # 3,494 .cs files, ~30s
ilspycmd -t <FullTypeName> "<...>\sts2.dll"                             # single type to stdout
```
Content is C# classes, not data blobs: 1,328 cards, 610 relics, 602 powers, 540 monsters, 283 events.
Card stats are constructor arguments. Namespaces are all under `MegaCrit.Sts2.Core.*`.

**Never commit decompiled source or game assets.** Decompile to a temp dir *outside* the repo.
`sts2src/` and `decompiled/` are in `.gitignore` purely as a guard against decompiling into the
project root by accident — no decompiled code has ever been in this repo.

### Localization strings (relic and card descriptions)
`SlayTheSpire2.pck` holds `localization/<lang>/*.json` for 15 languages — plain JSON, no Godot
`.translation` resources involved. `localization/eng/relics.json` is a flat map of
`"SCREAMING_SNAKE.description" | ".title" | ".flavor"`, where the key stem is our slug
uppercased. 306 relic descriptions; `.flavor` is a "revealed in the future" placeholder on
nearly all of them. Also there: `acts.json`, `ancients.json`, `events.json`, `cards.json`.

Descriptions are **templates**: 347 placeholders across 223 of the 306 descriptions, in exactly
these shapes (counts from v0.109.1):
- `{Cards}` ×229 — a DynamicVar on the relic instance. Nearly always a quantity.
- `{Cards:plural:card|cards}` ×75 — word choice driven by that var's value.
- `{Energy:energyIcons()}` ×31 + `{energyPrefix:energyIcons(1)}` ×2, `{Stars:starIcons()}` ×3,
  `{GoldIncrease:percentMore()}`, `{SharpAmount:diff()}` — formatter calls.
- `{X.StringValue:cond:specific|generic}` ×5 — blank outside a run, so the second branch.
- 12 placeholders nest another inside a branch, where `{}` means the enclosing value.

The values live in the **assembly**, not the loc file — each relic overrides `CanonicalVars`
with literals like `new BlockVar(10m, ...)`. `Assets/RelicVars.cs` reads them at runtime by
constructing each relic and asking it: `AbstractModel`'s ctor only computes an id and probes a
dictionary, and nothing in that path touches Godot, so **no `ModelDb.Init`, no engine, no game
running** (~150 ms for all 299 relic types). `ModelDb.Init(null)` does *not* work headless — it
needs `ModManager` — but nothing needs it.

`Assets/GameText.cs` then expands the templates. The branch rules are SmartFormat's, read from
the `SmartFormat.dll` the game ships, not guessed: plural under English `DualOneOther` is
`value == 1 ? singular : plural` for 2 words (3 and 4 add zero/negative forms); `cond` on a
string picks branch **1** when empty, and on a number indexes the branches by the value itself.

**213 of the 223 resolve.** The 10 that do not split into two causes:

- **Seven have no class in the assembly at all** — `ink_bottle`, `glowing_orb`, `vine_bracelet`,
  `cursed_kettle`, `endless_appetite`, `mysterious_cocoon`, `red_vine_tea`. Loc text written for
  content that is not implemented in this build. They cannot appear in the UI either, since the
  catalog is built from the assembly's pools rather than from the loc table. Harmless, and a
  useful canary: if a patch implements them they gain numbers with no work.
- **Three are blocked by the engine** — `PaelsClaw`, `PaelsGrowth`, `RoyalStamp`. Their vars read
  `ModelDb.Enchantment<T>().Title.GetFormattedText()`, i.e. a localized *name* rather than a
  literal. Verified failure chain, each step tested rather than assumed:
  1. `ModelDb.Enchantment<Goopy>()` throws `KeyNotFoundException` on `ENCHANTMENT.GOOPY`, because
     we construct relics directly and never populate the db.
  2. Inject all 24 `EnchantmentModel` subtypes (`ModelDb.Inject`, all 24 succeed) and it gets one
     line further: `.Title` resolves to a `LocString`, which holds only coordinates
     (`table="enchantments"`, `key="GOOPY.title"`), so `GetFormattedText()` — and `GetRawText()`
     equally — dereferences a null `LocManager.Instance`.
  3. `LocManager.Initialize()` is the only static entry point, and calling it **hard-crashes the
     process**: `ctor` → `SaveManager.Instance` → `UserDataPathProvider` →
     `PlatformUtil.GetLocalPlayerId` → access violation, `0xC0000005`. Not a catchable exception.
     That is the reason not to attempt it inside the web server.

  Only the first two are reachable in our pools, so this costs **2 of the 100 relics the UI
  offers**. Note the near miss: `enchantments.json` in the same .pck we already open holds all 24
  titles under keys of the form `<ENCHANTMENT>.title`, and the `LocString` even names the exact
  key it wants. The one missing
  fact is *which* enchantment a given relic references, and that exists only as a generic type
  argument in the compiled property — reachable by decoding the getter's IL for the `MethodSpec`
  token, which reflection cannot do.

**Cards go through the same machinery, and cost nothing extra.** `cards.json` has the identical
key shape, `CardModel` subtypes construct headless exactly like relics, and one `RelicVars.Read`
pass now returns both (`ModelVars.Relics` / `.Cards`) off a single assembly load. `GameText`
gained one template verb for them, `show` (branch 1 when the var is non-zero, else the last).
All 425 cards the first-fight picker offers resolve, description and title.

### Card art — `.import` files, and a `beta/` folder that is not dead weight
Relic art is a flat `images/relics/<slug>.png`, so basename matching against
`.godot/imported/` is enough. **Card art is not**, for two reasons:

- Cards live under `images/packed/card_portraits/<pool>/`, and some also under
  `<pool>/beta/` — older art, sometimes the *only* art a card has (`blaze`, `concoct`,
  `hibernate` and 11 others have no final portrait). `FindCardTextures` walks both and prefers
  the non-beta copy where both exist. Skipping the subfolder silently loses 14 cards.
- A card and a relic can share a slug, and their imported files differ only by a content hash.
  So cards are resolved by **reading each `.png.import` and taking the `path=` it names**,
  which is exact. ~900 small reads at startup, done once.

With that plus the `Slugify` fix above, coverage is **425/425 on both art and descriptions**.

**Character portraits** are a third family: `images/packed/character_select/char_select_<slug>.png`,
132×195 WebP, where the slug is the lowercased `Character` enum name. Resolve them by matching
that enum, **not** by globbing `char_select_*` — that folder is mostly the select screen's own
chrome (`button_mask`, `outline`, `outline_remote`, `player_icon`, `lock3`, `random`) plus a
`_locked` silhouette per character, and a glob reports 11 "characters" instead of 5.

Markup is BBCode-ish: `[gold]`, `[blue]`, `[red]`, `[purple]`, `[green]`, `[shake]`, always
balanced. The server sends it through untouched and `app.js` turns it into spans — never
`innerHTML`.

Same provenance boundary as the art: read from the user's own install at runtime, cached in
memory, never committed. See `docs/web_app_specs.md` §4. `sts2.dll` is loaded into its own
`AssemblyLoadContext` so the game's assemblies stay out of ours, and every failure path
degrades to `X` rather than taking the app down.

### Slugs — `Slugify` / `SnakeCase` split harder than they look
`StringHelper` breaks before **every** uppercase letter that follows a letter or digit, so a run
of capitals is split apart rather than kept whole as an acronym:
```
IAmInvincible -> I_AM_INVINCIBLE     ExpectAFight    -> EXPECT_A_FIGHT
ENetClient    -> E_NET_CLIENT        NVSyncPaginator -> N_V_SYNC_PAGINATOR
Act2BEpoch    -> ACT2_B_EPOCH
```
Both plausible simpler rules are wrong. "Split at lower-to-upper only" gives `iam_invincible`,
which matches neither the card's art file nor its localization key. "Split at lower-to-upper,
plus the end of an acronym run" (the usual convention) gets everything except
`NVSyncPaginator`. Fixed 2026-07-28; the one-line loop in `GameHash.InsertCamelBoundaries` is
checked against the game's own `Slugify` over **all 5,771 named types in the assembly, 0
disagreements**. Re-run that comparison after a patch if slugs ever look off.

This feeds `Id.Entry`, every RNG stream name, and every art/loc lookup, so getting it wrong is
not cosmetic. Only two names in our data ever hit the acronym case (`ExpectAFight`,
`IAmInvincible`), which is why it went unnoticed until cards landed.

### Seeds
- 12 chars over `0123456789ABCDEFGHJKLMNPQRSTUVWXYZ` (34 chars; no `O`/`I`).
- Canonicalize: uppercase, `O`→`0`, `I`→`1`, trim (`SeedHelper.CanonicalizeSeed`).
- `StringHelper.GetDeterministicHashCode(seedString)` → the `ulong` the RNGs consume.
- Effective space believed 2³² (~4.29B) — exhaustively searchable. *Confirm in Phase 0.*

### RNG
- PRNG is **xoshiro256\*\*** (`Core.Random.MegaRandom`) since v0.107.1, which replaced `System.Random`
  after correlated-generator exploits. Anything written about pre-0.107.1 RNG is obsolete.
- `Rng` wraps it and tracks a `_counter` (must be ported — it's serialized).
- Named derivation: `new Rng(seed, name)` → `new Rng(seed + hash(name))`.
- `RunRngSet` — 12 shared run RNGs, names snake_cased from `RunRngType`:
  `UpFront`, `Shuffle`, `UnknownMapPoint`, `CombatCardGeneration`, `CombatPotionGeneration`,
  `CombatCardSelection`, `CombatEnergyCosts`, `CombatTargets`, `MonsterAi`, `Niche`, `CombatOrbs`,
  `TreasureRoomRelics`. `UpFront` drives monsters/events/relics offered.
- `PlayerRngSet` — 3 **per-player** RNGs: `Rewards`, `Shops`, `Transformations`.

### The multiplayer key
`Player.cs:330` — a player's whole `PlayerRngSet` is seeded with the run seed plus their lobby
position, and nothing else goes into it:
```
playerSeed = StringHelper.GetDeterministicHashCode(seedString) + slotIndex
```
`GetPlayerSlotIndex` is `Players.IndexOf(player)` — plain lobby order. So P1 uses `hash(S)+0`, P2 `hash(S)+1`.
Also `new Rng(Player, ModelId, mixin)` = `runSeed + slotIndex + hash(modelId) + mixin`.

### The Neow chain (v1's core — all verified against the game)
```
runSeed  = XXH64(canonicalize(seedString))            // RunRngSet ctor
neowSeed = (ulong)((long)runSeed + slotIndex) + XXH64("NEOW")   // EventModel.cs:234, IsShared=false
index    = Rng(neowSeed).NextInt(0, curseCount)       // FIRST draw of Neow.GenerateInitialOptions
```
- `EventModel.IsShared => false` and Neow does not override it, so **the slot index is
  included** — every player rolls their own Neow offer. This is what makes co-op search work.
- Neow offers exactly 3 options: one curse relic (the first draw) + 2 positives.
- **The curse and positive pools are disjoint** (each curse has a *counterpart* that is a
  different relic), so a relic's branch is fixed by which relic it is. Two consequences:
  `SeedSearcher` settles a curse relic on ONE draw rather than generating the whole offer
  (~6x on a 4-player search, and it must key on the relic's pool, not on the caller's
  `OfferSlot`), and the web UI states the branch rather than offering it as a choice. The
  Oracle check `Curse fast path == full offer` asserts both the disjointness and the
  equivalence, because a patch breaking either would leave every game-facing check passing.
- Curse list, in declaration order (`Neow.CurseOptions`, do not reorder):
  CursedPearl, DowsingRod, HeftyTablet, LargeCapsule, LeafyPoultice, NeowsBones,
  NeowsSacrifice, PrecariousShears, **SilkenTress**, SilverCrucible.
- `SilverCrucible.IsAllowed => runState.Players.Count == 1` — singleplayer-only, so co-op
  pools are **9 relics**, giving Silken Tress a flat **1/9 per player**.
- No other curse relic overrides `IsAllowed`/`IsAllowedAtNeow`, so nothing else filters.
- `Id.Entry` is `Slugify(typeName)` → uppercase, so Neow hashes the string `"NEOW"`.

**Full offer (implemented in `Core/Neow/NeowGenerator.cs`).** Draw order is load-bearing:
```
1. curse   = rng.NextItem(curses filtered by IsAllowedAtNeow)   <- first draw
2. positives = the base 14, minus the curse's counterpart:
     CursedPearl->GoldenPearl  HeftyTablet->ArcaneScroll  LeafyPoultice->NewLeaf
     PrecariousShears->PreciseScissors  NeowsSacrifice->{PhialHolster,LostCoffer}
3. if curse is NOT LargeCapsule: flip LavaRock / SmallCapsule   <- SKIPPED for LargeCapsule,
                                                                   which shifts all later draws
4. flip NutritiousOyster / StoneHumidifier
5. flip NeowsTalisman / Pomander
6. NOW filter positives by availability (removals in step 2 happen before this)
7. UnstableShuffle, take 2.  Returned order is [pos1, pos2, curse].
```
Availability gates that matter: `WingedBoots` SP-only, `MassiveScroll` MP-only,
`Kaleidoscope` needs every character unlocked, `ScrollBoxes` needs ≥4 commons + ≥2 uncommons.

Card *payloads* (Hefty Tablet's 3 rare cards, Arcane Scroll's rare, Scroll Boxes' bundles)
are rolled in `AfterObtained`, not at Neow. **Arcane Scroll's and Hefty Tablet's are now
modelled and searchable** (`Core/Cards/NeowCardPayload.cs`, Oracle-verified against v0.111.0);
Massive Scroll's, Scroll Boxes' and Neow's Bones' are not. For all of them
**how many draws they cost is** known, because they come off `PlayerRng.Rewards` and therefore
shift the first fight's reward — see `NeowRewardDrawCost` below.

**Hefty Tablet and Arcane Scroll: the draw shape is settled** (established against v0.110.1,
2026-08-08). Neither relic rolls cards itself. Both hand the ordinary reward factory
(`CardFactory.CreateForReward`) the player's own character pool, a filter of "Rare only", uniform
rarity odds and the no-upgrade-roll flag, then ask it for a count — three for Hefty Tablet, one
for Arcane Scroll. Nothing else differs between them.

Three consequences, and the first is the one that is easy to get wrong:

- **The picks blacklist each other.** The multi-card entry point accumulates what it has already
  produced and excludes it from each later pick, so Hefty Tablet's second pick sees the rare pool
  one entry shorter and its third two shorter. Same factory, same blacklist as a fight reward,
  which `Core/Cards/` has already modelled as the `taken` span in `CardRewardGenerator.Hallway`.
- **The no-upgrade-roll flag really does remove the draw**, rather than rolling and discarding it.
  Unlike a fight reward there is no upgrade `NextFloat` per card.
- **Uniform rarity means no rarity roll.** That is what leaves one draw per card, and it is why
  the recorded costs of 1 and 3 are exactly the pick draws.

So a payload is `Hallway`'s inner loop with the rarity roll and the upgrade roll deleted: N times
`NextInt(0, CountAvailable(Rare, minus taken))` into `NthAvailable`. Nothing new is needed from
the pools. **Scroll Boxes, Massive Scroll and Neow's Bones were NOT established and are not
covered by this** — their costs (6/8, 9, and a shuffle) say their shapes differ.

**Implemented 2026-08-31** as `NeowCardPayload`, re-read against v0.111.0 and Oracle-checked
draw-for-draw (`Neow card payloads, draw-for-draw vs game Rng`, five characters x eighty seeds x
two slots, plus both card counts read out of the game's own `CanonicalVars` IL). The GPU carries
it too: a payload criterion is `Fight = 0` in the card stage, which walks the front of the same
stream on its own copy of the Rng. Fault-injection tested on both sides.

**No payload can make fight 1 offer a Rare, and this is the thing everybody assumes wrongly.**
The rare threshold is `RegularRareOdds + CardRarityOdds.CurrentValue`, and `CurrentValue` is
*state*: burning draws does not move it. Nothing at Neow moves it either. `CreateForReward`
branches on `RarityOddsType == Uniform` and never reaches `RollForRarity` at all (Arcane Scroll,
Hefty Tablet, Scroll Boxes' bundles), and Massive Scroll's roll does reach it but with
`CardCreationSource.Other`, which routes to `RollWithBaseOdds` — a roll that reads the base odds
and writes nothing back. Only `Roll` mutates the counter, and only an encounter reward calls it.
So taking a card at Neow changes WHICH commons and uncommons the first fight offers, never their
rarities. Both pickers keep refusing rares on fight 1 whatever the pick.

Two other things fell out of the same re-read and are worth recording:
- **Massive Scroll draws from the character pool PLUS Colourless, filtered to
  `MultiplayerConstraint == MultiplayerOnly`.** That is a different pool from every other card
  feature here, which is one reason its payload is not simply "the same with base odds".
- **Scroll Boxes' 6 draws are 2 commons + 1 uncommon per bundle**, with ONE shared used-card set
  across both bundles, plus a leading `NextInt(100)` per bundle for the Defect only. The shared
  set is the same ordering trap `ChestRelics.Generate` documents.

### Card rewards, fights 1 and 2 — IMPLEMENTED, oracle-verified AND confirmed in play
`Core/Cards/` holds `CardPoolData.cs` (generated by `scratchpad/gen_cardpools.py` — regenerate,
don't hand-edit), `CardRewardGenerator.cs` (the draw chain) and `CardCatalog.cs` (naming).

Fight 1 is the only combat reward a seed pins with NO assumption, and the reason is structural:
`StandardActMap.AssignPointTypes` forces row 1 to `MapPointType.Monster` with
`CanBeModified = false`, so the first room after the Ancient is always a normal fight no matter
what route the party takes. Nothing else has touched `PlayerRng.Rewards` by then, and the pity
counters are still at their initial values. Contrast the shop, which is unpredictable for
exactly the opposite reasons (see "Shops: the third relic slot IS predictable").

Draw order, one `Rewards` stream per player. The potion roll happening at LIST BUILD, before
anything is populated, is the load-bearing detail:
```
RewardsSet.GenerateRewardsFor:   potion odds roll      NextFloat   (1)
RewardsSet.GenerateWithoutOffering populates in insertion order:
  GoldReward.Populate:           gold amount           NextInt     (1)
  PotionReward.Populate:         rarity + pick         2 draws     (only if the roll hit)
  CardReward.Populate -> CardFactory.CreateForReward, x3:
                                 rarity roll           NextFloat
                                 card pick             NextItem
                                 upgrade roll          NextFloat   (outside the IsUpgradable guard)
```
`Rewards.Sort` runs *after* Populate, so display order is not draw order.

Two properties fall out of `CardRarityOdds` and are worth knowing before anyone reports a bug:
- **The first fight can never offer a Rare.** The rare threshold is base odds plus a pity
  offset that starts at `-0.05f` and grows `0.01f` a draw, so it is still ≥ 0 on all three
  draws and no roll can fall under it. True at A7+ too (0.0149 base). The web catalog omits
  rares and `ValidateCardCriteria` rejects one outright, rather than scanning to no answers.
- **No card is ever upgraded in Act 1**, because the upgrade odds scale with the act index.

**THE CAVEAT — five Neow relics shift the whole result.** They draw off `Rewards` before the
fight does: Arcane Scroll 1, Hefty Tablet 3, Massive Scroll 9, Scroll Boxes 6 (8 for Defect,
which adds a 1-in-100 all-Claw check per bundle), Neow's Bones a shuffle of the Neow pool.
Encoded in `CardRewardGenerator.NeowRewardDrawCost`; everything else costs zero, so the default
`priorDraws = 0` is right for the great majority of picks, Silken Tress included. The UI says so
in the panel hint. Not automatic: nothing knows which Neow option a player will take.

Also unmodelled, and much less likely: if every enemy in the first fight escapes, `GoldProportion`
is 0 and no `GoldReward` is added at all, shifting the stream by one draw.

**Fight 2, added 2026-07-29.** The same walk, continued. Nothing resets between two Monster
rooms, so fight 2 is fight 1's stream carried forward with both pity counters intact:

- **Stream position** advances 11 draws per fight, plus 2 more when the potion landed.
- **`CardRarityOdds.CurrentValue`** grows by `RarityGrowth` per card drawn and resets to `-0.05f`
  whenever a Rare lands.
- **`PotionRewardOdds.CurrentValue`** moves 0.1 DOWN on a hit and 0.1 UP on a miss, so fight 2's
  potion threshold is never the base 0.4. Nothing clamps it.
- The card blacklist does **not** carry — it is scoped to one reward, so fight 2 can re-offer what
  fight 1 showed.

Player choices stay free: generation happens before the offer, so taking or skipping a card or a
potion moves nothing. What fight 2 costs is an ASSUMPTION, not a computation — that the party
walks straight into a second Monster room. Capped at fight 2 (`MaxPredictableFight`) because each
further room makes that assumption less likely, and a prediction quietly conditional on a
four-room hallway is worse than none.

Two things change at fight 2 and are easy to get wrong:
- **Rares become possible**, so the picker must stop hiding them and the API must stop rejecting
  them. Both were right for fight 1 and wrong here.
- **Ascension starts to matter for cards.** `RegularRareOdds` and `RarityGrowth` are gated on
  `AscensionLevel.Scarcity` (A7+), so `--ascension` is a real input to card prediction, not just
  the A10 double-boss switch.

**Verification.** `--verify` **cannot** cover this — the reward is rolled on room entry, not
during upfront generation, so it is not in the save's generation section. Two Oracle checks do:

- `Card pool rarities + MP constraints vs assembly` — constructs the game's class for all
  **454 distinct cards** across the five pools and compares `Rarity` and `MultiplayerConstraint`
  against `CardPoolData`. This is the one that matters most, because that table is scraped from
  source by a Python script and a misread rarity would be silently wrong rather than broken.
- `First fight card reward, draw-for-draw vs game Rng` — replays the chain with the game's own
  `Rng(seed, name)` constructor over 5 characters × 120 seeds × 2 slots, and asserts the four
  odds constants against `CardRarityOdds`/`PotionRewardOdds`. `RegularRareOdds` and
  `RarityGrowth` are ascension-gated *properties*, not literals, so they are not readable
  headless and stay unchecked.

`RegularRareOdds` and `RarityGrowth` are ascension-gated **properties**, so invoking them needs a
live `AscensionManager` and they went unchecked while rares were impossible. Since fight 2 they
are load-bearing, and the Oracle now reads their two `ldc.r4` literals straight out of the
getter's IL (`GamePropertyFloatLiterals`). That asserts the numbers, not which one a given
ascension returns; it fails loudly if a patch makes the getter anything but a plain
`GetValueIfAscension` call. Fault-injection tested.

**The played-run check is now done, and it closed the open question.** That second Oracle check
could never prove this draw sequence is the one `RewardsSet` actually performs — that came from
reading source, and a misreading would agree with itself. Seed `8NZJ8J63RAKH` (v0.109.1, 2-player,
A10) settles it: floors 2 and 3 were both Monster rooms, and **all four rewards matched exactly**
— P2 (Fishing Rod at Neow, zero cost) on both fights straight off, and P1 on both fights once
Hefty Tablet's 3-draw cost was applied. So the sequence, the potion pity carry between fights and
the `NeowRewardDrawCost` table are all confirmed against the game itself, not just against our
own reading of it. Card order within a reward is not comparable — `Rewards.Sort` runs after
Populate — so the match is on the SET of three.

### Acts 2/3 — engine written, verified via save files, wired to the search
`Core/Acts/` holds `ActData.cs` + `RelicPoolData.cs` (both generated by
`scratchpad/gen_actdata.py` / `gen_relicpools.py` — regenerate, don't hand-edit),
`GrabBag.cs`, `RunGenerator.cs`, and `ActCatalog.cs`.

Primitives are oracle-verified: `GrabBag` (including its predicate retry loop) and
`UnstableShuffle` are checked against the game's own public implementations. The assembled
chain cannot be run headless (it needs a live `ModelDb`/`RunState`) — but it does not need
to be, because **the game writes its entire generation output to a JSON save file**.

Epoch-gated relic removal is modelled (fixed 2026-07-28). It previously extracted **empty**,
because `Ironclad3Epoch.Relics` compiles to `CollectionsMarshal.SetCount` + span assignment and
the old regex in `gen_relicpools.py` cut the property at its first semicolon — capturing the
length and none of the relics. The generator now takes the brace-balanced body, and all 15
gates (5 shared, 2 per character) come out populated. This matters for release: pool size sets
draw count, so a partially-unlocked account gets a genuinely different run from the same seed.

**Unlock state is PER PLAYER, and the run's is their union.** `RelicGrabBag.Populate(player,
rng)` reads `player.UnlockState`, while `RunState.UnlockState` is
`new UnlockState(players.Select(p => p.UnlockState))` — documented in the game's own source as
"the superset of all players' unlock states", used for Ancients and act generation. So each
player's bag is filtered by their own epochs and only the local profile's are readable; a
partner's live on their machine. `RunGenerator.GenerateRun` takes an optional `playerUnlocks`
for this. Searching still assumes the lobby matches the local profile, which is the only
assumption available about a stranger's account.

### Bosses and events as search criteria
`ActCatalog.cs` is the naming and pool layer over `ActData`. Encounter type names carry their
role as a suffix (`CeremonialBeastBoss`, `NibbitsWeak`) — strip it with `BareName` before
anything a user reads. `Display` gives "Ceremonial Beast", `Slug` gives "ceremonial_beast".

Both criteria are **run-level**, not per-player: one boss and one event order serve the whole
lobby, so `BossCriterion` / `EventCriterion` carry no `SlotRequirement`.

`BossCriterion.Exclude` inverts the test, so "any Act 3 boss but the Queen" is one criterion
rather than an enumeration of the alternatives. The test is against the act's whole boss SET,
which is what makes Double Boss fall out for free: two include criteria pin the pair, one
exclude keeps a boss out of both slots.

Two consequences of Act 1 being the only act with a choice of map:
- Its two maps' boss lists are **disjoint**, so naming an Act 1 boss pins the map. The web UI
  groups the Act 1 boss dropdown by map to make that visible.
- A boss and an event from different maps can never co-occur. `SeedSearcher.ValidateActCriteria`
  tests each act's criteria against **one map at a time** and refuses upfront — otherwise the
  `MapsCouldSatisfy` pre-filter silently rejects every seed and the search reports "0 found",
  which reads as a bad seed range rather than an impossible query.

`MapsCouldSatisfy` runs off act selection alone (3 draws) before the ~412-draw run generation,
so an unreachable boss or event costs a lookup instead of a full run.

**Events are an order, not a schedule.** `RoomSet.NextEvent` is
`events[eventsVisited % events.Count]` and `?` rooms consume from the front, but
`EnsureNextEventIsValid` skips any entry that `IsAllowed(runState)` rejects or that
`VisitedEventIds` already holds — and **36 of the 68 event classes override `IsAllowed`**
(HP, gold, deck contents, `CurrentActIndex`, event pets). `VisitedEventIds` is run-wide, so a
shared event seen in Act 1 is skipped in Acts 2 and 3. Hence the criterion is "within the
first n", and both the CLI and the UI say why. Never phrase it as a guaranteed position.

### Ascension 10 — Double Boss, the one ascension effect on RUN generation
Not the only ascension effect the finder models. A7's `AscensionLevel.Scarcity` moves card
rarity odds, which is why `--ascension` is a real input to card prediction — see "Card rewards,
fights 1 and 2". The distinction is that Scarcity touches REWARDS; nothing below the acts, map,
bosses or Ancients moves with ascension except this A10 second-boss draw.

`RunManager.cs:731`, inside the per-act loop of `RunManager.GenerateRooms`: on the last act only,
and only when `AscensionManager.HasLevel(AscensionLevel.DoubleBoss)`, the act is given a second
boss via `SetSecondBossEncounter`. The value is one `UpFront.NextItem` over that act's whole boss
encounter list filtered to drop the boss already chosen.

`AscensionLevel.DoubleBoss` is the 10th enum entry and `HasLevel` is a `>=` test, so this is
**A10+**. `AscensionManager.maxAscensionAllowed` is 10 — StS2 caps there, unlike StS1's 20.

Three properties worth keeping in mind:
- **Final act only** (`i == State.Acts.Count - 1`).
- **One draw, and it is the last one generation makes** — after that act's Ancient, with
  nothing following. So every other prediction is byte-identical with the mode on or off, and
  a search that does not care about the second boss need not set ascension at all.
- **Drawn from the act's bosses minus the first**, so the pair is always distinct.

Oracle-checked against the game's own `Rng.NextItem` over every act, every possible first
boss, and 60 seeds — the filtered-LINQ-sequence shape matters, since a different survivor
order would pick the other boss every time.

`--verify` reads `ascension` straight out of the save and compares `second_boss_id`, so a real
A10 run tests this end to end without a flag.

### Boss discovery order — an unmodelled gap on partially-unlocked accounts
`RunManager.GenerateRooms` calls `act.ApplyDiscoveryOrderModifications(unlockState)` whenever
`ShouldApplyTutorialModifications()`, and despite the name that returns **true for every
Standard-mode run** — it only excludes Custom mode and test mode. The method walks
`BossDiscoveryOrder` and forces the first boss the account has not `HasSeenEncounter`.

It consumes **no RNG**, so it cannot shift the stream, and on a fully-unlocked account it is a
no-op — which is why `--verify` passes here. But on an account that has not met every boss,
our boss prediction is wrong, and so is the second boss that filters against it. Not modelled:
`UnlockState` has no seen-encounter set. Worth fixing if anyone with a fresh profile uses this.

### Shops: the third relic slot IS predictable, the rest is not
Corrected 2026-07-28. An earlier version of this section said shops could not be predicted at
all. That is right about two of the three relic slots and wrong about the third, which is the
one other seed finders expose.

`MerchantInventory.PopulateRelicEntries` does not roll three rarities. It rolls two: the three-slot
rarity array it builds takes its first two entries from `RollRarity` and its third is the constant
`RelicRarity.Shop`. `MerchantRelicEntry.FillSlot` then fills each with `RelicFactory.PullNextRelicFromBack`, which
**consumes no RNG at all** — it takes the back of the player's deque for that rarity. So the
third slot is simply the back of the Shop deque, which `RelicGrabBag.Populate` shuffled during
upfront generation off `UpFront`. Nothing else in the game ever draws Shop rarity, because
`RollRarity` returns only Common, Uncommon or Rare (oracle-checked over 50,000 rolls). Each shop
therefore takes exactly one relic off the back, and **shop N's third slot is the Nth-from-last
entry of that player's Shop deque** — a fixed sequence, decided before the run starts.

Implemented in `RunGenerator` (`withShopRelics: true` materialises the deque instead of burning
the shuffle) and searchable as `ShopRelicCriterion` / `--shop p1:belt_buckle[:visit]`.
26 relics per player: 25 shared plus their character's one.

Two narrow caveats, both stated in the UI:
- Counted by shops **visited**, not by floor. Walking past a merchant shifts the rest along.
- Dragon Fruit is the only Shop relic with an `IsAllowed` gate (`IsBeforeAct3TreasureChest`,
  floor 38 in co-op). Reaching a shop past that floor drops it rather than offering it, moving
  the remainder up by one. Only possible in the final act.

The other two slots remain unpredictable, though **not for the reason first recorded here**.

**Corrected 2026-07-29.** This section used to say their rarity rolls against a pity counter.
It does not. `RelicFactory.RollRarity` is one `NextFloat` against fixed thresholds with no state
at all: **below 0.5 is Common, below 0.83 is Uncommon, and 0.83 and above is Rare** — so 50 / 33
/ 17. Ported in `ChestRelics.RollRarity`.

> Corrected again 2026-08-08. This section previously carried a transcription of that method with
> Uncommon and Rare **swapped**, claiming the top band was Uncommon. The code was always right and
> only the note was wrong, but a wrong quotation reads as more authoritative than a wrong sentence,
> which is one more reason this file describes behaviour instead of reproducing it.

The pity counter belongs to CARD rarity (`CardRarityOdds`), which is a different class. So the
blocker on shop slots 1 and 2 is only the POSITION of `PlayerRng.Rewards` when the merchant is
built — every combat reward before it has advanced that stream by an amount that depends on the
route. Given a known route it is computable, which means these two slots are reachable work
rather than a hard limit. Unbuilt because that route input means modelling the map graph, the
same dependency elite relics carry.

The rest still holds:
- They then pull from the Common/Uncommon/Rare deques, which combat and chest rewards have been
  draining since floor 1. (They pull from the BACK, which front-draining rewards do not disturb
  — so these are knowable at the FIRST shop given the rarity rolls. The rarity rolls are the
  blocker, not the deques.)
- `Hook.ModifyMerchantCardPool` / `ModifyMerchantCardRarity` let carried relics rewrite pools.
- Merchant CARDS are `RollWithoutChangingFutureOdds(Shop)` off the same pity counter, plus
  `PlayerRng.Shops`, whose position depends on how many shops you have visited.

**Verified end to end**, not just reasoned: `--verify-history` compares the predicted sequence
against `relic_choices` in real saved runs. On seed `8NZJ8J63RAKH` (v0.109.1, 2-player, A10) all
six observations across three shops matched, in order, for both players.

### Treasure chests — IMPLEMENTED and confirmed against a real co-op run
`Core/Acts/ChestRelics.cs`. Added 2026-07-29. Run-level, unlike shops and cards.

**Every act has exactly one chest and no route skips it.** `StandardActMap.AssignPointTypes`
forces the whole of row `GetRowCount() - 7` to `MapPointType.Treasure` with
`CanBeModified = false`. The one flag that swaps that row for elites
(`shouldReplaceTreasureWithElites`, a Warden mode) is passed a hardcoded `false` at its only call
site in `RunManager`. In co-op the three chests sit at **floors 9, 24 and 38** — read off a real
run, not derived.

`TreasureRoomRelicSynchronizer.BeginRelicPicking` is the whole draw. Per player, in slot order:
```
rarity = RelicFactory.RollRarity(rng)       1 NextFloat, on the RUN-level stream
relic  = sharedGrabBag.PullFromFront(...)   consumes NO rng
```

Three properties follow, and they are what make this worth having:

- **The stream is `RunRngType.TreasureRoomRelics`, and nothing else in the game draws from it.**
  `BeginRelicPicking` is called from exactly one place, `TreasureRoom`. The rarity roll is a plain
  `NextFloat` against fixed thresholds — no pity counter, no ascension term. So a chest's rarities
  depend on exactly one thing: how many chest picks came before it.
- **The award phase is free in the ordinary case.** It only draws when players contest a relic
  (rock-paper-scissors) or leave one unclaimed (`StableShuffle` of the leftovers), and
  `UnstableShuffle` of 0 or 1 element consumes nothing. Everyone picking a different relic costs
  zero.
- **It is a SHARED pick.** One relic is rolled per player and the whole party votes on the set, so
  the seed fixes what is on the table and the table decides who gets it. Hence `ChestRelicCriterion`
  carries no slot rule, and naming two relics for one act means "both are in that chest".

**What limits it, and both are stated in the UI:**

1. **Identity, not rarity, drifts.** `PullFromFront` takes the front of the SHARED bag, and every
   relic anyone obtains calls `SharedRelicGrabBag.Remove` on it — elite rewards, a merchant's
   stock (removed when the shop is POPULATED, not when bought), relic events. Those removals land
   at arbitrary positions, because the shared bag and each player's bag are independent shuffles.
   So `ChestSlot.Candidates` carries the ordered fallbacks and the criterion takes a tolerance.

   **That tolerance is a claim about the RUN, not about a relic** (corrected 2026-08-01). How far
   the bag had drained by a given chest is one number per rarity, shared by every slot of that
   rarity in that chest, so two relics named for one chest must be read at the SAME index. Asking
   each want separately whether its relic appears anywhere in the first n+1 entries accepts pairs
   that are individually reachable at drain counts which cannot both be true, and therefore can
   never share a chest. `ChestSlot.At(drained)` takes one index for this reason; there is
   deliberately no range-scanning helper. The loop over drain counts lives in
   `SeedSearcher.ChestSatisfies`, one per distinct rarity in the chest.
2. **A `?` room can become a treasure room.** `UnknownMapPointOdds` rolls Treasure at a 2% base
   that grows 2% each time it is not rolled (reset per act), and that runs `BeginRelicPicking`
   too — a full player-count of draws, shifting every later chest. Hence `--extra-chests`.

**Act 3's chest strips 16 relics first.** `RelicModel.IsBeforeAct3TreasureChest` is
`TotalFloor < 38` in multiplayer, which is *exactly* the Act 3 chest's floor, so everything gated
on it (`WhiteStar`, `Girya`, `OldCoin`, …) is removed by `RemoveDisallowedRelicsFromDeques` before
that pull and not before the earlier two. The gate removes ENTRIES, so what has already been taken
must be tracked by identity rather than as a per-rarity count — a count indexes into the shortened
list and skips a relic still on offer.

**Verified end to end** on seed `8NZJ8J63RAKH` (v0.109.1, 2-player, A10), which had a `?`-treasure
at floor 6 and so needed `extraPicksBefore: 1`:

| Act | Predicted | Observed | |
|---|---|---|---|
| 1 | Strawberry (C), White Star (R) | same | exact |
| 2 | Juzu Bracelet (C), Vajra (C) | War Paint, Vajra | Juzu Bracelet was in the floor-5 shop's stock |
| 3 | War Paint (C), Tiny Mailbox (U) | Potion Belt, Tiny Mailbox | Uncommon exact; Common drifted by two elite relics |

Every deviation is accounted for by a recorded relic acquisition. With `extraPicksBefore: 0` the
model instead predicts Lasting Candy and Ripple Basin for "act 1" — which is precisely what that
floor-6 `?` chest handed out, so the shift is confirmed from both directions.

### Run saves are the Act 2/3 oracle
`%APPDATA%\SlayTheSpire2\steam\<steamId>\profile<N>\saves\` — plain `System.Text.Json`,
readable, no encryption:
- `current_run.save` / `current_run_mp.save` — the in-progress run. `SerializableRun` holds
  `rng.seed`, `players[].character_id`, and per act a `SerializableRoomSet` with
  `event_ids`, `normal_encounter_ids`, `elite_encounter_ids`, `boss_id`, `ancient_id` —
  i.e. **every draw `GenerateRooms` makes**, in order. Also `shared_relic_grab_bag` and
  `players[].relic_grab_bag` (ordered lists per rarity), and `rng.rngs.up_front.counter`,
  which is a single-integer check on total draw accounting.
- `progress.save` — `epochs[]` (id + `revealed`), `discovered_acts`, stats. Read this
  instead of assuming a fully-unlocked account (`UnlockState.FromRevealedEpochs`).
- ModelIds serialize as `"TYPE.ENTRY"` where ENTRY is `Slugify(typeName)`.

`sts2seed --verify [path]` diffs our `RunGenerator` against such a save and prints the first
divergence. **This is why a co-op partner is not needed to test:** a singleplayer run
exercises the identical code path, so it validates relic-bag accounting, the shared-Ancient
shuffle, every act's event/encounter tables and tags, bosses and Ancients. The only things
it leaves untested are the two MP deltas — player count feeding the relic bags, and
`GetNumberOfRooms(isMp)` — which `current_run_mp.save` covers when a co-op run exists.

Caveat when interpreting a failure: this machine has mods installed (BaseLib, CrashGuard,
skins under `mods/` and `CznModConfig/`). A mod that adds relics, events or encounters
changes pool sizes and will look like a port bug. Verify vanilla first.

**The draw order that matters** (one sequential `UpFront` stream, all acts):
```
InitializeNewRun():                       <-- BEFORE GenerateRooms, easy to miss
  SharedRelicGrabBag.Populate(...)        shuffles each rarity deque (unfiltered!)
  per player: RelicGrabBag.Populate(...)  shared+character pools, filtered to
                                          {Common,Uncommon,Rare,Shop}
GenerateRooms():
  shared Ancients (Darv) UnstableShuffle
  per act after the first: NextInt(count+1) -> Take(n)
  per act: events(+shared, minus locked epochs) shuffle
           weak x N, regular x (rooms-N), elite x 15   (AddWithoutRepeatingTags)
           Boss    = NextItem(bosses)
           Ancient = NextItem(unlocked + sharedSubset)  <-- the target, rolled LAST
```
A shuffle of n consumes exactly n-1 draws, so only per-rarity **counts** matter for the
relic bags. All five characters have **identical** pool counts (Common 1, Uncommon 2,
Rare 3, Shop 1), so party composition and slot order do *not* shift the UpFront stream —
only **player count** does. (An earlier note here claimed composition mattered; it does
not, though it would the moment a patch gives one character a differently sized pool.)

### Ancients' offers — IMPLEMENTED and oracle-verified
Act 2 (Hive): Orobas *(needs OrobasEpoch unlock)*, Pael, Tezcatara.
Act 3 (Glory): Nonupeipe, Tanx, Vakuu. Darv is a *shared* Ancient distributed across acts.

`Core/Ancients/` holds `AncientData.cs` (generated by `scratchpad/gen_ancientdata.py` —
pools only) and `AncientOffers.cs` (the draw algorithms, hand-written because each differs
and the draw ORDER is the load-bearing part). Oracle check `Ancient offers, draw-for-draw
vs game Rng` covers all 7 across 400 seeds x 2 slots.

No Ancient overrides `EventModel.IsShared`, so **all of them roll per player**, same as Neow,
off `runSeed + slotIndex + XXH64(Slugify(typeName))`. That stream is independent of `UpFront`,
so offers are computable without act generation — only *which* Ancient appears needs the run.

Determinism, corrected (an earlier note here claimed Pael/Nonupeipe/Tanx were pure — they
are not; only Vakuu is):
- **Vakuu** — fully seed-determined. 3 pools, each shuffled, first of each taken.
- **Tanx** — +Tri-Boomerang at 3+ Instinct-enchantable cards. 2 branches.
- **Nonupeipe** — +Beautiful Bracelet at 4+ Swift-enchantable cards. 2 branches.
- **Tezcatara** — +Nutritious Soup when a basic Strike is still in the deck. 2 branches.
- **Pael** — +Pael's Claw (3+ Goopy), +Pael's Tooth (5+ removable), +Pael's Legion (no
  event pet). Up to 8 deck states. Note `list.AddRange(list)` doubles pool 2 *before*
  Pael's Growth is appended, so Growth is offered at half weight — not a decompiler artefact.
- **Orobas** — draws another character (for Sea Glass) and a float *before* any option, both
  consuming draws. Archaic Tooth drops out if the transcendence starter card was removed.
- **Darv** — one draw per eligible relic set (a 1-relic set still costs a draw), then a
  shuffle, then `NextBool` decides whether Dusty Tome replaces the third option. Two sets are
  gated on `CurrentActIndex == 1`, so **Darv's pool depends on which act he lands in**.

`Rng.NextFloat()` gotcha (fixed 2026-07-24, was a real bug): the game's `Rng.NextFloat(max)`
delegates to `NextDouble()` (`>> 11`), NOT to `MegaRandom.NextFloat()` (`>> 40`). Same draw
count, different value. Only Orobas reads it.
2. **Which Ancient appears** — requires reproducing ALL upfront generation, because
   `ActModel.cs:385` rolls it last:
   ```
   RunManager.GenerateRooms():                       // all acts, one sequential UpFront pass
     shared ancients (Darv) UnstableShuffle(UpFront)
     per act after the first: UpFront.NextInt(count+1) -> Take(n) subset
     for each act: act.GenerateRooms(UpFront, unlockState, players>1)
        events (+shared, minus locked epochs) UnstableShuffle
        NumberOfWeakEncounters   x AddWithoutRepeatingTags(weak grab bag)
        GetNumberOfRooms(isMp)-N x AddWithoutRepeatingTags(regular grab bag)
        15                       x AddWithoutRepeatingTags(elite grab bag)
        Boss    = rng.NextItem(AllBossEncounters)
        Ancient = rng.NextItem(unlockedAncients + sharedSubset)   <- the target
   ```
   Still needed: port `GrabBag.GrabIndex` + `AddWithoutRepeatingTags` (retries on
   `SharesTagsWith(last)`, so draw counts vary), and encode per-act event/encounter
   lists **with encounter tags**, plus `NumberOfWeakEncounters` / `GetNumberOfRooms`.

### MP-specific differences
- `ActModel.GetRandomList(rng, unlockState, isMultiplayer)` (`StartRunLobby.cs:464`). Act selection RNG is
  `new Rng(hash(seed), "act_selection")`. In SP, undiscovered acts are force-picked before the roll; in MP
  that branch is skipped — act order genuinely differs by mode.
- Acts 1 floor shorter each; bosses at floors 16/31/45. Confirmed in code: `GetNumberOfRooms`
  returns `BaseNumberOfRooms - 1` in MP, and base rooms 15/14/13 + 2 floors each reproduce
  16/31/45 exactly. Also corroborated by `RelicModel.IsBeforeAct3TreasureChest`:
  `(Players.Count > 1) ? 38 : 41`.
- `CardMultiplayerConstraint { None, MultiplayerOnly, SingleplayerOnly }` filters card pools.
- Neow relic pool differs: no Silver Crucible or Winged Boots, adds Massive Scroll. **No longer
  wiki-sourced** — these are `IsAllowed => Players.Count == 1` / `> 1` checks read off the game,
  encoded as `RelicAvailability` in `Core/Neow/NeowRelics.cs`, and confirmed in play across
  several co-op runs.
- Golden Compass makes Act 2 a single path 2 floors longer, shifting later floor numbers *(wiki-sourced)*.
- Runtime MP check elsewhere is `RunState.Players.Count > 1`.

### What a singleplayer `--verify` run does and does not prove
Verified 2026-07-24 on seed `KXRKMHZPH85U` (A10 Defect, SP, all epochs revealed): act
selection, relic bag sizes, and all three acts' event lists, encounter orders, bosses and
Ancients matched draw-for-draw; UpFront counter 412 vs the game's 413 (one draw ahead
because the run had started).

Shared with MP, therefore now verified for both modes:
- The entire `UpFront` chain — relic bag accounting, shared-Ancient shuffle and per-act
  subset draws, per-act event shuffles, weak/regular/elite `AddWithoutRepeatingTags` draw
  counts (so the encounter tag tables are right), boss and Ancient picks.
- **Act selection**, because this profile has all four acts discovered: the SP-only
  force-pick at `ActModel.cs:550` is a no-op then, so SP took the identical pure-RNG path.
- **Ascension does not affect any of the above** — `GenerateRooms(rng, unlockState,
  isMultiplayer)` has no ascension parameter, and an A10 run matched a model that ignored
  it entirely.

  **Corrected 2026-07-26**: an earlier version of this line said "ascension is irrelevant",
  full stop. That is wrong. `ActModel.GenerateRooms` ignores ascension, but its *caller*
  `RunManager.GenerateRooms` does not: at A10 (`AscensionLevel.DoubleBoss`) it draws a second
  boss for the final act. See below. The verified run stands, because that draw comes after
  everything the run was checked against.

  Note for the next `--verify`: that run reported UpFront 412 ours vs 413 saved, explained at
  the time as "the run had started". It was an A10 run, so the missing draw may simply have
  been the second boss. We model it now, so the next A10 save settles which.

**Both multiplayer-only lines are now VERIFIED** (2026-07-28), and no co-op partner was needed
after all — finished runs are kept in `saves/history/*.run`, and this account already had past
co-op runs. `sts2seed --verify-history` reads them.

Seed `8NZJ8J63RAKH` (v0.109.1, standard, 2 players, A10) matches exactly:
1. `RunManager.cs:493` loops `Populate` once per player off `UpFront`. Two players means one
   extra bag, +118 draws. If that loop count were wrong every later draw would be misaligned;
   acts, all three bosses, the A10 second boss, the Ancients, the encounter order and all six
   shop-relic observations matched.
2. `GetNumberOfRooms(isMultiplayer)` is `BaseNumberOfRooms - 1`. That run's map has 16/15/15
   points per act, giving boss floors 16/31/45 — the documented co-op numbers.

The old note here said a partner was required because `StartRunLobby.cs:737` refuses to start a
run when `IsMultiplayer() && Players.Count == 1`. True, but it only blocks STARTING one; it says
nothing about runs already played.
