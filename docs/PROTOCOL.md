# Browser ↔ server protocol

The server is `src/Drone.Server` (ASP.NET Core 8). Browsers talk to it over SignalR at `/hub`,
JSON protocol, camelCase names, enums as camelCase strings (`food`, `crystite`, `buy`, `mountain`).
Run it with `dotnet run --project src/Drone.Server`. The rules come from `settings/drone.json`,
found above the app or in the folder named by `DRONE_SETTINGS_DIR`.

The engine decides everything; the browser only sends what the player chose. Every input belongs to
the phase it was sent in: the draft, one land auction, the player's own turn, one goods auction.
Nothing carries over to the next phase. A player who sends nothing is treated as the engine treats
anyone who lets the clock run: a random draft list when the draft closes, no bids, a turn that runs
out, no trades.

## Joining and the lobby

| Call | Arguments | Returns |
|---|---|---|
| `CreateRoom` | `name` | `JoinResult`; the caller is the host |
| `JoinRoom` | `code`, `name`, `token` (or null) | `JoinResult` |
| `SetSpecies` | `species` id from the lobby's `species` list, or null / `"random"` | — |
| `SetOptions` | `rounds` (12 or 18), `landAuction` (`"live"` or `"sealed"`) — host only | — |
| `Start` | — (host only) | — |
| `Leave` | — | — |

`JoinResult` is `{ token, playerId, room }`. Keep `token` (for example in `localStorage`): calling
`JoinRoom` with it again, after a reload or a dropped connection, gives back the same place and
seat. Codes are 6 characters from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` and are not case-sensitive.
An invite link is `/r/<code>`; the server serves the client's `index.html` for it.

Up to 4 players; CPUs take the empty seats when the host starts. Seats (0-3) are dealt at random.
Two players can't pick the same species; players who don't pick get a random one.

Errors (bad code, room full, game started, not the host, not your turn) come back as a hub error
whose message can be shown as it is.

## Playing

| Call | Arguments | When |
|---|---|---|
| `Draft` | `plots`: plot indexes, most wanted first, up to one per player (empty = pass) | the land draft |
| `LandMove` | `direction`: 1 hold up, -1 hold down, 0 let go | a live land auction (the seller moves the start price) |
| `SealedBid` | `bid` (0 = none) | a sealed land auction |
| `Turn` | `{ type, plot?, good? }` (below) | your own development turn |
| `GoodsSide` | `"buy"` or `"sell"` | the moment before a goods auction starts |
| `GoodsMove` | `direction` (1, -1, 0), `belowNeed` (a food or energy seller sells below their need line) | a goods auction |

`LandMove` and `GoodsMove` are held: send them again whenever the player's control changes.

Turn actions are queued and done in order, each taking the turn time the rules give it:
`walkTo` (plot: any plot; the walk time is by distance), `goToTown` (plot: the store or an outpost),
`buyDrone`, `outfit` (good), `sellDrone`, `press` (install, swap or pick up a drone on your own plot),
`release`, `assay` (plot: the whole trip from the store and back), `markForSale` (plot),
`quickSale` (plot), `catchGlimmer` (where you stand), `catchCrate` (where you stand), `pub` (ends
the turn), `endTurn`.

## Server → browser

`lobby` — `{ code, phase: lobby | playing | over, hostId, options: { rounds, landAuction },
roundChoices, species: [{ id, ability }], members: [{ id, name, species, connected, seat }] }`,
sent whenever anything in the room changes, and when the game ends.

`frame` — `{ tick, events, state, full }`, 15 a second while a game runs (the game runs at 60 ticks a
second). `full` is true for the first frame after joining a game under way.

- `events`: what happened since the last frame, in order: each the event's fields plus `t`, its type:
  `Draft`, `LandSold`, `LandUnsold`, `QuickSale`, `Turn`, `TurnAction` (action: install, assay,
  pub, glimmer, crateCash, droneLost, ...), `PersonalEventHappened`, `RoundEventHappened`,
  `Production`, `Trade`, `Auction`, `Score`, `GameOver`. Fields are the records in
  `src/Drone.Engine/State.cs`.
- `state`: the game as it stands (`src/Drone.Server/Wire/StateView.cs`):
  - `tick`, `round`, `rounds`, `over`;
  - `map` `{ cols, rows, plots: [{ index, col, row, terrain, oreUnits, owner, drone, forSale,
    assayed, crystite, helion }] }` — sent only when it changed (always in a full frame); plots are
    in row order, `index = row * cols + col`, odd columns shifted half a hex down;
  - `players`: `[{ seat, name, id, isCpu, species, ability, cash, held: [food, energy, ore,
    crystite, helion], rank, money, land, goods, total, foodNeed, energyNeed }]`;
  - `store`: `{ stock, price, drones, dronePrice, landPrice, outfitCost }` (arrays by good);
  - `phase`: null between phases, or one of
    - `{ kind: "draft", endsAt, submitted }`;
    - `{ kind: "land", plot, seller (-1 = store), startPrice, bottom, positions, highBidder,
      highBid, timer (1 → 0), sealed }`;
    - `{ kind: "turn", seat, seconds, timeLeft, location, inTown, leading, leadJob, message,
      starving, glimmerAt, crate: { row, fromLeft, hexSeconds, cash, good, amount, caught, at },
      eventId, eventAmount }`;
    - `{ kind: "goods", good, startPrice, step, bottom, top, sides, pos, line, timer, units,
      storeSells, trading: [buyer, seller, price] | null }`.

Hidden on purpose, never sent: deposits under plots nobody has assayed, the plots' base outputs,
the Glimmer while it hides, the event deck, the draft lists before the draft closes, sealed bids,
and the CPUs' thinking.
