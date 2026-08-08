# CURRENT LIMITATION(S) BEING WORKED ON

1. Event descriptions. As of now some of the event descriptions still have X placeholders, and I'm not too sure how feasible filling some of them are due to underlying
reasons and not holding any in-repo pushable assets, but still overall supports a breakdown for each event (With most fully described as intended) which was a great addition to the seed-finding UI. I will continue to see how far this can be completed, but ultimately some may end up stuck with X placeholders in parts of the description which is still better than no description at all atleast.


# PENDING

1. Elite fight relics, and a shop's first two relic slots. Grouped because they're held up by the same thing: both depend on your route, which the app doesn't model yet.
The math is already worked out for both, so what's left is deciding how you hand it your route. They'll arrive together.

2. The actual cards from Neow options that give you cards (Hefty Tablet, Arcane Scroll). It already accounts for the draws they use, it just won't show you what you'd
get. Nothing blocking this one, only work.

3. Shop cards and potions. The third relic slot is confirmed and solid, but the card side of a shop moves with how many shops you've visited and your carried relics can
rewrite the pool. Sits behind the route work too.

4. Linux and macOS launchers. Only the .bat files are Windows-only, the app itself already runs fine there. Small job if there's demand, and will likely be shipped regardless soon enough.


# CONSTANT FACTOR

1. Optimization of the seed/sec generation. Probably the most important and complicated portion left to improve as this is ultimately the core of the app, especially
for tough seed combinations as multiplayer seed specifications can get pretty crazy.