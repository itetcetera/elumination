# The DRONE engine

## How a game runs

`Game.Run()` is the whole game as a coroutine. Each value it yields is a number of ticks to let pass
(60 ticks a second, `game.ticksPerSecond`). The round follows the original order (C1):

1. Land draft (`LandGrant`), if any plot is free
2. Player land sales, then the store's land auction (`LandAuction`)
3. Development turns, one player at a time (`DevTurn`)
4. Production (`Production`), then the round event (`RoundEvents`), except in the last round
5. Prices (`Prices.BeforeAuctions`), then one live auction per good (`GoodsAuction`)
6. The store builds drones (`Prices.BuildDrones`), then scoring and ranking (`Scoring`)

Every decision goes through a seat (`ISeat`). Each call happens on the game clock; a seat that
returns `null` (a human who hasn't decided yet) lets a tick pass and is asked again until that
phase's clock runs out. `CpuSeat` answers at once, which is why a headless game takes milliseconds.
A live server would implement `ISeat` with each player's latest input, run `Game.Run()` in real time,
and send `game.Phase` and `game.Events` to the clients. The port's rooms, SignalR hub and Docker
image can carry that over unchanged; that's the next step after the balance work.

All randomness comes from the game's seeded generator (`Rng`, xoshiro256**), so a seed replays a
game exactly. CPU thinking draws from its own stream (`CpuRng`), so changing the CPU never moves
the world's dice.

## Development turns

A turn is a time budget (F3: scaled by food, never below the food floor). Commands cost time:
walking costs per hex by terrain (mountains and water are slower), town visits cost fixed time, and
the turn ends at the pub, when the player ends it, or when time runs out (a led drone is then lost,
F5). The CPU sends whole errands (`WalkTo` a plot); a human client would send one hex at a time,
so the same rules cover both.

The Glimmer (shows and hides on mountains) and the drifting supply crate (crosses one row, one hex
every few seconds) are timed within the turn; catching either means standing on its plot at the
right moment.

## Auctions

Land: bidders climb a price scale in steps that grow with the price (E8); the clock runs slow while
anyone moves and fast while nobody does; first to a price holds it; the winner pays their bid. A
player sale's seller moves the start price. The sealed mode (host option) pays the second-highest
bid, with the start price as the minimum.

Goods: each player's place on the scale is a price. Buyers start below the bottom, sellers above
the top. When the best buyer is at or above the best seller, units change hands one at a time at
the seller's price, quicker as the trade runs. The store sells at the top while it has stock (not
exports) and buys at the bottom. Food and energy sellers stop at their need line unless they push
on below it (I12). The clock stops while units move.

## The CPU

`CpuSeat` values each job on each plot as expected output (terrain, scale, learning curve; exports
from what assays have proved) times what a unit is worth to it (the store price, plus a bonus when
it's short of food or energy, less the energy the drone will burn). It drafts its best plots, bids
up to a plot's worth (its 500 score value plus some of its output over the rounds left) while
keeping cash back, does the best jobs its time allows, assays, catches the crate when that beats
the pub, then gambles. In auctions it sells ore and exports, sells food and energy surpluses, buys
what it's short of, and moves toward its limit price as the clock runs. All its numbers are under
`cpu` in the settings.

It doesn't sell land and doesn't hunt the Glimmer yet. It is a baseline, not a strong player:
balance results are only as good as the CPU, so treat early species numbers as a first read.

## Simulations

`drone-sim run` plays N games in parallel and prints the report: winner and colony totals, colony
ratings, tension (lead changes, final margin), catch-up (how often the half-time leader wins, how
often half-time last place climbs), the share of the winner's wealth from exports, land and store
health, and win rates by species with 95% intervals. `--json` writes every game for deeper looks.
`drone-sim sweep` runs the same seeds for each value of one setting, so the differences come from
the setting, not the dice.

## Comparing with the original

`settings/variants/original.json` plays the original rules as closely as a hex map allows: one
export, no species, the original caps, learning curve, auction steps and events. `tools/PortBaseline`
plays the faithful port with its original CPUs. 1,000 games each, 12 rounds, 2026-10-28:

| | Port (original CPU) | DRONE engine, original variant (new CPU) |
| --- | ---: | ---: |
| Winner's total | 26,459 | 24,473 |
| Colony total | 86,899 | 78,141 |
| Lead changes | 3.78 | 3.55 |
| Final margin | 12.6% | 15.2% |
| Half-time leader wins | 50.0% | 49.1% |
| Half-time last place climbs | 51.9% | 51.0% |
| Winner's wealth from exports | 39.0% | 35.0% |
| Plots / drones per player | 11.0 / 10.9 | 11.0 / 10.2 |
| Store land price paid | $715 | $797 |
| Final food / energy price | 122 / 86 | 89 / 29 |

The shape of the game matches: the same tension and catch-up, about the same share from crystite,
the same land use. The new CPU makes about 10% less: it leaves some energy short (about 14 plots a
game stop) and pays a little more for land. Those are CPU tuning items, not rule differences.

## Assumptions made where the design doc left room

These are starting points in the settings, to confirm or change:

- Map sizes 9x5 / 9x7 / 13x7 by game length; Helion seeds 2 / 3 / 4; lakes 0 / 2 / 3; outposts
  0 / 1 / 2 on the river above and below the store; river-bank food bonus +1.
- Walking costs per hex (plain 0.93 s, mountain 1.6 s, water 1.3 s) and town visit times are set so
  a job costs about what the original CPU's time model charged (25 + 4 x distance time pixels).
- Species (Marcus, 2026-10-28: costs and benefits in whole amounts; speeds may be percentages):
  Sprinter walks 50% faster and needs 1 more food each turn; Broker moves 4 auction lines in the
  time others move 3 and starts with $200 less; Prospector assays in half the time and its food
  plots making 3 or more make 1 less; Engineer gets 1 free energy per 5 drones and pays $40 more
  per drone; Survivor's money and goods events are halved, bad and good alike; Settler is the
  normal species with no ability and no cost. The draft-first ability and a land surcharge are
  still in the engine (`draftFirst`, `landSurcharge`) for sweeps.
- Helion price 80-120 (crystite 50-149). Exports don't spoil (Marcus, 2026-10-29); the hold cap
  limits hoarding. If a cost is ever needed, it will be called storage costs.
- New events per 11 rounds: Ice raiders 1 (take half of held Helion), Comet 2, Supply ship 2
  (+8 food, +8 energy to the store), Drone malfunction 2.
- Learning curve cap 2 and output cap 10 (original 1 and 8).
- Colony verdict thresholds scale with plots and with game length.
- Quick sale pays half the store's land price, plus the drone price for a drone on the plot.

## First balance read (CPU games, default settings)

From 1,000 games at each length:

- With the Settler as the normal species (no ability, no cost), the field is much closer: at 12
  rounds the Engineer wins 31%, the Settler 24%, the Sprinter 20% (25% is even); at 24 rounds the
  Prospector and Engineer win 32%, the Sprinter 18%. The Sprinter's extra food costs more than its
  speed earns. (The earlier draft-first Settler won 41-56%.)
- Exports take over in longer games: 46% of the winner's wealth at 12 rounds, 69% at 18, 79% at 24.
  This is the "crystite grab" the design doc wants to avoid; spoilage and price bands are the
  knobs.
- Catch-up holds at 12 and 18 rounds (the half-time leader wins 42-49%) but weakens at 24 (61%).

## The x2 scale (the default since 2026-10-28)

`settings/drone.json` now doubles every amount of goods and money from the original scale (Marcus,
2026-10-28; `settings/variants/x1.json` and `original.json` keep the original scale): production, needs, stocks,
event amounts, cash, land and drone prices, scores. Unit prices stay the same (twice the money for
twice the goods). Auctions still trade one unit at a time, twice as fast, and speed up over twice
as many units, so they last about as long. Auction movement is unchanged: a buyer backing away from
a seller who follows it down gets a few more units than before (about 5 in original units against 3). The point is room for finer species costs and benefits in whole amounts (for example the
Sprinter's +1 food is now half of what it was).

1,000 games each, same seeds, 2026-10-28:

| | x1 | x2 | x2 / 2 |
| --- | ---: | ---: | ---: |
| Colony, 12 rounds | 89,677 | 175,509 | 87,755 |
| Lead changes, 12 rounds | 3.95 | 3.95 | |
| Half-time leader wins, 12 rounds | 41.0% | 43.5% | |
| Exports' share, 12 rounds | 45.7% | 43.2% | |
| Colony, 24 rounds | 473,521 | 930,846 | 465,423 |
| Exports' share, 24 rounds | 79.9% | 78.3% | |

The game keeps its shape, about 2% smaller. A starving turn means no food at all (Marcus,
2026-10-28); the floor still gives about 18 seconds, for food drones only. With that, starving
turns match too (2.3 a game today, 2.5 at x2). The care package still needs exactly 0 food.

The turn is 50 seconds (Marcus, 2026-10-28; the original's 47.13 stays in the `original` variant).

First finer species try under x2 (2,000 games each): the Sprinter at +2 food (the same cost as
today) wins 21% at 12 rounds and 28% at 24; at +1 food it wins 23% and 28%. Half the food cost
helps it in short games and changes little in long ones, where its speed already pays.

## Rules decided 2026-10-28/29 (Marcus)

At the x2 scale:

- A plot switched to a mining job makes half its output that round (`production.miningSwitchDivisor`).
- Deposits spread only to touching plots: high at the seed, medium next to it (`map.seedRadius` 1).
- Learning curve: +1 per matching neighbour; food and energy +1 per 2 (`farmNeighboursPerBonus`).
- No river-bank food bonus; plain energy plots make 5 (was 6).
- Ore, crystite and Helion hold cap 50; food and energy have spoilage only, exports neither.
- 2 raid cards per export (pirates for crystite, ice raiders for Helion).
- CPU: holds exports priced in the bottom 35% of their band (80% of the time) and ore below 52,
  selling only to bring cash up to $2,000; keeps money to develop a plot it bids on.

All-Settler CPU games, 1,000 each: exports take 36% / 54% / 53% of the winner's wealth at 12 / 18 /
24 rounds; the half-time leader wins 47% at every length; lead changes 3.8 / 4.3 / 4.1.

## Tuning targets (Marcus, 2026-10-29)

Measured on all-Settler CPU games (`--species settler,settler,settler,settler`) at 12, 18 and 24
rounds. Each target holds at every length.

| Target | Bar | Today (12 / 18 / 24) |
|---|---|---|
| Half-time leader wins | at most 55% | 49% / 55% / 53% |
| Lead changes | at least 3.5 | 3.4 / 4.1 / 3.7 |
| Final margin (winner over second) | 10-15% | 14% / 16% / 22% |
| Winner's wealth from exports | 30-55% | 31% / 55% / 50% |
| Middle-third food price | 60-100 | 66 / 75 / 66 |
| Middle-third energy price | 40-70 | 37 / 47 / 40 |
| Starving turns per game | at most 1.5 | 1.0 / 0.9 / 1.5 |
| Energy-short plots per player per game | at most 2 | 2.2 / 5.8 / 8.1 |
| First Founder | 85-90% of games | 99.5% / 97.1% / 96.7% |

First Founder depends only on the colony total, so it is set last, through the rating thresholds.
Prices are measured over the middle third of the rounds (Marcus): late in the game every player covers
their own food and energy, so those prices fall to their floors whatever the rules, and only rise
when players fail to feed or power themselves.
Marcus wants players to compete with each other, which sometimes means holding the others down.
