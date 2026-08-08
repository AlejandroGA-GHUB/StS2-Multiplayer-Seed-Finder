# Web UI — specification

The browser front end for the co-op seed finder. Everything the CLI can do, made usable by
someone who has never touched a terminal and does not know what a "lobby slot index" is.

Status: **phases 1–4 built.** `src/Sts2.SeedFinder.Web`, run with
`dotnet run -c Release --project src\Sts2.SeedFinder.Web`. Mobile layout (phase 5) is
deliberately deferred.

## 1. Goals

- **Intuitive enough for a new player.** Someone who just wants "a seed where my friend and
  I both get Silken Tress" should get there without reading documentation.
- **Dark themed**, comfortable for long sessions, consistent with the game's own mood.
- **Pickers show art and explain what the thing does.** A name like "Pomander" means nothing on
  its own; the picker has to teach. This holds for cards as much as relics, and cards need it
  more — a character has 58 offerable ones.
- **Full expressive power.** Any combination the CLI supports: per-player Neow requirements,
  a specific Ancient, a specific relic from that Ancient, a card for each player after the
  first fight, or any mix across acts.
- **Honest about uncertainty.** Where an outcome depends on your deck rather than the seed,
  the UI says so rather than picking a branch and pretending.

## 2. Non-goals

- **No singleplayer mode.** Same scope rule as the rest of the project.
- **No accounts, no persistence, no database.**
- **No monetization.** Free tool, no ads, no donations tied to it. See §4.
- **No build toolchain.** No npm, no bundler. One `dotnet run`, static HTML/CSS/JS.

## 3. Distribution

**Decided: released on GitHub for people who own the game, run locally. Not hosted.**

This is the cleanest posture available. Each user runs it against their own install, art is
read from their own copy at runtime, and nothing is ever redistributed — there is no
permission question to resolve, because no asset leaves the machine that already owns it.

| Mode | Who runs it | Asset provider | Status |
|---|---|---|---|
| Local | Anyone who owns the game | `local` | **The shipping mode** |
| No game | Anyone | `none` | Supported; monograms |
| Hosted | — | `bundled` | Built, but not the plan; see §4 |

Because it now ships to other people's machines, **finding the install is a first-class
concern** rather than a convenience: a missed install is the difference between a working app
and a grid of monograms. Steam permits libraries on any drive, so `GameInstall` reads the
Steam registry key and every path in `libraryfolders.vdf` across Windows, macOS and Linux,
and `Assets__GameDirectory` overrides it. The app still runs correctly with no game present.

## 4. Game art and descriptions — the constraint

Relic icons, card portraits, character portraits, Ancient node icons, event illustrations and the
descriptions of relics and cards are all Mega Crit's assets. The project's provenance rules (see
`CLAUDE.md`) forbid committing or redistributing them, and that rule does not bend because a
feature would be nicer with them. Each new kind changed the volume — 633 card portraits on top
of 324 relic icons, then 55 event illustrations — but not the boundary: same .pck, same runtime
read, same in-memory cache, nothing on disk.

**What Mega Crit's [Content Policy](https://www.megacrit.com/content-policy/) actually says:**
it covers video, mods and merchandise. Fan websites and tools are **not addressed**. The only
asset clause — "not assets taken directly from our game or marketing materials" — is scoped to
*merchandise you sell*. So there is neither permission nor prohibition for a free fan tool.
Other fan sites (SearchTheSpire, the wikis) operate in that same unaddressed space.

Consequences for the design:

- **Assets are never committed to this repo.** Not in any mode. Not as a sprite sheet, not
  as individual files, not base64-inlined in a source file.
- **No monetization**, which keeps the tool clearly inside the spirit of their policy.
- If the tool is ever hosted publicly, **ask Mega Crit first** — their policy points to their
  Discord for fanworks. That is the only route to actual permission.

### Decoupling a deployment from the game install (built, not the shipping path)

`--export-assets` decodes every icon out of a local install into a plain folder:

```
dotnet run -c Release --project src\Sts2.SeedFinder.Web -- --export-assets .\assets
```

324 PNG/WebP files, ~14 MB, named by slug, plus `relic_text.json` — the 306 resolved
descriptions, which travel with the icons or a bundled deployment loses its tooltips. Point a
deployment at it with `STS2_ASSETS=bundled` and `Assets__Directory=<path>` and the server never
needs the game.

**This solves the technical problem, not the permission one.** Serving that folder publicly is
redistribution of Mega Crit's art, which their Content Policy does not grant. Exporting for
your own use is a different act from publishing. The genuinely clean options are: ask Mega Crit
(their policy points to their Discord), ship original art, run with `none` and use monograms,
or have each visitor supply their own game files client-side.

### Asset providers

Configured by `Assets:Provider` in `appsettings.json`, or `STS2_ASSETS` in the environment.

- **`local`** *(default)* — reads from the player's own installed game. Resolves
  `SlayTheSpire2.pck`, decodes relic textures on demand and reads
  `localization/eng/relics.json` once at startup. It also loads `sts2.dll` — into its own
  `AssemblyLoadContext`, by reflection, never as a compile-time reference — because the
  quantities in a description live in the code rather than the text file. All of it is held
  **in memory only**: nothing is written to disk, so there is no cache to leak into a repo or
  a backup, and the bytes never leave the machine that already owns them. Requires parsing the
  Godot 4 PCK container and `.ctex` texture wrapper — see §9 Phase 3.
- **`bundled`** — serves whatever is in a gitignored `assets/` directory. The escape hatch
  for public hosting, populated by the operator, never by this repo.
- **`none`** — no art. Each relic renders as a generated monogram tile, colour-derived from
  its name hash so it is at least visually distinguishable. Descriptions fall back to the
  factual metadata we already hold (which pool it belongs to, what gates it).

Provider selection is per-request-cheap and resolved once at startup. A failure to load
assets **must degrade to `none`**, never crash the app.

## 5. Visual design

Dark, low-chroma, with a single warm accent so the primary action is unmistakable.

```
--bg-0    #14131a   page
--bg-1    #1c1b24   panels
--bg-2    #26242f   inputs, cards
--border  #35323f
--text-0  #e8e6ef   primary
--text-1  #a09cb0   secondary
--accent  #d4a24c   primary actions, focus rings
--curse   #b06a9e   curse-branch relics
--good    #6ba86b   confirmed / guaranteed
--warn    #c98a4b   branch-dependent / uncertain
```

- System font stack; no webfont downloads.
- 8px spacing grid. 6px corner radius. One elevation level — panels, not floating cards.
- Motion under 150ms, and only on state changes the user caused.
- Respect `prefers-reduced-motion` and `prefers-contrast`.
- Must be keyboard-navigable end to end, with visible focus rings. The relic picker in
  particular has to work with arrow keys and type-ahead.

## 6. Layout

Two panes on desktop, stacked on narrow screens. Breakpoint at 900px.

```
┌───────────────────────────┬────────────────────────────────────┐
│  CRITERIA (scrollable)    │  RESULTS                             │
│                           │                                      │
│  Lobby                    │  stop after / seeds / start   Inspect │
│   players 2 3 4           │  ──────────────────────────────────  │
│   P1 [Ironclad ▾]         │  [ Search ]  [ Cancel ]   n found    │
│   Ascension [A10 ▾]       │  ┌────────────────────────────────┐  │
│   [Sync from my save]     │  │ 1EMQY13NZN03           [copy]  │  │
│  Neow                     │  │ Act1 Overgrowth · The Kin      │  │
│   offers [relic picker]   │  │  P1  ◆ Precise Scissors        │  │
│   CURSE BRANCH  (label)   │  │      ✦ Silken Tress   (curse)  │  │
│   to  ( any | all | P1 )  │  │  ▸ Card rewards                │  │
│  Card rewards             │  │  ▸ Treasure chest, floor 9     │  │
│   P1 [card] [fight 1 ▾]   │  │ Act2 Hive · ⬤ Pael             │  │
│  Shop relic               │  │  P1  depends on deck  ▾        │  │
│   P1 [relic] [1st shop ▾] │  │  ▸ Event order, first 10       │  │
│  Treasure chest           │  │ Act3 Glory · Queen + Aeonglass │  │
│   [act] [relic] [allow n] │  │  P1  Fiddle · Preserved Fog…   │  │
│  Bosses / Events          │  │ ▸ Shop relics, third slot      │  │
│  Ancients                 │  └────────────────────────────────┘  │
│   [+ add …]               │                                      │
└───────────────────────────┴────────────────────────────────────┘
```

Scan settings sit in the results pane's top row rather than at the foot of the criteria
column. They describe how to run a search rather than what to search for, and the criteria
column is the one that keeps growing; giving it back that height matters more than the
grouping did.

## 7. Interaction detail

### Lobby
Player count 2–4 as a segmented control. One character dropdown per player, in lobby order,
each labelled with a plain-language note: *"lobby order matters — this is who joins first."*
Characters are required for anything involving acts, so the Ancient section stays disabled
with an inline explanation until they are set.

Each character carries its **portrait**, from the art the game's own character select uses, in
both the closed dropdown and the open list. The art is head-and-shoulders and taller than it is
wide, so it is cropped rather than letterboxed, and pulled up slightly (`object-position: 50%
22%`) because a centred square crop cuts the face.

Those portraits are matched against the `Character` enum, **not** by scraping the
`character_select/` folder. That folder is mostly the select screen's own chrome — a button
mask, an outline, a lock badge, a player icon, a "random" option — and a `char_select_*` glob
cheerfully reports all of it as playable characters. Anchoring on the enum also drops the
`_locked` silhouettes for free.

### Dropdowns are ours, not the browser's
There is no `<select>` anywhere in this app. The native popup is painted by the browser rather
than by the page: its highlight bar is the **system accent**, a bright blue, and no stylesheet
reaches it. `color-scheme: dark` fixes that popup's background but not the bar, so the list had
to be ours to draw.

- The trigger is a `<button class="dd">`, deliberately: every "disable this whole panel" pass
  selects `button`, so `disabled` keeps working with no special case.
- The list is `position: fixed` and appended to `<body>`, because the criteria panel scrolls and
  an absolutely positioned list inside it would be clipped at the panel edge. It flips above the
  trigger when there is no room below, and closes on scroll and resize since its position is
  computed once.
- **Its height is measured per open, not capped by a CSS constant.** The page is exactly one
  viewport tall and never scrolls, so anything the list pushes past the bottom edge is simply
  unreachable — a long list like Ascension's eleven entries looks like it stops early. `place()`
  measures what the list wants uncaptured, compares that against the room above and below, and
  caps to whichever side wins.
- **A scroll inside the list must not close it.** The close-on-scroll listener is on the capture
  phase so it catches the scrolling criteria panel, which also means it sees a wheel over the
  list's own overflow. Closing there makes every entry below the fold unreachable, so the
  handler ignores events originating inside `.dd-list`.
- One list open at a time; opening another closes the first, as a native select does.
- Keyboard is not optional: Enter / Space / Arrow open, Arrows and Home / End move, Enter picks,
  Escape and Tab close, and single characters do type-ahead. `role="listbox"` / `role="option"`
  plus `aria-expanded` and `aria-activedescendant` carry it to a screen reader.
- Groups render as inert header rows in the same flat list, so keyboard movement is one index
  and headers are simply skipped.
- Disabled options are drawn dimmed, get no click handler, and `pick()` refuses them, so the
  programmatic path cannot do what the pointer path forbids.
- `.value`, `.options` and `.pick(value)` mirror the parts of the `<select>` API the rest of the
  file used, which is what kept the call sites and the test harnesses small.

### Relic picker
A modal, opened from a field that shows the current selection as icon + name. **One picker,
used everywhere anything is chosen**: Neow, every Ancient row and every card row share the same
field, the same modal and the same tooltips, so art and descriptions are never available in one
place and missing in another. Only the group headings and the asset kind differ.

- Search box focused on open; filters as you type, over both display name and slug.

  Its handler must be wired **before** `openPicker` returns its promise. It originally sat
  after the `return`, i.e. unreachable, and the box was inert from the day it was added — the
  bug survived because `render` is a hoisted function declaration, so the initial unfiltered
  render worked and the control looked wired. Fixed 2026-07-28.
- Grouped by pool with headers: **Curse branch**, **Positive pool**, **Coin-flip pairs** for
  Neow; a single "everything this Ancient can offer" group for an Ancient; **Common** and
  **Uncommon** for cards, since rarity is what decides how long the search will take.
- 3-column grid of tiles: icon, name, and an availability badge where one applies
  (co-op only, needs all characters unlocked, …). Singleplayer-only relics are hidden
  entirely — this tool is co-op.
- **Hover or focus shows a tooltip**: name, the description from the loc table, and for relics
  the one fact the game will not tell you — which pool it comes from and what gates it.
- Relic icons are square art drawn to fit; card portraits are wide paintings, so they are
  cropped to centre (`object-fit: cover`) rather than letterboxed down to a few legible pixels.
- **Event art needs more than a crop rule.** There is one illustration per event, but it is the
  full-screen scene: 3440x1616, a 1.3 MB PNG, 22 MB of pixels, for a 26 px tile. It is cropped
  to a square and downscaled server-side (`EventMaxEdge`), which takes it to ~150 px and under
  20 KB. WHERE it crops is chosen per event by scoring candidate windows on luminance variance,
  because these scenes are composed around a dialogue panel and the subject is not consistently
  placed: a fixed `object-position` gave good tiles for some events and black squares for others.
  Two events have no art of their own and borrow it instead; see `BorrowedEventArt` below.
- **Event text is a script, so only the choices are read.** `localization/eng/events.json` is not
  shaped like `relics.json`: an event's keys are its whole branching narrative, with
  `pages.INITIAL.description` for scene-setting prose, `pages.INITIAL.options.<OPT>.{title,
  description}` for each choice and what it does, and a further page per outcome.
  `GameText.ReadEvents` takes the **options** and nothing else, because what decides whether an
  event is worth searching for is what it offers, not its prose. Outcome pages are spoilers.
  `*_LOCKED` options are dropped: they restate an option for when you cannot afford it ("Requires
  X Gold") and add a line without adding a choice. Longest list is 6 options / 225 characters.
- **Option effects interpolate values, and those now resolve.** Two thirds read "Pay
  {ArachnidAcupunctureCost} Gold". `RelicVars` was extended to event models, whose type names are
  already our slugs (`ZenWeaver`, `ByrdonisNest`), so 39 of 57 events read as real figures: "Pay
  250 Gold", "Fight a 150 HP dummy".
  **The fix that mattered was populating the model database first.** Models resolve each other
  through `ModelDb` — Wood Carvings names a card, Tea Master a relic, Pael's Claw an enchantment —
  so constructing only relics, cards and events threw `KeyNotFoundException` for anything
  referencing a kind we had skipped, and 19 events plus Pael's two relics fell back to X.
  Injecting **every** `AbstractModel` subtype first, the same call `--refresh` makes, resolves
  them. Startup stays ~1.6s to a served catalog.
  Two things to keep in mind if this is touched: the vars read must be guarded **separately** from
  construction, because one model whose vars reach for run state would otherwise empty the whole
  table; and instances have to be taken from `ModelDb._contentById` rather than constructed again,
  since `AbstractModel`'s constructor registers and a second one throws `DuplicateModelException`.
- **The option list is what the text files DECLARE, not what the game offers.** Spiraling
  Whirlpool's `pages.INITIAL.options` carries a third choice ("Reach In") that does not appear in
  play, and the same install keeps other dead variants around (`trial_started`,
  `zen_weaver_phobia_mode`, `dense_vegetation_foreground` are all in the art folder). Nothing in
  the localization data marks an option live, cut or conditional. Filtering to the real list means
  reading each event model's own option-building code, which is per-event work rather than one
  rule. Until then the list is a **superset**, and the README says so.
- **One model-wide failure, not one missing number.** `DynamicVars`' own GETTER throws
  `NullReferenceException` for 18 events plus Pael's Claw, Pael's Growth and Royal Stamp, because
  the set it builds includes a var whose value is a NAME, and a name is blank until the engine's
  localization manager fills it in. The getter is the single entry point, so one such var costs
  that model its numeric vars as well: `{BoneTeaCost}` reads X despite being a plain number.
  There is no finer-grained route to it. The set is built inside the getter, the models expose no
  other var-like member (only `DynamicVars`, plus `Title`/`InitialDescription` as `LocString`), and
  enumerating members blind to look for one **segfaults the runtime**. Do not repeat that.
- **Names are recovered from IL instead** (`RelicVars.ReferencedModelIds`). A placeholder that
  wants a name is named after the KIND it wants (`{Enchantment}`, `{Potion}`, `{Relic}`,
  `{Card}`), and the model's own code says which one — but NOT as a string. The game looks models
  up generically, `ModelDb.Enchantment<Sown>()`, so what is read is the **generic argument of each
  call**, and the id is rebuilt the way the game would: kind from the argument's model base class,
  name from `Slugify` of its type name. `ENCHANTMENT.SOWN` then resolves to "Sown" via
  `enchantments.json`. My first attempt scanned for `ldstr` and found nothing, for exactly this
  reason.
  It substitutes **only when the model references exactly one id of the kind asked for**. Wood
  Carvings names two cards behind one `{Card}` token that the game fills per option, so nothing
  here can say which is which and it keeps its X rather than being guessed at.
  Two spellings of the same token exist, and missing that cost a day of looking in the wrong place:
  Royal Stamp's template says `{Enchantment}`, Pael's Claw's says `{EnchantmentName}`. With only the
  bare form matched, Royal Stamp resolved and Pael's two did not, which read as a deeper limit and
  was a second name for one token. `SubstituteNamedRefs` accepts both, and with that **every relic
  description resolves in full** — the same completeness other seed finders get by shipping a
  curated table, reached here without shipping anything.
- **What remains is 42 placeholders in 18 events**, in three groups: numbers the game computes
  mid-run (`{Heal}`, `{Gold}`, `{HpLoss}` — the bulk of it, and unreachable for the getter reason
  above); ambiguous multi-model references (Wood Carvings, Amalgamator, Bugslayer); and nested
  descriptions rather than titles (`{BoneTeaDescription}`).
- **A zero-valued var is treated as unknown, for events only** (`GameText.Meaningful`). A few
  models hold 0 for a quantity the game computes per run, and "Gain 0 Gold" is worse than "Gain X
  Gold": it reads as a real promise of nothing. No event option meaningfully offers zero, so there
  is nothing true to lose. Relics and cards are excluded deliberately, since a relic can
  legitimately say 0 and the same rule would corrupt text that is currently right. One literal 0
  survives and should: Infested Automaton's "random 0 cost card" is written that way in the game.
- **Two events have no illustration and borrow one** (`BorrowedEventArt`). The Merchant??? is a
  monster impersonating a shop, staged live from merchant-room props rather than drawn as a scene,
  so it takes the merchant icon the run summary uses; The Lantern Key takes its quest card's
  portrait. Neither is derivable by pattern, because neither lives under `images/events/`. The
  table only fills gaps, so real art in a later patch wins automatically, and a borrowed path that
  stops resolving is reported in the asset status rather than silently becoming a lettered tile.
- `fake_merchant` has no options and `"Placeholder"` for its prose: it is staged as a shop rather
  than a dialogue, so its own text amounts to a title, the game's `The Merchant???`. Its art is
  borrowed, per above.
- **Event titles come from the install where it has one**, the same call cards make. Splitting on
  capitals gives "Doors Of Light And Dark" and "Welcome To Wongos"; the game says "Doors of Light
  and Dark" and "Welcome to Wongo's". Descriptions carry a wider markup palette than relic text
  (`orange`, `aqua`, `rainbow`, `b`, plus the `jitter`/`sine` animations, which are parsed and
  left unstyled), and they are laid out `white-space: pre-line`, since they are written as
  paragraphs and read as one blob without it.
- **Ancient node art is a white mask, not artwork** — the game tints it at runtime. Drawn as an
  image it is a flat white blob, so it is applied as a CSS mask and painted per Ancient.
- Escape closes, Enter selects, arrows move. Clicking the backdrop closes.

### Neow relic
A relic field, a **branch label**, and "who must get it".

The branch label replaced an "In which slot" dropdown offering Anywhere / Curse branch /
Positive options. That control could never do anything, for a reason worth writing down so
nobody re-adds it:

- Neow always offers **exactly one curse relic and two positives**, so there is no such thing
  as an offer with no curse branch to filter on.
- Its real meaning was therefore "which of the three slots must the *named* relic occupy" —
  coherent in principle, but **no relic appears in both pools**. Mega Crit pairs each curse with
  a *counterpart* that is a different relic (Cursed Pearl ↔ Golden Pearl, Hefty Tablet ↔ Arcane
  Scroll), so naming a relic already fixes its branch.
- Hence for every relic one setting agreed with the truth, one produced a validation error
  (*"is only ever offered as the curse option"*), and Anywhere always matched the truth. A
  reachable dead end, offering no reachable benefit.

So the UI now states the branch instead of asking for it, which also teaches the thing a new
player actually needs to know: that Silken Tress costs you a curse. `OfferSlot` stays in `Core`
and the API still accepts `where=` (the CLI's `--where` and older links keep working) — if a
patch ever gave a relic a dual role, the Oracle's pool-disjointness assertion would fail and
the control would become meaningful again.

The same disjointness is what lets `SeedSearcher` settle a curse relic on **one** RNG draw
instead of generating the whole offer, worth ~6× on a 4-player Silken Tress search. That fast
path used to be gated on the user having chosen "Curse branch", so the default setting silently
paid full price; it is now keyed on the relic's pool.

### Ancient requirements
A repeatable block: `[Ancient ▾]` with a remove button, then a full-width relic field below it,
then the per-row "who must get it" once a relic is set.

- The relic field opens the picker on that Ancient's own pool, and clears back to "Any relic",
  which means *this Ancient shows up, don't care what it has*.
- The field gets its own line rather than sharing one with the Ancient dropdown: a 380px
  sidebar cannot fit a 28px icon and a full relic name beside another control without one of
  them truncating.
- Each row shows which act that Ancient can open. Darv is marked as appearing in either.
- **Each Ancient carries its own icon**, in the dropdown and in the act header of every result.
  The art is the game's map-node badge (`images/packed/map/ancients/ancient_node_*`), which is
  already icon-shaped, unlike the full-body portraits under `images/ancients/`. Served by
  `/api/asset/ancient/{slug}`, and the slugs arrive as their own catalog list rather than a flag
  on each Ancient, because the list has to cover **Neow** — which opens Act 1 and heads that
  result box, but has no searchable offer and so is not in the Ancients list at all.
- **Vakuu is flagged as fully seed-determined**; the rest carry a small note that their pool
  depends on your deck, linking to the explanation below.

### Card rewards
**Fixed rows, one per player** — not a repeatable list, unlike every other criteria panel. Each
player gets one reward per fight, so the row carries a fight selector rather than an add button.
The shape is the point: it makes the per-player streams visible, which is what lets a co-op search
ask two players for two different cards at once.

- Each row is `P{n} is offered [card field] [fight]`, opening the picker on **that player's
  character's pool**, grouped Common / Uncommon / Rare. Two players on different characters see
  different lists.
- **The fight selector is 1 or 2**, appearing only once a card is chosen — same rule as the shop
  visit selector and the chest tolerance. Fight 1 needs no assumption (the map forces it); fight 2
  assumes a second consecutive monster room, which the hint states.
- **Rares are listed but disabled while the row is on fight 1**, which can never roll one (the
  rare odds carry a penalty that has not worn off by the third draw). They used to be omitted
  outright, which was right when fight 1 was the only option and became wrong the moment fight 2
  existed — hiding them would make a reachable search unexpressible. Greyed-out says "not here";
  absent just looks like a missing card. Dropping a row back to fight 1 clears a rare it was
  holding. The API rejects one too, with the reason, in case a URL is hand-written.
- A row whose player has no character yet is disabled and reads **"Pick a character first"**
  rather than sitting there refusing to open. This panel comes alive a row at a time, unlike
  the act panels, which need every player's character before run generation can run at all.
- Changing a character clears a pick that its pool no longer contains.
- The hint names the five Neow relics that shift the result (Arcane Scroll, Hefty Tablet,
  Massive Scroll, Scroll Boxes, Neow's Bones) — they draw off the same stream first. Everything
  else, Silken Tress included, leaves it alone.

In results this appears **inside Act 1, after Neow**, as an indented sub-block: it happens in
Act 1, and the Neow pick above it is the thing that can move it. Three card pills per player
with art, the requested card highlighted, plus a `+ potion` marker when the potion roll lands —
shown because it is the same roll, and a potion costs two extra draws that change which cards
come out.

### Shop relic (third slot)
One row per player: a relic field, plus a "which shop" select that appears once a relic is
chosen. Per player, because each has their own relic bag and their own merchant.

The picker is grouped into "Shared pool" and "<Character> only": five of the thirty belong to a
specific character's pool and can only ever turn up in that character's shop. Each row filters
to the party's own, so an unreachable request cannot be built by clicking.

The panel states two things plainly, because both are easy to assume wrongly:
- Counting is by shops **entered**, not by floor. Walking past one shifts the rest along.
- The other two relic slots are not predictable, and no seed decides them.

Results render the sequence as a collapsed block placed outside the acts. A player's shop order
runs across the whole run, and which act a given visit lands in depends on the route they take,
so filing it under an act would be a lie. The entry matching the criterion is highlighted, the
same way card and event pills are.

### Syncing from the player's save
A button in the Lobby panel, plus a line in the header on load.

The header line is the important half, because it appears without being asked for. Searches are
generated against the local profile's real unlock state, and a user who does not know that has
no reason to trust the numbers. It reads `save: all unlocked`, `save: 41/57 epochs`, or
`no save found, assuming all unlocked`.

The button adds what a line cannot: which profile was found, and, when a co-op run is in
progress, copying its player count, characters and ascension into the form. That last part is
worth automating specifically because lobby ORDER is load-bearing and retyping is where it gets
lost. Player count is applied first, since changing it rebuilds every per-player control.

When no save is found the hint lists the paths tried and names the override variable, rather
than saying no and leaving the user to guess what to do about it.

### Treasure chest
**A repeatable list, with no player column** — the only relic panel that has none. A chest rolls
one relic per player and the whole party votes on the set, so what is on the table is a property
of the seed and who ends up with it is not. Giving it a `P{n}` column would promise something the
game does not decide.

- Each row is `[act] [relic field] [allow taken earlier]`, the picker grouped Common / Uncommon /
  Rare — which is what the seed actually rolls, and what sets how likely the search is to land.
- Naming two relics for one act asks for **both in that chest**, up to the player count. Over that
  the hint turns into a warning naming the act, rather than letting the search return nothing.
  Same principle as the boss panel refusing a pair the act cannot produce.
- **"Allow taken earlier"** is the tolerance, hidden until a relic is chosen. The rarity is exact;
  the relic is the front of the shared bag, and elite rewards, merchant stock and relic events all
  remove entries from it. Raising it accepts the next relics of that rarity instead.
- Results show the chest inside its act, collapsed, labelled with the co-op floor (9 / 24 / 38),
  with each slot's rarity initial as the ordinal badge and the fallbacks folded underneath.
- Act 1's chest renders in the Act 1 box, which is built separately from Acts 2 and 3 because its
  Ancient is Neow and has its own panel. Worth knowing: attaching the chest only to the Acts 2/3
  loop silently drops Act 1's, which is exactly the bug that shipped first.

### Bosses
A repeatable block: `[Act ▾]` with a remove button, then
`[Contains boss | Doesn't contain boss ▾]`, then the act's boss list. No slot rule: the boss is
a run-level draw, so everybody in the lobby fights the same one.

"Contains" rather than "ends with" because the test is against the act's boss *set*, which is
two entries on the final act at Ascension 10 — "ends with" implies a single one.

Repeatable rather than one fixed dropdown per act, because Ascension 10 gives the final act two
bosses — "exactly this pair" has to be expressible, and that is two rows.

Act 1's boss list is **grouped by map** with `<optgroup>`, because its two maps have disjoint
boss lists — choosing one therefore pins the map, and grouping is what makes that legible
without a sentence. Acts 2 and 3 have a single map each, so naming it would say nothing, and
they get a flat list.

Impossible combinations are prevented at the control rather than reported after a search:

- A boss another row of the same act already names is **removed from this row's list**, so the
  same boss cannot be picked twice.
- **"Doesn't contain boss" is disabled** in a row when no boss it could still choose would
  leave the act satisfiable. On the final act at Ascension 10 that means one exclusion, since
  ruling out two of three leaves fewer than the two it draws. At Ascension 0 the same act draws
  one of three, so two exclusions are fine and the option stays live — the rule is arithmetic,
  not a fixed limit.
- A warning line under the rows still catches whatever the dropdowns cannot express, and
  `SeedSearcher.ValidateActCriteria` refuses it server-side regardless. `actIsFeasible` in
  `app.js` mirrors that function deliberately: the two must never disagree about what is
  possible, only about when the user finds out.

### Act 1's map narrows both pools
Choosing Overgrowth or Underdocks makes the other map's content unreachable, so the boss and
event lists show only what the chosen map can produce, and the "(Overgrowth)" / "(Underdocks)"
suffixes on events disappear because there is nothing left to disambiguate. Switching maps
clears any selection that just became impossible rather than leaving a dead value in place.

On "Either" both maps are offered, bosses grouped by map. `mapsFor` / `bossesFor` / `eventsFor`
are the only place this filtering happens.

### Ascension
A dropdown in the **Lobby** panel, not beside the bosses: it describes the run being set up,
and the user is copying it off the lobby screen along with player count and characters.

Only A10 changes anything, so the hint says exactly that, and switches to naming Double Boss
once it is selected. Changing it re-renders the bosses, since the number of slots on the final
act just changed and a previously impossible pair may now be fine.

### Events
A repeatable block: `[Act ▾]` with a remove button, the act's event list below it, then a
"within the first *n*" box once an event is chosen (default 3).

- In Act 1's list, events only one map carries are suffixed with that map. Events reachable
  either way — the 18 run-wide shared ones plus Sunken Statue — are not, because naming a map
  for something you get regardless would be a lie.
- The hint has to state the limit plainly: an act shuffles its whole event pool once and hands
  them out from the front, so the seed fixes the **order**, not the schedule. A room takes the
  next event you currently qualify for and have not already seen, and 36 of the 68 event
  classes gate themselves on HP, gold, deck or act. "Within the first *n*" means near the front
  of the queue.
- "Within the first *n*" uses a **custom stepper**, not `<input type=number>`'s own spinner.
  The native one is a browser widget that page CSS cannot reach, so on a dark panel it lands as
  a white box. `color-scheme: dark` on `:root` fixes the widgets we keep (select popups,
  scrollbars); the spinner is removed outright and replaced. Its ceiling is the act's event
  pool size, which shrinks when an Act 1 map is pinned, so an existing value can need clamping.
- Result cards show each act's event order collapsed under a disclosure, numbered by position,
  with anything asked for highlighted. It uses the neutral text colour rather than the
  deck-branch warning colour — this is folded-away context, not a caveat.

### Uncertainty
Whenever an outcome is branch-dependent, the result shows the branches collapsed under a
"depends on your deck" disclosure, each labelled with its condition and how many deck states
lead to it. Never silently pick one. This is a correctness requirement, not a nicety.

### Search
- Long searches need feedback. Results stream in over Server-Sent Events; the button becomes
  a cancel control while running, and a counter shows seeds scanned.
- `start` defaults to a random index, displayed and editable, so a search is reproducible.
- Every criteria change updates the URL query string, so a search is shareable by link.
- Results carry a copy-to-clipboard button on the seed — the one thing every user needs.

### Inspect a seed
A second mode: paste a seed, get the full breakdown with no search. Same result card.

## 8. HTTP API

JSON, all under `/api`. The UI is the only client, but keeping it clean means the CLI and UI
can share nothing but `Core`.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/catalog` | Neow relics, Ancients and their pools, per-act bosses and events, per-character card pools, shop relics, characters, game version |
| `GET` | `/api/profile` | The local profile: unlock state, epochs revealed, and the lobby of any run in progress |
| `GET` | `/api/explain?seed=&players=&characters=` | Full breakdown of one seed, including each player's first-fight reward and shop sequence |
| `GET` | `/api/search?…` | SSE stream of `SeedHit`s; same params as the CLI, plus `card` and `shop` |
| `GET` | `/api/asset/relic/{slug}.png` | Relic icon from the active provider; 404 under `none` |
| `GET` | `/api/asset/card/{slug}.png` | Card portrait, same provider and same rules |
| `GET` | `/api/asset/character/{slug}.png` | Character portrait, same provider and same rules |

`/api/catalog` reports which asset provider is active so the UI can decide whether to render
art or monograms, rather than firing 60 requests that 404.

`card` is `?card=<slot>:<slug>`, slot **1-based** on the wire to match the P1..P4 labels
(`any` also accepted). Repeatable, and the same shape as the CLI's `--card p1:anger`.
Rejections are specific rather than a silent empty result: wrong player's pool, no such card,
a slot the lobby does not have, more than three cards for one player, or a Rare.

Card slugs and relic slugs live in **separate namespaces** — a card and a relic can share one.
Every art and description lookup in `app.js` is keyed by `"kind:slug"` for that reason, and
`kind` doubles as the asset route. Characters were the third kind, and adding them needed only
an endpoint plus one line in the boot indexer, which is the property that design was for.

`characters` in the catalog is `{name, slug, hasArt}` rather than a bare string: `name` is the
enum name every other endpoint speaks, `slug` is what the art route takes.

`shop` is `?shop=<slot>:<slug>[:<visit>]`, both numbers 1-based on the wire. Shop relics reuse
the relic art and text routes and needed no new asset endpoint: they are the same relics,
reached a different way.

**Unlock state is applied server-side to every request**, read from `progress.save` rather than
assumed. This is not a nicety. Locked epochs shrink the relic pools, pool size sets how many
draws each shuffle costs, and every draw after that lands somewhere else, so generating against
the wrong state gives a different run rather than a slightly wrong one. `?unlocks=all` forces
the fully-unlocked assumption, for predicting a lobby that is not yours.

`/api/profile` returns `found: false` with the list of paths tried rather than an error, because
having no save is a normal state (predicting for a friend, a fresh machine) and the app still
works. It falls back to assuming everything is unlocked, and says so on screen.

## 9. Phases

1. **Working UI, no art.** ✅ Criteria model, streaming search, inspect-a-seed, results with
   branch disclosure.
2. **Descriptions.** ✅ The game's own text and numbers, read from the install; our own notes
   cover only what the game does not say (gating).
3. **Local asset provider.** ✅ Godot PCK reader → `.ctex` → BC7 / S3TC / WebP → icon endpoint.
   All 324 relic icons resolve. Cached in memory, warmed at startup.
4. **Card rewards.** ✅ Per-player picker for the first fight, with portraits and descriptions.
   All 425 offerable cards resolve on both. Same provider, one extra endpoint.
5. **Polish.** Deferred: mobile layout, full keyboard-nav audit, contrast check.

### What phase 3 actually required

The PCK is Godot **pack format 3**, which differs from the widely documented format 2 in two
ways that silently produce garbage if missed: the file table sits at an offset stored in the
header rather than straight after it, and entry offsets are relative to `filesBase`.

Textures are then one of three encodings, and the game ships a different variant per GPU
family, so matching only one suffix loses relics:

| Encoding | Count | Handling |
|---|---|---|
| WebP (plain `.ctex`) | 11 | Served straight to the browser, no decode, no quality loss |
| BC7 (`.bptc.ctex`) | 312 | Decoded to RGBA, re-encoded as PNG |
| S3TC/DXT (`.s3tc.ctex`) | 1 | Same, via the DXT1/DXT5 path |

Both block decoders and the PNG encoder are hand-written — the project takes no image
dependency. Decoding is exact, so correctness was verified by decoding icons and looking at
them; a wrong table scrambles the image rather than drifting subtly.

## 10. Notes and remaining gaps

- **Relic descriptions come from the game, in full.** ✅ Text from `localization/eng/relics.json`
  in the PCK, numbers from each relic's `CanonicalVars` in `sts2.dll`, both read from the
  player's own install at runtime under the §4 boundary. The templates' markup is passed
  through and turned into spans client-side, never `innerHTML`. Two relics keep a placeholder
  where an enchantment's localized name belongs, because that name only resolves inside Godot.
- **Modal tooltips need a top-layer host.** A `<dialog>` opened with `showModal()` renders in
  the browser's top layer, above every normal stacking context, so a `body`-level tooltip can
  never paint over it regardless of `z-index`. The tooltip is reparented into the open dialog.
- **Icons are decoded at startup**, not on demand — sixty concurrent BC7 decodes on first
  paint left the picker grid visibly empty.
- **Shop stock is not a feature that was skipped, it is one that cannot exist.** A shop rolls
  its inventory when you enter the room, off streams your play has already advanced: a card
  rarity pity counter every reward has moved, the relic bag elites and chests have drained,
  and the rewards RNG shared with both. If a user asks for it, explain rather than defer.
- **The first fight's card reward is the exception that proves that rule**, and it is
  implemented. Same late generation, but on floor 1 none of the state above exists yet: row 1
  is a forced Monster room, the pity counters are untouched, and the only thing that can have
  moved the stream is the player's own Neow pick. Ask for the second fight and the answer is
  the shop answer.
- **Card art needed a different discovery path from relic art.** Relics are a flat
  `images/relics/<slug>.png`, so basename matching works. Cards live under
  `card_portraits/<pool>/`, some only under `<pool>/beta/`, and a card can share a slug with a
  relic. Each card's `.png.import` is read for the exact texture it names instead. Skipping the
  `beta/` subfolder silently loses 14 cards that have no final portrait.
- **A slug is not unique across kinds.** Card and relic slugs collide, so every art and
  description lookup is keyed `"kind:slug"`. Worth remembering before adding a third kind.
- **Card rewards are oracle-checked, not `--verify`-checked.** The reward is rolled on room
  entry rather than during run generation, so it never reaches a save file. Two oracle checks
  stand in: every card's rarity against the class the game ships, and the draw sequence
  replayed through the game's own `Rng`.
