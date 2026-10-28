using System.Collections.Concurrent;
using System.Diagnostics;
using Drone.Engine.Analysis;
using Mule.Engine.Core;
using Mule.Engine.Flow;
using Mule.Engine.Rules;
using PortGame = Mule.Engine.Flow.Game;

// port-baseline --games N [--seed S]: the original rules, played by the original's CPU, summed up
// with the same Report as drone-sim so the two can be read side by side.
int games = 200, firstSeed = 1;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--games") games = int.Parse(args[i + 1]);
    if (args[i] == "--seed") firstSeed = int.Parse(args[i + 1]);
}

var sw = Stopwatch.StartNew();
var results = new ConcurrentBag<GameStats>();
Parallel.For(0, games, i => results.Add(PlayOne(firstSeed + i)));
var report = Report.From(results.OrderBy(r => r.Seed).ToList());
Console.WriteLine("The faithful port (original rules, original CPU):");
Console.Write(report.ToText());
Console.WriteLine("  (not measured for the port: unsold plots, store out of drones, starving turns, energy-short plots, skipped auctions)");
Console.WriteLine($"  ({sw.Elapsed.TotalSeconds:0.0} s)");

static GameStats PlayOne(int seed)
{
    var races = new[] { Race.Mechtron, Race.Bonzoid, Race.Gollumer, Race.Packer };
    var colors = new byte[] { 0x46, 0x86, 0xC6, 0x26 };
    var seats = Enumerable.Range(0, 4).Select(p => new SeatSetup(true, races[p], colors[p])).ToList();
    var rng = new SystemRandomSource(seed);
    var state = Setup.NewGame(rng, seats);
    var game = new PortGame(state, rng);
    var outcome = new ScoreScreen.Outcome();
    game.StartNow(GameLoop.Play(game, outcome));
    var input = new PlayerInput[4];
    for (int p = 0; p < 4; p++) input[p] = new PlayerInput(false);

    var rankings = new List<int[]>();
    var prices = new List<int[]>();
    var revenue = new long[4, 5];
    int good = -1, landSold = 0, skipped = 0;
    long landSum = 0;
    int lastRound = 0;
    while (!game.Finished)
    {
        game.RunFrame(input);
        foreach (var e in game.Events)
        {
            switch (e)
            {
                case GoodsAuctionStartedEvent g: good = g.Good; break;
                case GoodsTradedEvent t when t.Seller < 4 && good >= 0: revenue[t.Seller, good] += t.Price; break;
                case AuctionEndedEvent a when a.Winner < 4 && a.Price > 0: landSold++; landSum += a.Price; break;
                case RoundStartedEvent r:
                    if (r.Round > 1) Snapshot(state, rankings, prices);
                    lastRound = r.Round;
                    break;
            }
        }
        game.Events.Clear();
    }
    Snapshot(state, rankings, prices);

    int total(int p) => state.Score(p, GameState.ScoreTotal);
    var order = state.OrdersPlayer.Select(b => (int)b).ToArray();
    var mid = rankings[Math.Max(0, rankings.Count / 2 - 1)];
    int leadChanges = 0;
    for (int r = 1; r < rankings.Count; r++) if (rankings[r][0] != rankings[r - 1][0]) leadChanges++;
    int win = total(order[0]);
    return new GameStats
    {
        Seed = (ulong)seed,
        Rounds = lastRound,
        Colony = state.TotalScore,
        Rating = state.TotalScore < 10000 ? 0 : Math.Min(6, state.TotalScore / 20000),
        FirstFounder = state.TotalScore >= 60000,
        WinnerSeat = order[0],
        WinnerSpecies = "original",
        Players = Enumerable.Range(0, 4).Select(p => new PlayerStats
        {
            Seat = p,
            Species = "original",
            IsCpu = true,
            Rank = Array.IndexOf(order, p),
            Total = total(p),
            Money = state.Score(p, GameState.ScoreMoney),
            Land = state.Score(p, GameState.ScoreLand),
            GoodsValue = state.Score(p, GameState.ScoreGoods),
            Plots = state.PlotsPlayer.Count(o => o == p),
            Drones = Enumerable.Range(0, GameState.Plots).Count(i => state.PlotsPlayer[i] == p && state.PlotsExp[i] != Exp.None),
            Revenue = Enumerable.Range(0, 5).Select(g => revenue[p, g]).ToArray(),
            LandBought = 0,
        }).ToArray(),
        LeadChanges = leadChanges,
        Margin = win <= 0 ? 0 : (win - total(order[1])) / (double)win,
        MidLeader = mid[0],
        MidLast = mid[3],
        Prices = prices.ToArray(),
        AuctionsSkipped = skipped,
        StorePlotsSold = landSold,
        StorePlotsUnsold = 0,
        AvgLandPrice = landSold == 0 ? 0 : landSum / (double)landSold,
        FreePlotsAtEnd = Enumerable.Range(0, GameState.Plots).Count(i => i != GameState.StorePlot && state.PlotsPlayer[i] == GameState.Nobody),
        RoundsStoreOutOfDrones = 0,
        StarvingTurns = 0,
        ShortageStops = 0,
        AuctionPlots = 0,
        AuctionPlotsIdle = 0,
        GlimmerHunts = 0,
        GlimmerCatches = 0,
        RoundEvents = new(),
    };
}

static void Snapshot(GameState s, List<int[]> rankings, List<int[]> prices)
{
    rankings.Add(s.OrdersPlayer.Select(b => (int)b).ToArray());
    prices.Add(new[] { (int)s.GoodsPrice[0], s.GoodsPrice[1], s.GoodsPrice[2], s.GoodsPrice[3], 0 });
}
