namespace Drone.Engine.Analysis;

/// <summary>What one finished game says about balance (filled from the game's events).</summary>
public sealed record GameStats
{
    public required ulong Seed { get; init; }
    public required int Rounds { get; init; }
    public required int Colony { get; init; }
    public required int Rating { get; init; }
    public required bool FirstFounder { get; init; }
    public required int WinnerSeat { get; init; }
    public required string WinnerSpecies { get; init; }
    public required PlayerStats[] Players { get; init; }
    /// <summary>How many times the leader changed after round 1 (a tense game changes hands).</summary>
    public required int LeadChanges { get; init; }
    /// <summary>(winner - second) / winner at the end.</summary>
    public required double Margin { get; init; }
    /// <summary>Seat of the leader and of last place at half time.</summary>
    public required int MidLeader { get; init; }
    public required int MidLast { get; init; }
    /// <summary>Average price per good after each round's auction, [round][good].</summary>
    public required int[][] Prices { get; init; }
    public required int AuctionsSkipped { get; init; }
    public required int StorePlotsSold { get; init; }
    public required int StorePlotsUnsold { get; init; }
    public required double AvgLandPrice { get; init; }
    public required int FreePlotsAtEnd { get; init; }
    public required int RoundsStoreOutOfDrones { get; init; }
    public required int StarvingTurns { get; init; }
    public required int ShortageStops { get; init; }
    /// <summary>Plots bought at auction, and how many waited 2+ rounds for a drone (or never got one).</summary>
    public required int AuctionPlots { get; init; }
    public required int AuctionPlotsIdle { get; init; }
    public required int GlimmerHunts { get; init; }
    public required int GlimmerCatches { get; init; }
    public required Dictionary<string, int> RoundEvents { get; init; }

    public static GameStats From(Game game)
    {
        var events = game.Events;
        var over = events.OfType<GameOverEvent>().Single();
        int n = game.Players.Count;
        var scores = events.OfType<ScoreEvent>().GroupBy(e => e.Round).OrderBy(g => g.Key).ToList();
        int leadChanges = 0, lastLeader = -1;
        foreach (var round in scores)
        {
            int leader = round.First(e => e.Rank == 0).Seat;
            if (lastLeader >= 0 && leader != lastLeader) leadChanges++;
            lastLeader = leader;
        }
        var mid = scores[Math.Max(0, scores.Count / 2 - 1)].ToList();
        var final = game.Players.OrderBy(p => p.Rank).ToList();
        int winTotal = final[0].Score.Total;
        var revenue = new long[n, Engine.Goods.Count];
        foreach (var t in events.OfType<TradeEvent>())
            if (t.Seller >= 0) revenue[t.Seller, (int)t.Good] += t.Price;

        var prices = new int[game.Rounds][];
        for (int r = 0; r < game.Rounds; r++) prices[r] = new int[Engine.Goods.Count];
        foreach (var a in events.OfType<AuctionEvent>()) prices[a.Round - 1][(int)a.Good] = a.NewPrice;
        var landSold = events.OfType<LandSoldEvent>().Where(e => e.Store).ToList();
        var installs = events.OfType<TurnActionEvent>().Where(e => e.Action == "install").ToList();
        int idle = events.OfType<LandSoldEvent>().Count(sold =>
        {
            var first = installs.FirstOrDefault(i => i.Plot == sold.Plot && i.Seat == sold.Buyer && i.Round >= sold.Round);
            return (first == null ? game.Rounds + 1 : first.Round) - sold.Round >= 2;
        });
        var players = game.Players.Select(p => new PlayerStats
        {
            Seat = p.Seat,
            Species = p.SpeciesId,
            IsCpu = p.IsCpu,
            Rank = p.Rank,
            Total = p.Score.Total,
            Money = p.Score.Money,
            Land = p.Score.Land,
            GoodsValue = p.Score.GoodsValue,
            Plots = game.PlotsOf(p).Count(),
            Drones = game.PlotsOf(p).Count(x => x.Drone != null),
            Revenue = Engine.Goods.All.Select(g => revenue[p.Seat, (int)g]).ToArray(),
            LandBought = events.OfType<LandSoldEvent>().Count(e => e.Buyer == p.Seat),
        }).ToArray();
        return new GameStats
        {
            Seed = 0,
            Rounds = game.Rounds,
            Colony = over.Colony,
            Rating = over.Rating,
            FirstFounder = over.FirstFounder,
            WinnerSeat = over.Winner,
            WinnerSpecies = game.Players[over.Winner].SpeciesId,
            Players = players,
            LeadChanges = leadChanges,
            Margin = winTotal <= 0 ? 0 : (winTotal - final[1].Score.Total) / (double)winTotal,
            MidLeader = mid.First(e => e.Rank == 0).Seat,
            MidLast = mid.First(e => e.Rank == n - 1).Seat,
            Prices = prices,
            AuctionsSkipped = events.OfType<AuctionEvent>().Count(e => e.Skipped),
            StorePlotsSold = landSold.Count,
            StorePlotsUnsold = events.OfType<LandUnsoldEvent>().Count(e => e.Seller < 0),
            AvgLandPrice = landSold.Count == 0 ? 0 : landSold.Average(e => e.Price),
            FreePlotsAtEnd = game.Map.FreePlots.Count(),
            RoundsStoreOutOfDrones = game.StoreDronesAfterTurns.Count(d => d == 0),
            StarvingTurns = events.OfType<TurnEvent>().Count(e => e.Starving),
            ShortageStops = events.OfType<ProductionEvent>().Sum(e => e.Shortage),
            AuctionPlots = events.OfType<LandSoldEvent>().Count(),
            AuctionPlotsIdle = idle,
            GlimmerHunts = events.OfType<TurnActionEvent>().Count(e => e.Action == "glimmerHunt"),
            GlimmerCatches = events.OfType<TurnActionEvent>().Count(e => e.Action == "glimmer"),
            RoundEvents = events.OfType<RoundEventHappened>().GroupBy(e => e.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
        };
    }
}

public sealed record PlayerStats
{
    public required int Seat { get; init; }
    public required string Species { get; init; }
    public required bool IsCpu { get; init; }
    public required int Rank { get; init; }
    public required int Total { get; init; }
    public required int Money { get; init; }
    public required int Land { get; init; }
    public required int GoodsValue { get; init; }
    public required int Plots { get; init; }
    public required int Drones { get; init; }
    /// <summary>Money taken in by selling each good, [good].</summary>
    public required long[] Revenue { get; init; }
    public required int LandBought { get; init; }
}
