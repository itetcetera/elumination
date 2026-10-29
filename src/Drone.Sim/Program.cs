using System.Diagnostics;
using System.Text.Json;
using Drone.Engine;
using Drone.Engine.Analysis;

// drone-sim: headless DRONE games for balance work.
//   run    --games N --rounds R [--variant NAME]... [--set path=value]... [--seed S] [--species a,b,c,d] [--json FILE]
//   sweep  --param path --values v1,v2,... (plus run's options)
//   game   --seed S --rounds R [...]  one game, round by round
var cmd = args.Length > 0 ? args[0] : "help";
try
{
    var opts = Options.Parse(args.Skip(1).ToArray());
    switch (cmd)
    {
        case "run": Run(opts); break;
        case "sweep": Sweep(opts); break;
        case "game": OneGame(opts); break;
        case "rounds": Rounds(opts); break;
        default:
            Console.WriteLine("""
                drone-sim run   --games 1000 --rounds 12 [--variant original] [--set land.storeStartPrice=200] [--seed 1] [--species sprinter,broker,engineer,settler] [--json out.json]
                drone-sim sweep --param production.learningCap --values 1,2,3 [run options]
                drone-sim rounds --games 300 --rounds 18 [run options]   averages round by round
                drone-sim game  --seed 7 --rounds 12 [--variant original] [--set ...]
                Settings come from settings/drone.json; variants from settings/variants/NAME.json.
                """);
            break;
    }
}
catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException or FormatException)
{
    Console.Error.WriteLine("error: " + e.Message);
    Environment.Exit(1);
}

static Settings Load(Options o) => Settings.LoadDefault(o.All("variant"), o.All("set"));

static void Run(Options o)
{
    var settings = Load(o);
    var (games, rounds, seed, species) = (o.Int("games", 1000), o.Int("rounds", 12), (ulong)o.Int("seed", 1), o.Get("species")?.Split(','));
    var sw = Stopwatch.StartNew();
    var results = Batch.Run(settings, rounds, games, seed, species);
    var report = Report.From(results);
    Console.Write(report.ToText());
    Console.WriteLine($"  ({sw.Elapsed.TotalSeconds:0.0} s, {games / sw.Elapsed.TotalSeconds:0} games/s)");
    if (o.Get("json") is { } path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new { settings = settings.ToJson(), report, games = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  wrote {path}");
    }
}

static void Sweep(Options o)
{
    string param = o.Get("param") ?? throw new ArgumentException("--param is required");
    var values = (o.Get("values") ?? throw new ArgumentException("--values is required")).Split(',');
    var (games, rounds, seed, species) = (o.Int("games", 500), o.Int("rounds", 12), (ulong)o.Int("seed", 1), o.Get("species")?.Split(','));
    var rows = new List<(string value, Report report)>();
    foreach (var v in values)
    {
        var settings = Settings.LoadDefault(o.All("variant"), o.All("set").Append($"{param}={v}"));
        // The same seeds for every value, so differences come from the setting, not the dice.
        rows.Add((v, Report.From(Batch.Run(settings, rounds, games, seed, species))));
    }
    Console.WriteLine($"sweep {param}: {games} games x {values.Length} values, {rounds} rounds");
    Console.WriteLine($"{"value",-10} {"winner",8} {"colony",8} {"margin",7} {"leadchg",7} {"midLdrWin",9} {"export%",7} {"plots",6} {"drones",6} {"free",5} {"landP",6} {"spread",7}");
    foreach (var (v, r) in rows)
    {
        double spread = r.Species.Count > 1 ? r.Species.Values.Max(s => s.WinRate) - r.Species.Values.Min(s => s.WinRate) : 0;
        Console.WriteLine($"{v,-10} {r.WinnerTotal,8:0} {r.Colony,8:0} {r.Margin * 100,6:0.0}% {r.LeadChanges,7:0.00} {r.MidLeaderWins * 100,8:0.0}% {r.WinnerExportShare * 100,6:0.0}% {r.PlayerPlots,6:0.0} {r.PlayerDrones,6:0.0} {r.FreePlotsAtEnd,5:0.0} {r.AvgLandPrice,6:0} {spread * 100,6:0.0}%");
    }
    Console.WriteLine("spread = best minus worst species win rate");
}

static void Rounds(Options o)
{
    var settings = Load(o);
    var (games, rounds, seed, species) = (o.Int("games", 300), o.Int("rounds", 12), (ulong)o.Int("seed", 1), o.Get("species")?.Split(','));
    int n = settings.Game.Players;
    // Per round: energy-short plots, starving turns, food and energy price, energy and food made, units
    // traded, installs (all per game, production and installs per player).
    var sum = new double[rounds + 1, 8];
    var gate = new object();
    Parallel.For(0, games, i =>
    {
        var seats = Enumerable.Range(0, n).Select(p => new SeatSetup($"CPU {p + 1}", true, species != null && p < species.Length ? species[p] : null)).ToList();
        var game = new Game(settings, rounds, seats, seed + (ulong)i).Play();
        var row = new double[rounds + 1, 8];
        foreach (var e in game.Events)
        {
            switch (e)
            {
                case ProductionEvent p: row[p.Round, 0] += p.Shortage; row[p.Round, 4] += p.Produced[(int)Good.Energy] / (double)n; row[p.Round, 5] += p.Produced[(int)Good.Food] / (double)n; break;
                case TurnEvent t when t.Starving: row[t.Round, 1]++; break;
                case AuctionEvent a when a.Good == Good.Food: row[a.Round, 2] += a.StartPrice; break;
                case AuctionEvent a when a.Good == Good.Energy: row[a.Round, 3] += a.StartPrice; row[a.Round, 6] += a.Units; break;
                case TurnActionEvent t when t.Action == "install": row[t.Round, 7] += 1.0 / n; break;
            }
        }
        lock (gate) for (int r = 1; r <= rounds; r++) for (int k = 0; k < 8; k++) sum[r, k] += row[r, k] / games;
    });
    Console.WriteLine($"{games} games, {rounds} rounds: per game (made and installs per player)");
    Console.WriteLine($"{"round",5} {"eShort",7} {"starve",7} {"foodP",6} {"enerP",6} {"eMade",6} {"fMade",6} {"eSold",6} {"install",7}");
    for (int r = 1; r <= rounds; r++)
        Console.WriteLine($"{r,5} {sum[r, 0],7:0.00} {sum[r, 1],7:0.00} {sum[r, 2],6:0} {sum[r, 3],6:0} {sum[r, 4],6:0.0} {sum[r, 5],6:0.0} {sum[r, 6],6:0.0} {sum[r, 7],7:0.00}");
}

static void OneGame(Options o)
{
    var settings = Load(o);
    int rounds = o.Int("rounds", 12);
    var seats = Enumerable.Range(0, settings.Game.Players).Select(p => new SeatSetup($"CPU {p + 1}", true)).ToList();
    var game = new Game(settings, rounds, seats, (ulong)o.Int("seed", 1)).Play();
    Console.WriteLine($"map {game.Map.Cols}x{game.Map.Rows}, {game.Map.ClaimableCount} plots; players: {string.Join(", ", game.Players)}");
    PrintMap(game);
    foreach (var round in game.Events.GroupBy(e => e.Round))
    {
        Console.WriteLine($"-- round {round.Key}");
        foreach (var e in round)
        {
            if (e is TradeEvent) continue;
            if (e is TurnActionEvent t && t.Action is not ("install" or "pub" or "assay" or "crateCash" or "droneLost" or "droneRanAway" or "glimmer")) continue;
            Console.WriteLine($"   {e}");
        }
    }
    PrintMap(game);
}

// Each hex: terrain, owner seat, drone job, crystite level, Helion level.
static void PrintMap(Game game)
{
    var m = game.Map;
    for (int r = 0; r < m.Rows; r++)
    {
        var line = new System.Text.StringBuilder();
        for (int c = 0; c < m.Cols; c++)
        {
            var p = m.At(c, r);
            char t = p.Terrain switch { Terrain.Mountain => '^', Terrain.River => '~', Terrain.Lake => 'o', Terrain.Store => 'S', Terrain.Outpost => 'T', _ => '.' };
            string owner = p.Owner >= 0 ? p.Owner.ToString() : " ";
            string drone = p.Drone switch { Good.Food => "f", Good.Energy => "e", Good.Ore => "o", Good.Crystite => "c", Good.Helion => "h", _ => " " };
            line.Append($"{t}{owner}{drone}{(p.Crystite > 0 ? p.Crystite.ToString() : " ")}{(p.Helion > 0 ? p.Helion.ToString() : " ")} ");
        }
        Console.WriteLine(line);
    }
}

sealed class Options
{
    readonly List<(string key, string value)> _items = [];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected '{args[i]}'");
            string key = args[i][2..];
            string value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            o._items.Add((key, value));
        }
        return o;
    }

    public string? Get(string key) => _items.LastOrDefault(x => x.key == key).value;
    public IEnumerable<string> All(string key) => _items.Where(x => x.key == key).Select(x => x.value);
    public int Int(string key, int fallback) => Get(key) is { } v ? int.Parse(v) : fallback;
}
