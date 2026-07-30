# Sts2 Multiplayer Seed Finder

A seed finder for **Slay the Spire 2 co-op runs**. Shipped. `docs/plan.md` holds what is still
open (nothing starts without the user's go-ahead), what was built, and the reasoning behind it.
**Nothing is queued.** Elite relics were investigated and declined by the user — they need the
route as an input, which the map graph would otherwise have to supply; see "Elite relics:
feasible, declined" in `docs/plan.md` before re-deriving any of it.

## Scope — read this first

**Multiplayer only.** Every search runs with `isMultiplayer = true` and `playerCount >= 2`. The product is
*joint, per-player criteria*: "find a seed where P1 gets X and P2 gets Y."

**Do not build a singleplayer mode.** Good SP finders already exist (SearchTheSpire, SearchTheSeed, sts.gg)
and the user has explicitly ruled SP out. No SP code path ships.

Careful with the boundary: run-level generation (acts, bosses, map, encounters, events) is **not** SP work —
co-op runs need all of it, just with the MP flag, shorter acts, and MP-adjusted pools. Only SP *packaging*
is out of scope.

## Status

**Acts 1–3 complete, and confirmed in play.** Neow's *full* offer — the curse-branch relic
and both positive options — per player slot. Acts 2/3 generation is verified against a real
singleplayer run save; all 7 Ancients' relic offers are oracle-verified. The user has since
played multiple co-op runs whose relics, bosses and Act 1 map matched the prediction exactly
(2026-07-26).

Multiplayer generation is now verified end to end against a real co-op run (2026-07-28,
seed `8NZJ8J63RAKH`, v0.109.1, A10): acts, every boss including the A10 second, Ancients,
encounter order and shop relics all matched. Past co-op runs live in `saves/history/`, which is
why no partner was needed. See "What a singleplayer `--verify` run does and does not prove".

That same run also confirms the two things added 2026-07-29, both against the game rather than
against our own reading of it. **Card rewards**: floors 2 and 3 were both Monster rooms, and all
four rewards (2 players x 2 fights) matched exactly, once P1's Hefty Tablet cost was applied —
which closes the last open verification item on the card chain. **Chests**: all three acts matched
once the floor-6 `?` room that became a treasure room was counted, and every remaining difference
is a relic the run records someone taking out of the shared bag first.

Searchable criteria, all combinable in one search: **Neow relic**, **Act 1 map**, **boss**
(per act, and negatable), **event** (per act, "within the first n"), **Ancient** (optionally
offering a given relic), **card rewards for fights 1 and 2** (per player), the **shop's third
relic slot** (per player, per shop visit), and each act's **treasure chest** (per act).
**Ascension 10** adds the final act's second boss, which two boss criteria can pin as a pair —
and from fight 2 ascension also affects card rarity, via Scarcity at A7+.

Bosses, events and chests are run-level, not per-player, so unlike Neow, the Ancients, card
rewards and shop relics they take no slot rule. A chest is run-level for a specific reason: it is
a SHARED pick, so the seed fixes what is on the table and the party votes on who takes it. All of
them need `--characters`, because they come out of the same run generation. Card and shop
criteria need it too, for a different reason: the pools are the character's.

```
dotnet build -c Release
seed-finder.bat                                                             # web UI: build, serve on 5173, open a browser
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --relic silken_tress --players 2 --require all
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --act1 underdocks --relic silken_tress --require all
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --boss 3:queen --event 2:zen_weaver:4 --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --boss 3:!queen --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --ascension 10 --boss 3:queen --boss 3:aeonglass --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --card p1:anger --card p2:deflect --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --shop p1:belt_buckle --shop p2:orrery:2 --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --chest 1:vajra --chest 1:war_paint --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --card p1:offering:2 --characters ironclad,silent
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --explain 0BUJY7ZRE8TP --players 2
dotnet run -c Release --project src\Sts2.SeedFinder.Oracle              # differential test vs sts2.dll
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --verify     # diff act generation vs the run in progress
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --verify-history --verbose  # ... vs every finished run
```

**Run the Oracle after any change to Core, and after every game patch.** It loads the real
`sts2.dll` and asserts our port still matches. As of v0.109.1 all checks pass.

**Run `--verify` after any change to `Core/Acts/`.** It checks the act/boss/Ancient chain
against a save the game itself wrote — see "Run saves are the Act 2/3 oracle" below.

**Run `--verify-history` too.** It needs no live run and it is the only check that covers the
co-op path, since a solo multiplayer lobby cannot be started. Expect some older runs to be
skipped (different build) or reported under "Lobby": generation depends on state a finished run
does not record, and the verifier fits a partner unlock state rather than pretending to know it.
Runs on the current build should match with no fitting at all.

### Terminology — important
The user asked for "Act 1 boss relic". StS2 has **no Boss relic rarity** and no boss relic
reward. Silken Tress is `RelicRarity.Ancient` and is offered by **Neow**, the Ancient at the
*start* of Act 1, as one of the "curse" options (take a curse, get a relic). v1 targets that.
Silken Tress is also reachable via `EventRelicPool` (random events) — not yet modelled.

"Ancients" are act-opening NPCs (Neow, Darv, Vakuu, Tezcatara, Pael, Tanx, Orobas, Nonupeipe).

## Where the detail lives

`docs/game_mechanics.md` holds what was learned by decompiling the game. It is not loaded
automatically, so **read the relevant section before you touch the matching code** rather than
working from memory. Each of these records something that was got wrong at least once:

| Before changing | Read |
|---|---|
| `Core/MegaRandom.cs`, `Core/Rng.cs`, `Core/GameHash.cs` | *Seeds*, *RNG*, *Slugs*, *The multiplayer key* |
| `Core/Neow/` | *The Neow chain* |
| `Core/Acts/RunGenerator.cs`, `ActData.cs` | *Acts 2/3*, *Run saves are the Act 2/3 oracle*, *Ascension 10*, *Boss discovery order*, *MP-specific differences* |
| `Core/Acts/` shop relics | *Shops: the third relic slot IS predictable* |
| `Core/Acts/ChestRelics.cs` | *Treasure chests* |
| `Core/Cards/` | *Card rewards, fights 1 and 2* |
| `Core/Ancients/` | *Ancients' offers* |
| Search criteria, the web pickers | *Bosses and events as search criteria* |
| `Web/Assets/` | *Localization strings*, *Card art* |
| Interpreting a `--verify` result | *What a singleplayer `--verify` run does and does not prove* |
| Regenerating data tables | *Decompiling* (though `sts2seed --refresh` does this without decompiling) |

Other documents: `docs/plan.md` (open queue, what shipped, and the reasoning behind each feature),
`docs/web_app_specs.md` (the web UI), `docs/PATCH_RECOVERY.md` (what a user does after a patch).

## Game facts (verified by decompiling v0.109.1, built 2026-07-20)

Install: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\`
Game logic: `data_sts2_windows_x86_64\sts2.dll` — 9.6 MB, **.NET 9, unobfuscated**.
Version metadata lives in `release_info.json` (`version`, `commit`, `main_assembly_hash`).

### Provenance rules (read before adding code that mirrors the game)
Reading decompiled code to understand behavior is normal modding practice; MegaCrit ships
`0Harmony.dll` inside the game. Redistributing their code is the line we do not cross.

- Write **behavior**, not transcription. Reimplement from understanding; don't paste.
- Where output must be bit-identical (RNG, hashing), a close port is unavoidable — keep those
  files small, isolated, and clearly commented as ports.
- **Check upstream licenses.** `MegaRandom` is not MegaCrit IP: xoshiro256** is public domain
  (CC0, Blackman & Vigna) and the C# shape comes from Redzen (MIT, © Colin D. Green). The MIT
  notice is reproduced in that file and **must stay there**.
- Game data we encode (relic names, pool ordering) is factual, but keep it to what the feature
  needs rather than bulk-exporting their content.
- No game assets, ever — no art, audio, or localization strings.

### Confounders — must be explicit inputs, never silent defaults
`UnlockState`, Standard vs Custom, player count, game version, and **ascension** — the last
only at A10, and only for the final act's second boss. Player slot order and party composition
are confirmed NOT to affect upfront generation (see above).

`UnlockState` is the one that bites in practice, and the web app now reads it from
`progress.save` rather than assuming everything is revealed (`Core/Saves/`, `/api/profile`).
`?unlocks=all` forces the old assumption, for predicting a lobby that is not yours. Verified
end to end: hiding a single relic epoch changes two of the three act bosses and reorders the
whole shop sequence.

## Architecture

Hybrid, as planned: the hot search path is our own fast C# (`Core`), with a `sts2.dll`-backed
**Oracle** proving the port matches the real game and catching patch drift.

- `src/Sts2.SeedFinder.Core` — `MegaRandom` (xoshiro256**), `Rng`, `GameHash` (XXH64 via
  `System.IO.Hashing`), `SeedCodec`, `SeedSearcher`, `Neow/`, `Acts/`, `Ancients/`, `Cards/`.
- `src/Sts2.SeedFinder.Cli` — `sts2seed` executable. Two jobs in one binary: the scriptable
  front end onto the same `Core` the web UI drives, and the host of `--verify`.
- `src/Sts2.SeedFinder.Web` — local web UI (`seed-finder.bat`, or
  `dotnet run -c Release --project src\Sts2.SeedFinder.Web`, then http://localhost:5173).
  Minimal API + static HTML/CSS/JS, no npm. Relic and card art **and descriptions** are read
  from the player's own game install at runtime and never committed — see `docs/web_app_specs.md` §4
  for the provenance boundary and `Assets/` for the Godot PCK / BC7 / S3TC readers.

  Two things about hosting the static UI that cost real debugging time, both now fixed in code:
  `wwwroot` is **not** copied to the build output by the Web SDK (publish only), and the
  static-web-assets manifest that would otherwise point back at the project folder is loaded
  **only in Development**. So a Production run of the built exe served `/api/*` fine and 404'd
  every page. `Program.cs` pins `ContentRootPath` to `AppContext.BaseDirectory` and the csproj
  copies `wwwroot` on build. Static responses also send `Cache-Control: no-cache`, because
  Chrome otherwise heuristically caches `app.js` and silently keeps running a stale copy while
  the server serves the new one.
- `src/Sts2.SeedFinder.Oracle` — loads the real `sts2.dll` by reflection (no compile-time
  reference), so it fails gracefully when the game isn't installed.

### Finding the player's files, for people who are not this user
Two separate lookups, both needed for an open-source release and both with an override:

- **Install** (art, descriptions, version) — `Core/Install/GameInstall.cs`. Steam lets users put
  games on any drive, so this reads `libraryfolders.vdf` and the registry rather than guessing.
  Override: `Assets:GameDirectory`.
- **Saves** (unlock state, run in progress, history) — `Core/Saves/SaveLocations.cs`. Godot's
  user dir for a project with a custom dir name: `%APPDATA%\SlayTheSpire2` on Windows,
  `~/Library/Application Support/SlayTheSpire2` on macOS, `~/.local/share/SlayTheSpire2` on
  Linux, plus the Proton prefix. Steam does NOT relocate these, so there is no library list to
  read here. Below the user dir everything is walked, not constructed, because the path contains
  the platform name and the account id (`steam/<steamId>/profile<N>/saves`).
  Override: `STS2_SAVE_DIR`, or `Saves:Directory` in config. An explicit override does not fall
  back — a wrong path should fail loudly rather than silently use someone else's profile.

`/api/profile` reports which profile was found, how many epochs are revealed, and the lobby of
any run in progress. When nothing is found it returns the paths it tried, so the UI can name the
fix instead of just saying no. Both the CLI verifiers and the web app go through the same code,
so they cannot disagree about where your saves are.

### The three verification harnesses cover different failure modes
None replaces the others, and knowing which to reach for saves time:

- **Oracle** — proves each *function* matches. Runs our code and the game's compiled code on
  the same inputs and asserts identical output, headless, no run needed. This is the only
  reason the fast path can be trusted: `SeedSearcher` scans millions of seeds through our
  reimplementation and never through the game's code. It is also the patch-drift alarm.
- **`--verify`** — proves the *wiring*. Diffs `RunGenerator` against `current_run.save`, which
  is the game's own written record of every draw `GenerateRooms` made. Needed because the
  assembled chain cannot run headless (it wants a live `ModelDb`/`RunState`), so the Oracle
  structurally cannot reach it. Needs a run in progress.
- **`--verify-history`** — proves the same wiring against what players actually SAW, across
  every finished run on disk. Weaker per run (no rng block, no grab bags, only the rooms
  entered) but far broader, retrospective, and the only one that reaches co-op or shop relics.
  Its failures need reading rather than trusting: build, unlock state at the time, boss
  discovery order and mods are all unrecorded, and the tool says so.

Correct pieces wired in the wrong order pass the Oracle and fail `--verify`. A patch that
changes `NextFloat` fails the Oracle immediately. Card rewards are Oracle-only, by necessity —
see "The first fight's card reward" for why `--verify` cannot reach them.

**Phase 0 spike resolved:** the RNG/hash primitives (`Rng`, `StringHelper`, `MegaRandom`) run
headless with **no Godot runtime and no `ModelDb` init**. Higher-level generation (`ModelDb`,
acts, pools) is untested and may still need Godot — retest when Phase 2 needs it.

**Seed space:** the 2³² figure other tools advertise came from the pre-0.107.1 32-bit hash. The current
hash is **XXH64 (64-bit)**, so the hash space is not enumerable. `SeedCodec` instead enumerates
the **seed-string** space (34¹² ≈ 2.4×10¹⁸) by index, which is deterministic, sortable and
resumable. Fine in practice — per-player odds are ~1/9, so matches are dense.

## Conventions
- C# / .NET 10 (SDK 10.0.101 installed). Chosen so ported code can be diffed line-by-line against
  decompiled source.
- Mirror the game's own names (`RunRngSet`, `PlayerRngSet`, `UpFront`) so cross-referencing stays cheap.
- Version-stamp every result with the game version it was computed for.
- Any behavior marked *wiki-sourced* above is unverified — confirm against decompiled source or a live run
  before relying on it.

## Environment
- Windows. PowerShell 5.1 is the default shell (no `&&`; use `;` / `if ($?)`). Bash tool also available.
- Verification from Phase 2 onward needs a **real co-op partner** (two accounts or a tester).
