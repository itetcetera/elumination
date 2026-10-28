using System.Collections.Concurrent;

namespace Drone.Engine.Analysis;

/// <summary>Runs many headless CPU games in parallel and sums up the balance numbers.</summary>
public static class Batch
{
    public static List<GameStats> Run(Settings settings, int rounds, int games, ulong firstSeed, string[]? species = null, Action<int>? progress = null)
    {
        var results = new ConcurrentBag<GameStats>();
        int done = 0;
        Parallel.For(0, games, i =>
        {
            ulong seed = firstSeed + (ulong)i;
            var seats = Enumerable.Range(0, settings.Game.Players)
                .Select(p => new SeatSetup($"CPU {p + 1}", true, species != null && p < species.Length ? species[p] : null))
                .ToList();
            var game = new Game(settings, rounds, seats, seed).Play();
            results.Add(GameStats.From(game) with { Seed = seed });
            int d = Interlocked.Increment(ref done);
            progress?.Invoke(d);
        });
        return results.OrderBy(r => r.Seed).ToList();
    }
}

/// <summary>The summary of a batch, with the numbers the design doc asks the simulator for.</summary>
public sealed record Report
{
    public required int Games { get; init; }
    public required int Rounds { get; init; }
    public required double WinnerTotal { get; init; }
    public required double Colony { get; init; }
    public required double[] RatingShare { get; init; }
    public required double FirstFounderShare { get; init; }
    public required double LeadChanges { get; init; }
    public required double Margin { get; init; }
    /// <summary>Share of games the half-time leader goes on to win (lower = more catch-up).</summary>
    public required double MidLeaderWins { get; init; }
    /// <summary>Share of games the half-time last place finishes above last.</summary>
    public required double MidLastClimbs { get; init; }
    public required Dictionary<string, SpeciesRow> Species { get; init; }
    /// <summary>Share of the winner's total that came from selling export goods.</summary>
    public required double WinnerExportShare { get; init; }
    public required double[] MeanFinalPrice { get; init; }
    public required double AuctionsSkipped { get; init; }
    public required double StorePlotsSold { get; init; }
    public required double StorePlotsUnsold { get; init; }
    public required double AvgLandPrice { get; init; }
    public required double FreePlotsAtEnd { get; init; }
    public required double RoundsStoreOutOfDrones { get; init; }
    public required double StarvingTurns { get; init; }
    public required double ShortageStops { get; init; }
    public required double PlayerTotal { get; init; }
    public required double PlayerPlots { get; init; }
    public required double PlayerDrones { get; init; }

    public sealed record SpeciesRow(int Played, double WinRate, double WinRateLow, double WinRateHigh, double AvgRank, double AvgTotal);

    public static Report From(IReadOnlyList<GameStats> games)
    {
        int n = games.Count;
        var players = games.SelectMany(g => g.Players).ToList();
        var species = players.GroupBy(p => p.Species).ToDictionary(g => g.Key, g =>
        {
            int played = g.Count();
            int wins = g.Count(p => p.Rank == 0);
            var (lo, hi) = Wilson(wins, played);
            return new SpeciesRow(played, wins / (double)played, lo, hi, g.Average(p => p.Rank + 1.0), g.Average(p => (double)p.Total));
        });
        int goods = Engine.Goods.Count;
        return new Report
        {
            Games = n,
            Rounds = games[0].Rounds,
            WinnerTotal = games.Average(g => (double)g.Players.First(p => p.Rank == 0).Total),
            Colony = games.Average(g => (double)g.Colony),
            RatingShare = Enumerable.Range(0, 7).Select(r => games.Count(g => g.Rating == r) / (double)n).ToArray(),
            FirstFounderShare = games.Count(g => g.FirstFounder) / (double)n,
            LeadChanges = games.Average(g => (double)g.LeadChanges),
            Margin = games.Average(g => g.Margin),
            MidLeaderWins = games.Count(g => g.MidLeader == g.WinnerSeat) / (double)n,
            MidLastClimbs = games.Count(g => g.Players.First(p => p.Seat == g.MidLast).Rank < g.Players.Length - 1) / (double)n,
            Species = species,
            WinnerExportShare = games.Average(g =>
            {
                var w = g.Players.First(p => p.Rank == 0);
                return w.Total <= 0 ? 0 : (w.Revenue[(int)Good.Crystite] + w.Revenue[(int)Good.Helion]) / (double)w.Total;
            }),
            MeanFinalPrice = Enumerable.Range(0, goods).Select(i => games.Average(g => (double)g.Prices[^1][i])).ToArray(),
            AuctionsSkipped = games.Average(g => (double)g.AuctionsSkipped),
            StorePlotsSold = games.Average(g => (double)g.StorePlotsSold),
            StorePlotsUnsold = games.Average(g => (double)g.StorePlotsUnsold),
            AvgLandPrice = games.Where(g => g.StorePlotsSold > 0).Select(g => g.AvgLandPrice).DefaultIfEmpty(0).Average(),
            FreePlotsAtEnd = games.Average(g => (double)g.FreePlotsAtEnd),
            RoundsStoreOutOfDrones = games.Average(g => (double)g.RoundsStoreOutOfDrones),
            StarvingTurns = games.Average(g => (double)g.StarvingTurns),
            ShortageStops = games.Average(g => (double)g.ShortageStops),
            PlayerTotal = players.Average(p => (double)p.Total),
            PlayerPlots = players.Average(p => (double)p.Plots),
            PlayerDrones = players.Average(p => (double)p.Drones),
        };
    }

    /// <summary>95% Wilson interval for a share.</summary>
    public static (double Low, double High) Wilson(int k, int n)
    {
        if (n == 0) return (0, 0);
        const double z = 1.96;
        double p = k / (double)n, d = 1 + z * z / n;
        double c = (p + z * z / (2 * n)) / d;
        double h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return (Math.Max(0, c - h), Math.Min(1, c + h));
    }

    public string ToText()
    {
        var w = new StringWriter();
        string Pct(double x) => $"{x * 100,5:0.0}%";
        w.WriteLine($"{Games} games, {Rounds} rounds");
        w.WriteLine($"  winner total {WinnerTotal,8:0}   player total {PlayerTotal,8:0}   colony {Colony,8:0}");
        w.WriteLine($"  colony ratings 0..6: {string.Join(" ", RatingShare.Select(Pct))}   First Founder {Pct(FirstFounderShare)}");
        w.WriteLine($"  tension: lead changes {LeadChanges:0.00}, final margin {Pct(Margin)}");
        w.WriteLine($"  catch-up: half-time leader wins {Pct(MidLeaderWins)}, half-time last climbs {Pct(MidLastClimbs)}");
        w.WriteLine($"  winner's wealth from exports {Pct(WinnerExportShare)}");
        w.WriteLine($"  per player: plots {PlayerPlots:0.0}, drones {PlayerDrones:0.0}");
        w.WriteLine($"  land: store plots sold {StorePlotsSold:0.0} (avg ${AvgLandPrice:0}), unsold {StorePlotsUnsold:0.0}, free at end {FreePlotsAtEnd:0.0}");
        w.WriteLine($"  store out of drones {RoundsStoreOutOfDrones:0.0} rounds; starving turns {StarvingTurns:0.0}; energy-short plots {ShortageStops:0.0}; auctions skipped {AuctionsSkipped:0.0}");
        w.WriteLine($"  final prices: {string.Join("  ", Engine.Goods.All.Select(g => $"{g} {MeanFinalPrice[(int)g]:0}"))}");
        if (Species.Count > 1)
        {
            w.WriteLine("  species      played   win rate (95% CI)      avg rank   avg total");
            foreach (var (k, r) in Species.OrderByDescending(x => x.Value.WinRate))
                w.WriteLine($"  {k,-11} {r.Played,7}   {Pct(r.WinRate)} ({Pct(r.WinRateLow)}-{Pct(r.WinRateHigh)})   {r.AvgRank,5:0.00}   {r.AvgTotal,9:0}");
        }
        return w.ToString();
    }
}
