# Repairing this after a game patch

Predictions are computed for one build of Slay the Spire 2. When the game updates, some of them
may stop being true — and the failure is quiet, because art and descriptions keep loading
correctly from your install, so a stale copy looks perfectly healthy.

This is the runbook. You do not need to have read the source.

---

## The short version

1. Start the app. **No banner? Nothing to do.**
2. Double-click **`repair.bat`**.
3. Answer `y` when it offers to regenerate.
4. If it says something needs a code edit, see [When it needs a code edit](#when-it-needs-a-code-edit).

That is the whole thing for most patches, and takes about two minutes.

---

## What you need

The [.NET 10 SDK](https://dotnet.microsoft.com/download), which you already needed to run this at
all, and the game installed. Nothing else — no Python, no decompiler, no extra tools.

Keep using the **source checkout**. `seed-finder.bat` compiles on every launch, and that is what makes a
regenerated table take effect without you having to think about it.

---

## Step by step

### 1. Find out whether anything is wrong

`repair.bat` builds, then prints something like:

```
  Your game       v0.121.0
  Verified against v0.109.1  (2026-07-28)

  primitives (RNG, hashing)      OK
  data tables (pools, acts)      FAIL     stale: RelicPoolData, ActData
  draw order (hand-written)      OK       23 mirrored methods unchanged

  Still correct regardless: Neow's offer and the Act 1 map.

  Fixable by command:  yes
  Needs a code edit:   no
```

Read the two verdict lines at the bottom. They tell you whether this is yours to fix.

The "still correct regardless" line is worth trusting: Neow's offer and the Act 1 map run on
their own RNG streams over fixed lists, so a content patch does not move them. Even a badly
drifted copy is still a working Neow finder.

### 2. Regenerate the data tables

Answer `y`. It reads your own game and rewrites the relic, card and act tables, printing what
changed:

```
  Shared relics    118 -> 127   (Common 25->28, Rare 35->38, Shop 25->26)
  Act 2 Hive       elites 6 -> 7
  Ancient pools    unchanged
```

**Sanity-check that the numbers moved a little.** If something reads `118 -> 0`, stop: that is
an extraction failure, not a patch. The tool refuses such a write on its own, but it is worth
knowing what you are looking at.

Then it rebuilds and re-checks. If everything is green, go to step 4.

### 3. When it needs a code edit

Some changes cannot be regenerated, because they are *behaviour* this project re-expressed in
its own code rather than *data* it copied. You will see:

```
  draw order (hand-written)      FAIL     Neow.GenerateInitialOptions
```

That names the game method that changed. To see it beside the file that mirrors it:

```
sts2seed --show Neow.GenerateInitialOptions
```

which prints the game's current version of that method, the path of our file, and one line on
what it decides. The repair is reading both and making ours match the **order things are drawn
in** — not the effects, only the order and count of RNG calls.

`sts2seed --verify` against a real run names the first draw index that disagrees, which usually
narrows it to a line or two.

**If you are not comfortable editing C#, stop here.** Check whether anyone has published a fixed
fork, or open an issue. Meanwhile the parts reported as still correct really are.

There is a third kind of change the tool will tell you about explicitly:

```
  This patch changed something the generator assumes:
    your game has character relic pools this build does not know: Watcher
  That needs a code change, not a regeneration. Nothing was written.
```

A new character, a new act, or a new relic rarity changes shapes this project declares as C#
enums. Nothing is written, on purpose — emitting the old shape would produce tables that look
fine and are wrong.

#### If you improve a generated file, change the generator, not the file

This one only bites people who edit the code, and it bites hard because the damage is delayed.
`--refresh` does not patch the generated files, it **rewrites them whole** from string templates
in `src/Sts2.SeedFinder.Cli/Tools/` — `ActTables.cs`, `CardPools.cs`, `RelicPools.cs`. Anything
hand-written in `Acts/ActData.cs`, `Cards/CardPoolData.cs` or `Acts/RelicPoolData.cs` that is not
also in its template is deleted the next time anyone answers `y` in step 2.

The failure does not look like a lost edit. It looks like a build break with errors pointing at
files you never touched, because the callers elsewhere still expect what the generated file used
to offer:

```
RunGenerator.cs(306,43): error CS1061: 'ActDefinition' does not contain a definition for 'EventsFor'
```

and repair.bat stops there, correctly saying this is not something it can fix.

So: put the member in the template, keep its text identical to the generated file, and check by
running `--refresh` and confirming `git status` shows the generated file **unmodified**. A refresh
against a game whose data has not moved should produce no diff at all. The `<auto-generated>`
header says "do not hand-edit" for exactly this reason.

### 4. Prove it, then record it

The layer checks above are necessary and **not sufficient**, for two reasons worth understanding:

- The assembled draw chain cannot run outside the game, so its only real oracle is a save file
  the game itself wrote.
- The checks walk *our* lists, so they are blind to **content being added** — which is the most
  likely thing a patch does.

So `repair.bat` runs:

```
sts2seed --verify-history
```

which reads runs you have already finished. Usually there is nothing to do. If you patched and
have not played since, start a run in game, quit to the menu, and run `sts2seed --verify` — that
save is the richer check, carrying the full room sets, relic bags and RNG counter.

Then answer `y` to record your version. That clears the banner.

You may first be asked one extra question:

```
The game's draw-order code has changed since this checkout was last
baselined, but your runs still match it.
Record the new shape as the baseline? Saying no just means you get told again. [y/N]
```

That only appears when the game's code moved but your runs still agree, which usually means the
change did not affect behaviour or you have already reconciled it. `y` stops it being reported
next time; `n` costs nothing but seeing the same question again. It is asked rather than done
silently because a change that your runs happen not to exercise would otherwise have its only
warning quietly erased.

Expect `--verify-history` to skip runs from older game builds, and to report some co-op runs
under "Lobby". A finished run does not record which epochs each player had revealed at the time,
and that changes generation, so the tool fits a state rather than pretending to know one. Runs on
your current build should match with no fitting at all.

---

## Every command, if you prefer typing

| Command | What it does |
|---|---|
| `sts2seed --doctor` | Check by layer; say what still works and what to type next |
| `sts2seed --refresh` | Rewrite the data tables from your game |
| `sts2seed --refresh --dry-run` | Show what that would change, without writing |
| `sts2seed --show [Type.Method]` | Print a game method beside the file that mirrors it |
| `sts2seed --verify-history` | Check against runs you have already finished |
| `sts2seed --verify` | Check against a run in progress |
| `sts2seed --accept` | Record your game as verified (refuses while anything fails) |
| `sts2seed --snapshot` | Re-baseline the draw-order snapshots. Only on a build you trust |

**Double-click `cli.bat`** and type them there. It opens a command line already in this folder
with `sts2seed` ready, so every command this tool prints is one you can type as written.

If you already have a terminal open here, `sts2seed.bat` does the same job (in PowerShell,
prefix it with `.\`).

The three you double-click: `seed-finder.bat` for the seed finder, `repair.bat` after a game update,
`cli.bat` for a command line.

---

## Why some of this cannot be automatic

The game's generation code cannot run outside the game — `ModelDb.Init` wants `ModManager`, and
`LocManager.Initialize()` crashes in the platform layer. That single fact is why this project
reimplements anything at all, and it splits maintenance in two:

- **Data** — which relics are in which pool, rarities, act tables. Facts we copy. `--refresh`
  copies them again, by populating the game's own model database and asking it. There is no
  parsing involved and so nothing to misread.
- **Algorithm** — the order draws happen in. Behaviour we re-expressed in our own code.
  Automating that would mean compiling the game's IL into our port, which needs the engine types
  you cannot instantiate. So the tool **detects** and **locates** those changes, and a person
  makes them.

That is the honest boundary: most patches are one command, and the rest are one method to read.

---

## What would defeat this

Worth stating so nobody wastes an afternoon:

- **Obfuscation, or the game leaving .NET.** The whole approach depends on `sts2.dll` being
  readable managed code. It is unobfuscated today, and MegaCrit ship `0Harmony.dll` inside the
  game, so this is not an imminent worry — but it is the one that ends it.
- **Save-format changes.** The finder would keep working, but `--verify` and `--verify-history`
  would stop reading, and those are the ground truth. Losing the verifier is worse than losing a
  prediction, because you can no longer tell whether anything else is right.
- **A mirrored method nobody listed.** Draw-order detection only covers methods in
  `MirrorMap.All`. If you port a new piece of game behaviour, add it there in the same commit, or
  the next patch to touch it will go unreported.

---

## If you fixed something

`baselines/verified-build.json` and `baselines/method-snapshots.json` are committed files. If you repaired a patch
and got a clean `repair.bat`, those two plus whatever you edited are the change worth sharing —
open a pull request so the next person downloading this does not repeat your afternoon.
