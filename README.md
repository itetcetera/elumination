# ELUM: DRONE

DRONE is a modern reimagining of M.U.L.E., set on the planet ELUM. Drones take the mules' place,
the map is a hex grid, six species trade abilities for costs, and a second export good (Helion ice)
joins crystite. The design decisions live in the DRONE design doc.

This repository holds DRONE's game engine and its online server. The engine is written simulation-first:

- **Headless and fast.** A whole game runs in about 10 ms with CPU players, so thousands of games
  answer a balance question in seconds.
- **One settings file.** Every tunable number is in [`settings/drone.json`](settings/drone.json).
  Sweeps change values from the command line; variants (like [`original`](settings/variants/original.json))
  are partial copies merged over it.
- **One set of rules for live and simulated games.** The game is a coroutine on a 60 ticks/s clock.
  A live server waits each step out in real time and feeds players' inputs to their seats; the
  simulator just adds the steps up.

## Layout

| Path | What it is |
| --- | --- |
| `src/Drone.Engine` | The rules: map, draft, land auctions, development turns, production, events, goods auctions, scoring |
| `src/Drone.Engine/Cpu` | DRONE's own CPU player (it also plays every simulation) |
| `src/Drone.Engine/Analysis` | Batch runs and the balance report |
| `src/Drone.Sim` | `drone-sim`, the command-line simulator |
| `src/Drone.Server` | The online server: rooms with invite codes, the browsers' SignalR hub (see `docs/PROTOCOL.md`) |
| `client/` | The browser client the server hands out |
| `tests/Drone.Engine.Tests` | Rule tests |
| `tests/Drone.Server.Tests` | Server tests: rooms, and browsers playing whole games through the hub |
| `tools/PortBaseline` | Plays the faithful port with the original CPUs, for comparison |
| `settings/` | `drone.json` and its variants |
| `docs/ENGINE.md` | How the engine works, and the assumptions made where the design doc left room |
| `docs/PROTOCOL.md` | What the browser and the server send each other |

## Running it

Needs the .NET 8 SDK.

```sh
dotnet test                                    # the rule tests
cd src/Drone.Sim
dotnet run -c Release -- run --games 1000 --rounds 12
dotnet run -c Release -- run --games 1000 --variant original
dotnet run -c Release -- sweep --param production.learningCap --values 1,2,3 --games 500
dotnet run -c Release -- run --games 2000 --set goods.crystite.spoilage=0.4 --set land.storeStartPrice=200
dotnet run -c Release -- run --species settler,broker,engineer,survivor --games 1000
dotnet run -c Release -- game --seed 7         # one game, round by round, with the map
```

The port baseline needs a checkout of `itetcetera/crispy-fiesta` next to this repository:

```sh
cd tools/PortBaseline
dotnet run -c Release -- --games 500
```

## Playing online

```sh
dotnet run --project src/Drone.Server          # then open http://localhost:5000
docker build -t drone . && docker run -p 8080:8080 drone
```

One image holds the server, the settings and the browser client. Rooms live in memory: a restart
ends the games under way.

The client in `client/` is a plain playtest client (HTML, CSS and one script, no build step). One
player starts a room and shares the invite link; up to four people join, pick a species, and the host
starts. CPUs take the empty seats. In auctions, hold the Up and Down buttons or the arrow keys. In
your turn, click the map to walk (the store or an outpost takes you into town), and use the buttons
for everything else; actions queue up and happen in order.
