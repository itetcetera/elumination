namespace Drone.Engine;

/// <summary>
/// Production after the development turns (section G): terrain output, economies of scale, the
/// learning curve from neighbouring plots, a luck roll, then the energy shortage. Round events can
/// still change it before <see cref="Commit"/> hands it to the players.
/// </summary>
public sealed class Production
{
    public required Game Game { get; init; }
    /// <summary>This round's output per plot index (0 for plots without a drone).</summary>
    public required int[] Output { get; init; }

    /// <summary>A plot's base output for a job, or null when the job can't go there.</summary>
    public static int? Base(Settings s, Plot plot, Good good)
    {
        var p = s.Production;
        if (plot.IsTown) return null;
        if (good.IsMined() && plot.IsWater) return null;
        if (good == Good.Helion && !s.Game.HelionEnabled) return null;
        if (good.IsExport()) return Math.Max(0, plot.Deposit(good));
        int i = (int)good;
        return plot.Terrain switch
        {
            Terrain.Mountain => good == Good.Ore ? plot.OreUnits * p.OrePerUnit + p.MountainOreBase : p.Mountain[i],
            Terrain.River or Terrain.Lake => p.Water[i],
            _ => p.Plain[i] + (good == Good.Food && plot.Bank ? p.BankFoodBonus : 0),
        };
    }

    /// <summary>Output before luck, for a drone of a job on a plot of an owner. <paramref name="extra"/>
    /// counts drones not yet installed (the CPU's what-if).</summary>
    public static int Expected(Game game, Player owner, Plot plot, Good good, int extra = 0)
    {
        var s = game.Settings;
        if (Base(s, plot, good) is not int b) return 0;
        int same = game.PlotsOf(owner).Count(x => x.Drone == good && x != plot) + 1 + extra;
        int scale = same / s.Production.ScaleStep * s.Production.ScaleBonus;
        int learn = Learning(game, owner, plot, good);
        int v = b + scale + learn;
        v = Math.Clamp(v, 0, s.Production.MaxOutput);
        return good == Good.Food ? FoodAfterSpecies(owner, v) : v;
    }

    /// <summary>The learning curve: LearningBonus per matching neighbour of the owner's (up to
    /// LearningCap of them); food and energy need FarmNeighboursPerBonus neighbours per bonus.</summary>
    public static int Learning(Game game, Player owner, Plot plot, Good good)
    {
        var p = game.Settings.Production;
        int n = Math.Min(p.LearningCap, game.Map.Neighbours(plot).Count(x => x.Owner == owner.Seat && x.Drone == good));
        if (good is Good.Food or Good.Energy) n /= Math.Max(1, p.FarmNeighboursPerBonus);
        return n * p.LearningBonus;
    }

    /// <summary>The Prospector's cost: bigger food plots make a little less.</summary>
    public static int FoodAfterSpecies(Player owner, int v)
        => v >= owner.Species.FoodLossFrom ? Math.Max(0, v - owner.Species.FoodLoss) : v;

    public static Production Run(Game game)
    {
        var s = game.Settings;
        var output = new int[game.Map.Plots.Length];
        foreach (var plot in game.Map.Plots)
        {
            if (plot.Owner < 0 || plot.Drone is not { } job) continue;
            if (Base(s, plot, job) is not int b) continue;
            var owner = game.Players[plot.Owner];
            int same = game.PlotsOf(owner).Count(x => x.Drone == job);
            int v = b + same / s.Production.ScaleStep * s.Production.ScaleBonus
                + Learning(game, owner, plot, job)
                + game.Rng.RoundNormal(s.Production.LuckSpread);
            v = Math.Clamp(v, 0, s.Production.MaxOutput);
            if (job == Good.Food) v = FoodAfterSpecies(owner, v);
            // A plot switched to a mining job this round makes a share of its output.
            if (job.IsMined() && plot.JobBefore is { } before && before != job) v /= s.Production.MiningSwitchDivisor;
            output[plot.Index] = v;
        }
        var prod = new Production { Game = game, Output = output };

        // G5: each drone's worth of missing energy stops one producing non-energy plot, picked fairly (L2).
        foreach (var p in game.Players)
        {
            int missing = (int)Math.Ceiling((game.EnergyNeed(p) - p.Held[(int)Good.Energy]) / s.Production.EnergyPerDrone - 1e-9);
            int stopped = 0;
            for (int i = 0; i < missing; i++)
            {
                var producing = game.PlotsOf(p).Where(x => x.Drone is { } d && d != Good.Energy && output[x.Index] > 0).ToList();
                if (producing.Count == 0) break;
                output[game.Rng.Pick(producing).Index] = 0;
                stopped++;
            }
            prod.Shortage[p.Seat] = stopped;
        }
        return prod;
    }

    public int[] Shortage { get; } = new int[8];
    /// <summary>Plots making another good this round (the Drone malfunction event).</summary>
    public Dictionary<int, Good> JobOverride { get; } = [];

    /// <summary>Hands the output to the players (added to their stock before each auction).</summary>
    public void Commit()
    {
        foreach (var p in Game.Players) Array.Clear(p.Produced);
        foreach (var plot in Game.Map.Plots)
            if (plot.Owner >= 0 && plot.Drone is { } job)
                Game.Players[plot.Owner].Produced[(int)JobOverride.GetValueOrDefault(plot.Index, job)] += Output[plot.Index];
        foreach (var p in Game.Players)
            Game.Emit(new ProductionEvent(Game.Round, p.Seat, (int[])p.Produced.Clone(), Shortage[p.Seat]));
    }
}
