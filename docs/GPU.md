# The GPU accelerator

Everything about `src/Sts2.SeedFinder.Gpu`. Split out of `CLAUDE.md` because none of it is
needed to work on the project, only to work on the accelerator. The one rule that matters
everywhere else is the first paragraph below: it may only NARROW.

**Run `--gpu-verify` after any change to `Core/MegaRandom.cs`, `Core/Rng.cs`,
`Core/GameHash.cs`, `Core/SeedCodec.cs`, `Core/Acts/`, `Core/Cards/`, `Core/Modifiers/` or
`Gpu/`.** Those are the files the kernels mirror.

---

`src/Sts2.SeedFinder.Gpu` is an **optional** accelerator (ILGPU: CUDA, OpenCL, CPU fallback).
`Core` does NOT reference it, and never should: the accelerator plugs in through
`SeedSearcher`'s `candidateIndices` parameter, which may only NARROW which indices get
examined. Every candidate still goes through the whole CPU criteria chain, so an accelerated
search returns the same set. A GPU is never required to build or run; `STS2_GPU=off` disables
it, `cuda`/`opencl`/`cpu` force a backend.

Ported so far, all fused into one pass (`SeedFilter`) and ordered cheapest-first: **acts**,
**Neow (both branches)**, **card rewards**, the **three modifier payloads** (Specialized, All
Star, Draft), **all four Neow card payloads** and **run generation** for bosses, events, Ancient
identity and shop relics.

**The Neow payloads are three chains behind one stage.** `PayloadShapeOfSlot` says which to walk
for each slot, because two players can be asked about different relics. One stream per slot means
one chain per slot: two relics of the SAME shape merge by taking the deeper count, which only
over-accepts (asking whether a card is among Hefty Tablet's three is looser than asking whether
it is Arcane Scroll's one), but two DIFFERENT shapes on one slot make the planner decline. Scroll
Boxes' bundle rule is deliberately left out of the kernel, the same way Draft's matching is: it
asks whether the cards are present at all and `SeedSearcher` runs the real same-bundle check.

Every one of them is faster than the CPU path on the same criteria, by margins that depend on
how much work the criterion makes per seed: a stage that walks sixty draws gains far more than
one that walks a single pick. `--gpu-bench` measures the machine it is run on, which is the only
number worth trusting, since it varies with the device, the criteria and how early a seed is
rejected.

**The modifier payloads share the card stage** rather than getting one each, because all three
come off the same per-player `Rewards` stream it already walks. `CardCriteriaView.Fight` is the
discriminator: positive is a fight, 0 a Neow payload, and -1/-2/-3 Specialized/All Star/Draft.
Three things they needed, each worth knowing before touching `GpuCardPools`:
a flat **`Offerable`** index per pool, since a uniform pick indexes the non-Basic, non-Ancient
entries in POOL order, which is neither one rarity group nor the groups concatenated; the
**colourless pool** uploaded once per distinct unlock state, since it has five epoch gates of
its own; and **`OfferableOwnCount`**, the prefix of a pool belonging to the player's own
character, because Specialized must not follow CharacterCards into the widened pool.

How exact each payload test is decides whether the kernel may reject. Specialized satisfies
EVERY matching criterion (one card, so two asks for it are one question). All Star claims one
still-unsatisfied criterion per draw, which is exact because criteria naming the same card are
interchangeable. **Draft is deliberately looser**: the game takes one card per reward, so two
wanted cards sharing a reward cannot both be had, and the kernel only asks whether enough
DISTINCT rewards offer them. Exact per card, over-accepting across different cards, which is
free because `SeedSearcher` re-runs the real matching on every candidate.

**The positive branch never materialises the offer.** Building it the CPU's way needs a mutable
variable-length list per thread. Instead the kernel computes where the wanted relic STARTS and
follows that one position through the same reverse Fisher-Yates swaps, then asks whether it
landed at index 0 or 1 — the `RunFilter` trick, applied to Neow. Three things make the starting
position cheap: availability filtering is lobby-dependent and not seed-dependent, so the base
pool is a host constant; the counterpart removals are a five-row table keyed by the curse that
rolled (`NeowPrefilterView.RemovedMask`); and the coin flips append in a fixed order. The
Large Capsule skip is a `continue`, not a burned draw, because it genuinely removes a draw.

**Not ported: treasure chests, and Ancient OFFERS.** Both for structural reasons, not lack of
time. An Ancient's offer runs `AncientOffers.Branches`, which returns variable-length branch
sets — the GPU filters on which Ancient turns up and the CPU decides what it offers. Chests are
the harder one and the analysis is worth not re-deriving: `RunFilter` never materialises a
shuffle, it follows one entry through the same swaps (see its class comment), which answers
"where did this relic land". A chest asks the INVERSE, "which relic is at the front", and for
Act 3 the `DroppedAtAct3Chest` gate means the answer depends on the identity of everything
ahead of it. Acts 1 and 2 have no gate and their chest index is fixed by the rarity rolls
alone, so those two ARE expressible by tracking; Act 3 would need the deque built. A partial
port covering acts 1 and 2 and declining act 3 would be sound, since declining only ever
hands work back to the CPU.
