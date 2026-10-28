# Verification

What each harness proves and which to reach for. Split out of `CLAUDE.md` because this is
reference you consult when choosing one, not context needed on every change.

```
dotnet run -c Release --project src\Sts2.SeedFinder.Oracle              # vs sts2.dll, no run needed
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --verify     # vs the run in progress
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --verify-history
dotnet run -c Release --project src\Sts2.SeedFinder.Cli -- --gpu-verify # kernels vs Core
```

## The four harnesses cover different failure modes
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

- **`--gpu-verify`** — proves the *kernels*. The Oracle structurally cannot reach them: they are
  a second implementation of the same arithmetic, compiled by a different compiler for a
  different instruction set, so `sts2.dll` has nothing to say about them. They are held to
  `Core` instead, which the Oracle already vouches for. Needs no GPU (it falls back to ILGPU's
  CPU accelerator) and no run. **Run it after any change to `Core/MegaRandom.cs`, `Core/Rng.cs`,
  `Core/GameHash.cs`, `Core/SeedCodec.cs`, `Core/Acts/`, `Core/Cards/`, `Core/Modifiers/` or
  `Gpu/`** — those are the files the kernels mirror. `Core/Acts/` matters most: `RunFilter` reproduces the whole
  UpFront draw order, so a change there that nobody mirrors puts the kernel one draw out and
  every act after that point is quietly wrong.
  It compares hit SETS, not sampled hits, because a kernel that wrongly REJECTS seeds produces
  a search that is quietly incomplete and looks perfectly healthy.
  Its `planner` checks (2026-09-28) go one layer up and run whole searches through
  `GpuSearchPlanner.TryPlan` against plain `SeedSearcher`, because the kernel checks build their
  own pools and parameters and so cannot see the planner building them differently. That gap hid
  a real bug: the card pools came from the lobby's union unlock state instead of each player's
  own, dropping seeds whenever an imported friend epoch differed in card epochs. They need a real
  device and are skipped on the CPU accelerator.

Correct pieces wired in the wrong order pass the Oracle and fail `--verify`. A patch that
changes `NextFloat` fails the Oracle immediately. Card rewards are Oracle-only, by necessity —
see "The first fight's card reward" for why `--verify` cannot reach them.

**Phase 0 spike resolved:** the RNG/hash primitives (`Rng`, `StringHelper`, `MegaRandom`) run
headless with **no Godot runtime and no `ModelDb` init**. Higher-level generation (`ModelDb`,
acts, pools) is untested and may still need Godot — retest when Phase 2 needs it.
