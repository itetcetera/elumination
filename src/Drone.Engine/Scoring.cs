namespace Drone.Engine;

/// <summary>
/// Scoring (section J): money, land (a flat value per plot plus each drone's outfit), and goods
/// (each installed drone at the current drone price, held goods at current prices). The richest
/// player wins, whatever the colony's fate. Rank ties go to more cash, then a fair draw.
/// </summary>
public static class Scoring
{
    public static Score Of(Game game, Player p)
    {
        var s = game.Settings;
        int land = 0, goods = 0;
        foreach (var plot in game.PlotsOf(p))
        {
            land += s.Scoring.PlotValue;
            if (plot.Drone is { } d)
            {
                land += s.Goods.Of(d).OutfitCost;
                goods += game.Store.DronePrice;
            }
        }
        foreach (var g in Goods.All) goods += p.Held[(int)g] * game.Store.Price[(int)g];
        return new Score { Money = p.Cash, Land = land, GoodsValue = goods };
    }

    public static void Rank(Game game)
    {
        foreach (var p in game.Players) p.Score = Of(game, p);
        var tiebreak = game.Players.Select(_ => game.Rng.Next(1 << 30)).ToArray();
        var order = game.Players
            .OrderByDescending(p => p.Score.Total)
            .ThenByDescending(p => p.Cash)
            .ThenBy(p => tiebreak[p.Seat])
            .ToList();
        for (int i = 0; i < order.Count; i++) order[i].Rank = i;
    }

    public static void Run(Game game)
    {
        Rank(game);
        foreach (var p in game.Players) game.Emit(new ScoreEvent(game.Round, p.Seat, p.Score.Total, p.Rank));
    }

    public static int Colony(Game game) => game.Players.Sum(p => p.Score.Total);

    /// <summary>J6: the colony verdict; thresholds scale with the map and the game length.</summary>
    public static int Rating(Game game, int colony)
    {
        var s = game.Settings.Scoring;
        double scale = game.PlotScale * game.Rounds / 12.0;
        if (colony < s.ColonyMinimum * scale) return 0;
        return Math.Min(s.MaxRating, (int)Math.Round(colony / (s.ColonyStep * scale), MidpointRounding.ToZero));
    }

    public static void End(Game game)
    {
        int colony = Colony(game);
        int rating = Rating(game, colony);
        var s = game.Settings.Scoring;
        var winner = game.Players.First(p => p.Rank == 0);
        game.Emit(new GameOverEvent(game.Round, colony, rating, s.Verdicts[Math.Min(rating, s.Verdicts.Length - 1)], winner.Seat, rating >= s.FounderRating));
    }
}
