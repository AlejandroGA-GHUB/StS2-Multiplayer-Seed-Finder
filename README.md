# Slay the Spire 2 — Co-op Seed Finder

Find **co-op** seeds by what each player gets. An example being: "A seed where P1 and P2 are both offered Silken
Tress, and Vakuu shows up in Act 3 with Fiddle for both players" is a search you can actually run.

Multiplayer only seed finder as very good singleplayer seed finders already exist. This one exists because in co-op
**every player rolls their own offers off their own RNG stream**, and nothing else searches
that space.


> Huge shoutout to the creator of searchthespire, as their single player seed finder served as a strong reference for a clean UI style + supported functionality.
> Check out their website here for single player seeds -> ([SearchTheSpire](https://searchthespire.app/)) 


> Updated At **Slay the Spire 2 v0.110.1**. Predictions are version-specific — see
> [After a game patch](#after-a-game-patch).

> Can reach out via aga.personal.dev@gmail.com with any queries regarding the app.


---

## Quick start

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
```

### Platforms

I've only run/tesed this on Windows as of now, so that's what I can currently vouch for at the moment.

I didn't build it Windows-only though. Everything targets plain `net10.0` with no Windows-only
dependencies, and the code that goes looking for your files already knows about the other
platforms: your Steam library including Flatpak, a native Linux or macOS build of the game, and
saves under `~/.local/share/SlayTheSpire2`, `~/Library/Application Support/SlayTheSpire2` or a
Proton prefix. So the `dotnet run` commands below should just work on Linux and macOS.

Two things you won't get there:

- **No launchers.** The `.bat` files are Windows only. Use the `dotnet run` commands, or wrap
  them in a two-line shell script.
- **Art and descriptions might not load.** They're read out of the game's own packed files, and
  I've never run that lookup on a case-sensitive filesystem. If it misses you'll get lettered
  tiles and no hover text. Searching won't care either way, it never touches your install.

### Web UI

On Windows, double-click **`seed-finder.bat`**. It builds, starts the server, and opens your browser
once the port is actually listening. Closing that window shuts the server down.

```
seed-finder.bat            build, serve on 5173, open a browser
seed-finder.bat 8080       serve on a different port
seed-finder.bat /nobuild   skip the build for a faster restart

repair.bat         check and fix after a game patch (see below)
cli.bat            open a command line here, ready to use
```

Three files to double-click, which serve as the whole interface:

| | |
|---|---|
| `seed-finder.bat` | The seed finder itself, in your browser |
| `repair.bat` | Check and fix after a game update |
| `cli.bat` | A command line, already pointed at the right place |

Inside `cli.bat` every command is `sts2seed <flags>`:

```
sts2seed --help              everything it can do
sts2seed --list              relics, cards, bosses and events you can search for
sts2seed --explain <SEED>    break down a single seed
sts2seed --relic silken_tress --players 2 --require all
```

(If you already keep a terminal in this folder, `sts2seed.bat` makes the same commands work
there. In PowerShell that one needs a `.\` prefix; `cli.bat` avoids the question by opening
cmd.)

Or start it yourself:

```
dotnet run -c Release --project src\Sts2.SeedFinder.Web
```

Either way, open <http://localhost:5173>. Pick your lobby, choose what you want, hit Search.

To use a different port:

```
dotnet run -c Release --project src\Sts2.SeedFinder.Web -- --urls http://localhost:8080
```

### Command line

```
# Both players offered Silken Tress
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --relic silken_tress --require all

# ...and Act 1 is the Underdocks
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --relic silken_tress --require all \
    --act1 underdocks

# Act 3 ends on the Queen, and Zen Weaver is near the front of Act 2's event order
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --boss 3:queen --event 2:zen_weaver:4 \
    --characters ironclad,silent

# Anything but the Queen
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --boss 3:!queen --characters ironclad,silent

# P1 opens with Anger, P2 with Deflect
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --card p1:anger --card p2:deflect \
    --characters ironclad,silent

# P1's SECOND fight offers Offering, which is a Rare, so it has to be fight 2
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --card p1:offering:2 \
    --characters ironclad,silent

# P1's first shop stocks a Belt Buckle, P2's second stocks an Orrery
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --shop p1:belt_buckle --shop p2:orrery:2 \
    --characters ironclad,silent

# All three cards for P1, in whatever order the fights hand them over
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --any-order \
    --card p1:anger --card p1:ashen_strike --card p1:battle_trance --characters ironclad,silent

# Act 1's chest holds both Vajra and War Paint
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --chest 1:vajra --chest 1:war_paint \
    --characters ironclad,silent

# Ascension 10: exactly this pair of final bosses
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --ascension 10 \
    --boss 3:queen --boss 3:aeonglass --characters ironclad,silent

# Vakuu offering Fiddle, plus Silken Tress from Neow
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --relic silken_tress --require all \
    --ancient-relic vakuu:fiddle --characters ironclad,silent

# What does this seed give us?
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --explain 1EMQY13NZN03 \
    --players 2 --characters ironclad,silent

dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --help
```

---

## Searching on your GPU

If you have one, the tool uses it. Nothing to install, nothing to switch on, and no different
answer if you don't — a search finds exactly the same seeds either way.

The status line tells you which one ran and how fast it is going, so you never have to guess:

```
67,108,864 of 400,000,000 scanned · 501.0 M seeds/s · GPU: NVIDIA GeForce RTX 4070 SUPER · 3 found…
```

**Why it's safe.** The GPU is never allowed to decide anything. All it does is throw out seeds
that cannot possibly match, and every seed that survives is then checked by exactly the same code
that runs when there's no GPU at all. So the worst a bug in it could do is waste time, not invent
a wrong answer. It's also held to that standard directly: `sts2seed --gpu-verify` runs both paths
over a range of seeds and compares the results as **sets**, which is what catches a filter
throwing away seeds it should have kept.

**Roughly what to expect**, measured on an RTX 4070 SUPER against the same searches without it:

| Search | Without a GPU | With one |
|---|---|---|
| Neow relic | 45 M/s | ~1 B/s |
| Shop relic | 0.9 M/s | 280 M/s |
| Boss, event or Ancient | 2 M/s | 37 to 430 M/s |
| Treasure chest | 0.5 M/s | not accelerated yet |

An integrated laptop GPU lands well below a discrete card but still far above the CPU. If there's
no usable device at all, or you set `STS2_GPU=off`, everything works as it always did.

Two things that surprise people:

- **More requirements usually means a *higher* seeds/sec.** Cheap checks run first, so a seed
  that fails Neow is discarded after a couple of numbers and never reaches the expensive work.
  It does not mean you find seeds sooner — the same requirements that make each seed cheap to
  reject are what make matches rare.
- **A treasure chest requirement slows the whole search down**, because it's the one thing still
  checked entirely on the CPU. It's barely noticeable next to a demanding search, since few seeds
  get that far, but paired with something loose it becomes the limit.

---

## What it predicts

| | Confidence |
|---|---|
| Neow's full offer per player — curse relic and both positives | Confirmed in play |
| Which Act 1 map you get — Overgrowth or Underdocks | Confirmed in play |
| Act order, bosses, and which Ancient opens each act | Confirmed in play |
| The Ascension 10 second boss | Verified against the game's own RNG |
| What each Ancient offers (Acts 2–3) | Verified against the game's own RNG |
| The order each act hands out its events | Verified against a real run |
| The card reward from fights 1 and 2, per player | Confirmed in play |
| The card reward from fight 3 | Same draw chain, not yet seen in a played run |
| Every shop's third relic, per player, in order | Confirmed against real co-op runs |
| Each act's treasure chest | Confirmed against a real co-op run |
| Card rewards from fight 4 on | Not supported yet |
| A shop's other two relics, cards or potions | Not supported yet |
| Relics from elite fights | Not supported yet |
| Card payloads (Hefty Tablet's rares, Arcane Scroll's rare) | Not supported yet |

Five independent checks back this up:

- A **differential oracle** loads the real `sts2.dll` and replays every draw through the game's
  own RNG, asserting our port matches.
- **`--verify`** diffs our whole act-generation chain against a save file the game itself
  wrote — every event, encounter, boss and Ancient, in draw order.
- **`--verify-history`** does the same against every run you have already finished, which is
  what covers co-op: the game will not let you start a multiplayer lobby alone, but runs you
  played with someone else are still on disk.
- **`--gpu-verify`** runs the GPU search and the CPU one over the same seeds and compares the
  results as sets, so an accelerated search cannot quietly answer a different question.
- Relics, bosses and the Act 1 map have been **confirmed across several played co-op runs**.

Card rewards are the one thing `--verify` can't see, because the reward is rolled when you walk
into the room rather than when the run is generated, so it never lands in the save file. The
oracle covers them instead, two ways: every card's rarity against the class the game ships, and
the whole draw sequence replayed through the game's own RNG. A co-op run has since confirmed all
four rewards outright — two players, two fights, all matching.

### What you can search on

Neow's relic, the Act 1 map, each act's **boss**, each act's **events**, which **Ancient** opens
Acts 2 and 3 (optionally offering a particular relic), the **card reward from fights 1 to 3**,
the **third relic in a shop**, and each act's **treasure chest**. Combine as many as you like in
one search.

Neow, the Ancients, card rewards and shop relics are rolled per player, so those are per-slot.
The boss, the event order and the chest are the same for everybody in the lobby — the chest
because it is a shared pick: the seed decides what is on the table, and the party decides who
takes it.

Boss requirements can be inverted: **does not end with** finds every seed that keeps a boss
you would rather skip out of the run.

A shop means the **third relic slot** and nothing else. That one is fixed before the run starts;
the other two are rolled from run state when you open the door, so no seed pins them. It is
counted by shops you actually walk into rather than by floor, so skipping one moves the rest up.

### Card rewards, fights 1 to 3

The first room of a run is always a normal fight — row 1 of the map is forced to it and can't be
anything else — and every player rolls their own reward for it off their own stream. So each
player can ask for a specific card, and "P1 opens with Anger while P2 opens with Deflect" is one
search.

Fights 2 and 3 work the same way, and 3 is as far as this goes. In the picker you click up to
three cards and each one is badged with the fight it stands for: first click is fight 1, second
is fight 2, third is fight 3. On the command line, add `:2` or `:3`.

**Exact order or any order.** Pick more than one card for a player and a dropdown appears. On
*Exact order* each card has to come from the fight its badge names. On *Any order* you still get
all of them, one per fight, but which fight produces which is free. That costs nothing to ask for
and it is dramatically more likely: about **5.6x per player**, so a three-player search for a
Common and two Uncommons each goes from roughly 1 in 1.8 trillion to about 1 in 10 billion,
which is seconds of searching rather than hours. Use Exact order only when the order genuinely
matters to you. On the command line it is `--any-order`.

Things to know:

- **Fight 1 needs no assumption. Fights 2 and 3 do.** They assume you walk straight into the
  next monster room each time, with no shop, elite, event or rest in between, so all three have
  to be consecutive. Take a detour and the prediction is for a fight you never had, and the
  further along it is the easier that is to do by accident.
- **A Rare never appears in fight 1**, but can after it. The rare odds start with a penalty
  that only wears off across rewards, so rares are held out of the first fight entirely and the
  picker refuses them there.
- **Ascension matters from fight 2 on.** At A7 and up, Scarcity roughly halves the rare odds, so
  set your ascension if you're searching for one.
- **Nothing is upgraded in Act 1.** Upgrade odds scale with the act.
- **Five Neow options change the answer.** Arcane Scroll, Hefty Tablet, Massive Scroll, Scroll
  Boxes and Neow's Bones all draw cards from the same stream first, which shifts what the fights
  hand you. Every other Neow pick, Silken Tress included, leaves it alone. The prediction
  assumes you took one of those.

Past fight 3 it stops, because each further room makes the unbroken-hallway assumption less
likely than it is worth — see [What the seed cannot decide](#what-the-seed-cannot-decide).

### Treasure chests

Every act has exactly one treasure chest, at co-op floors 9, 24 and 38, and no route skips it.
That makes it the one mid-run reward the seed pins down: it puts **one relic per player** on the
table, the party votes on who takes it, and none of that depends on how you got there.

So a chest requirement is run-level. It asks what's *in* the chest, not who ends up with it, and
naming two relics for one act asks for both of them to be there, up to your player count.

Two things to know:

- **The rarity is exact, the relic can drift.** What fills a slot is the front of the shared
  relic bag, and every relic anyone picks up earlier in the run removes an entry from it. If the
  relic you asked for got taken already, you get the next one of that rarity instead — so the
  picker lets you accept the next few, the same way event ordering does.
- **A `?` room that turns into a treasure room shifts everything.** It drains a full round of
  picks, which moves every later chest along by one. If that happened, pass `--extra-chests 1`
  (or set it in the UI) and the predictions line back up.

### Ascension

Two ascension levels change what a seed gives you, and nothing in between them does.

**A7 (Scarcity)** roughly halves the odds of a Rare card reward. Fight 1 can't offer a Rare
anyway, so this only shows up from fight 2 on, and only if you're searching for a Rare.

**A10 (Double Boss)** gives the **final act a second boss**, drawn from that act's other two.
It's the last decision generation makes, so everything else about the run is identical whether
it's on or off. With it set, the final act takes two boss requirements instead of one:

- Two "ends with" rows pin the exact pair.
- One "does not end with" row keeps a boss out of both slots.

Set your real ascension and you don't have to think about either of these.

### Bosses and events

Two things worth knowing before you search on them:

- Act 1's two maps have **completely separate bosses**, so choosing an Act 1 boss also decides
  the map. Asking for a boss from one map and an event from the other has no answers, and the
  tool says so rather than scanning.
- An act shuffles its **whole** event pool once and hands them out from the front, so what the
  seed fixes is the order, not what you will actually see. A room takes the next event you
  currently qualify for and have not already met — and half the events gate themselves on HP,
  gold, deck or act. So "within the first 3" means near the front of the queue, not a promise.

---

## Inputs that change the answer

Get one of these wrong and the prediction is for a different run. The tool cannot infer them.

- **Lobby order.** P1 is whoever joins first. Swap join order and the offers swap with it.
- **Player count.** Changes act generation, so it changes bosses and Ancients.
  Party *composition* does not — all five characters have identically sized relic pools.
- **Ascension**, at two levels only: A10 for the final act's second boss, and A7 for the odds of
  a Rare card reward. Nothing else in a run changes with ascension.
- **No run modifiers.** Predictions assume an ordinary run. Ticking anything on the Custom Run
  screen changes how the game generates a run, so none of this holds for one. Typing a seed in
  doesn't make a run custom, only the modifiers do.
- **Mods that add content.** Anything adding relics, cards, events or encounters changes pool
  sizes and invalidates Act 2/3 predictions.

  **Cosmetic mods are completely fine.** Art replacements and reskins touch nothing a seed
  decides, and I've verified that myself by playing full runs with the Chaos Zero Nightmare
  mods (which you should go check out by the way, they're very good). Everything still matched.

  The tool warns whenever a mods folder is present at all, because it can't tell the two kinds
  apart from the outside. Read that banner as "check what you have installed", not as a verdict.

## What the seed cannot decide

Only **Vakuu**'s offer is fully determined by the seed. Every other Ancient gates part of its
pool on your deck at the moment you meet it — Tanx on Instinct-enchantable cards, Nonupeipe on
Swift, Tezcatara on whether a basic Strike survives, Pael on three separate conditions.

The tool shows **every branch with the condition that produces it**, rather than picking one
and presenting it as fact.


---

## Art and descriptions

The web UI shows real relic icons, card portraits, character portraits, Ancient icons, event
illustrations, and the game's own description of what each one does, all read from **your own
installed copy of the game** at runtime. Nothing is bundled and nothing is redistributed — no art
or text ships in this repository or in a release.

For events you get the game's own title and **the choices it offers you**, with what each one
does and what it costs: "Pay 250 Gold. Remove 2 cards from your Deck." Where those choices lead is
deliberately left out, since that is the part worth meeting cold.

Descriptions appear on hover, anywhere a relic or card can be chosen or shown. They read
exactly as they do in game, numbers and all: the text files hold only templates ("draw {Cards}
additional cards"), so the values are read from the game's own assembly alongside them.

Names of things are resolved too, so a relic reads "Enchant all Defends with Goopy" and an event
choice reads "Enchant a card with Sown" or "Procure Glowwater Potion", rather than leaving you to
guess. Those come out of the game's code rather than its text, because the text only says which
*kind* of thing is meant. Every relic description resolves in full.

Some event values still show an **X**, and it stands for "the game decides this later" rather than
for a gap. Most are quantities worked out mid-run, like the gold from Dense Vegetation or the
healing from Sapphire Seed. A few are choices that can hand out either of two cards, where nothing
outside a live run says which. Where a value would otherwise print as **0**, X is deliberate: "Gain
0 Gold" reads as a promise of nothing, which is worse than admitting we cannot say.

One caveat on event choices: they are the choices the game's text files **declare**, which can
include one the game no longer offers. Spiraling Whirlpool lists a third option that does not
appear in play. Treat the list as what an event is about, not as a promise of exactly what you will
be asked.

Your Steam library is located automatically, including libraries on other drives. To point it
somewhere explicitly:

```
set Assets__GameDirectory=D:\SteamLibrary\steamapps\common\Slay the Spire 2
```

Without the game installed, the app works exactly the same, drawing lettered tiles instead and
skipping the descriptions.

---

## Your save file

The tool reads your profile as well as your install which has actual functionality changes.

Locked content is removed from the relic pools, smaller pools mean fewer shuffles, and every
draw after a shuffle lands somewhere else. **An account that has not unlocked everything gets a
different run from the same seed.** So predictions are generated against what your
`progress.save` actually reports, not against an assumption that everything is revealed. The
header line tells you which it used; **Sync from my save** in the Lobby panel shows the profile
it found, and copies the lobby of a co-op run you have in progress.

If no save is found the tool falls back to assuming everything is unlocked and says so, rather
than failing.

Saves are found automatically on Windows, macOS, Linux and under Proton. Unlike installs, Steam
does not let you relocate them, so this is far less fragile than finding the game itself — but
if you have moved your `AppData`, or you keep profiles somewhere unusual:

```
set STS2_SAVE_DIR=D:\somewhere\SlayTheSpire2
```

Point it at the folder that contains `steam` (the one with `<your-id>/profile1/saves` under it).
An explicit setting is taken as final: if it is wrong, the tool reports that it found nothing
rather than quietly falling back and using a profile you did not mean.

One thing it cannot know: in co-op, **each player's relic bag is filtered by their own unlocks**,
and a partner's profile is on their machine. Searches assume the lobby matches yours. If you
play with someone newer to the game, predictions past the relic bags may not match.

---

## After a game patch

Predictions are computed for one build of the game. When it updates, some may stop being true,
and **the failure is quiet** — art and descriptions keep loading correctly from your install, so
a stale copy looks perfectly healthy.

So the app tells you. If your game's logic differs from the build this checkout was verified
against, a banner appears above the results.

**This is not tied to one Steam branch.** The main release branch and the beta branch are both
supported: nothing here assumes which one you are on, it only cares whether the build in front of
it matches the build the data tables were read from. Whichever branch you play, if yours is ahead
of the recorded one, `repair.bat` reads the tables back out of your own install and brings this
copy into line with it. Switching branches is the same situation as a patch, and has the same
one-step fix.

**To fix it, double-click `repair.bat`.** It checks by layer, offers to regenerate what can be
regenerated, rebuilds, verifies against runs you have already played, and records the result.
About two minutes, no terminal.

```
  Your game       v0.121.0
  Verified against v0.109.1

  primitives (RNG, hashing)      OK
  data tables (pools, acts)      FAIL     stale: RelicPoolData, ActData
  draw order (hand-written)      OK       23 mirrored methods unchanged

  Still correct regardless: Neow's offer and the Act 1 map.

  Fixable by command:  yes
  Needs a code edit:   no
```

Most patches are fixed by that one double-click, and the few that aren't will tell you which
file needs an edit. **[docs/PATCH_RECOVERY.md](docs/PATCH_RECOVERY.md)** is the full runbook,
written for someone who hasn't read the source.

If you would rather type, `sts2seed.bat` makes every command the tool prints work as written:

```
sts2seed --doctor           what broke, and what to do about it
sts2seed --refresh          rewrite the data tables from your game
sts2seed --show <method>    read a game method beside the file that mirrors it
sts2seed --verify-history   check against runs you have already played
sts2seed --gpu-verify       check the GPU search against the CPU one (needs no GPU)

dotnet run -c Release --project src\Sts2.SeedFinder.Oracle    the exhaustive port check
```

`--verify` reads a run in progress, so start one and quit to the menu first. `--verify-history`
needs no live run: it reads runs you have already finished, which is the only way to test the
co-op path, since the game will not start a multiplayer lobby with one player in it.

Expect `--verify-history` to skip runs from older game builds, and to report some co-op runs
under "Lobby". A finished run does not record which epochs each player had revealed at the time,
and that changes generation, so the tool fits a state rather than pretending to know one. Runs
on the build you are on now should match with no fitting.

One caveat worth knowing: a passing `--doctor` is necessary and not sufficient. Its checks walk
*our* lists, so they are blind to content being **added** — the likeliest thing a patch does.
Only a real run settles it, which is why `repair.bat` insists on one before recording anything.

---

# AI File References For Development

If using AI to work on a fork, CLAUDE.md, game_mechanics.md, web_app_specs.md, are the 3 main files to use for context on the app.

If you're using any LLM other than Anthropics models (Haiku/Sonnet/Opus/Fable), you will need to rename the CLAUDE.md file to AGENTS.md or whichever file name
the LLM is meant to utilize.

## Layout

| | |
|---|---|
| `src/Sts2.SeedFinder.Core` | RNG, hashing, seed codec, Neow, acts, Ancients, search |
| `src/Sts2.SeedFinder.Cli` | `sts2seed` command line |
| `src/Sts2.SeedFinder.Web` | Local web UI — minimal API plus static HTML/CSS/JS, no npm |
| `src/Sts2.SeedFinder.Gpu` | Optional GPU search kernels (ILGPU). Nothing else depends on it |
| `src/Sts2.SeedFinder.Oracle` | Differential test against the real `sts2.dll` |
| `seed-finder.bat` | Builds, serves, and opens the web UI in your browser |
| `repair.bat` | Checks and fixes after a game update |
| `cli.bat` | Opens a command line here with `sts2seed` ready to use |
| `sts2seed.bat` | Lets you type `sts2seed <flags>` in a terminal you already have open |
| `baselines/verified-build.json` | The game build this checkout agrees with. `--accept` writes it |
| `baselines/method-snapshots.json` | Recorded shape of every game method we mirror, for detecting drift |
| `docs/PATCH_RECOVERY.md` | What to do when the game updates |

## Building on this

Reading the game's code to understand its behaviour is ordinary modding practice, Mega Crit
ship Harmony inside the game. Redistributing their work is the line this project does not
cross:

- No decompiled source, and no game assets, in this repository. Ever.
- Behaviour is reimplemented from understanding rather than transcribed. Where output must be
  bit-identical (RNG, hashing) the port is kept small, isolated and clearly marked.
- `MegaRandom` is xoshiro256\*\* — public domain (Blackman & Vigna), by way of Redzen (MIT,
  © Colin D. Green). That MIT notice stays in the file.

Not affiliated with or endorsed by Mega Crit. Slay the Spire 2 and its assets are theirs.

## License

MIT — see [LICENSE](LICENSE).
