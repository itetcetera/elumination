namespace Drone.Engine;

/// <summary>
/// The round event after production (section H): one card a round from a deck sized to the game,
/// none in the last round. Aimed events follow the original: bad ones at the top two players.
/// </summary>
public static class RoundEvents
{
    public static void Run(Game game, Production prod)
    {
        if (game.Deck.Count == 0) return;
        int i = game.Rng.Next(game.Deck.Count);
        var kind = game.Deck[i];
        game.Deck.RemoveAt(i);
        string detail = Apply(game, prod, kind);
        game.Emit(new RoundEventHappened(game.Round, kind, detail));
    }

    static int Scaled(Game game, int per44) => Math.Max(1, (int)Math.Round(per44 * game.PlotScale));

    static bool TopTwo(Game game, Plot p) => p.Owner >= 0 && game.Players[p.Owner].Rank <= 1;

    public static string Apply(Game game, Production prod, RoundEventKind kind)
    {
        var s = game.Settings;
        var e = s.RoundEvents;
        var map = game.Map;
        var rng = game.Rng;
        int max = s.Production.MaxOutput;
        var output = prod.Output;
        switch (kind)
        {
            case RoundEventKind.Pest:
            {
                var hit = new List<int>();
                for (int k = 0; k < Scaled(game, e.PestPlotsPer44); k++)
                {
                    var options = map.Plots.Where(p => p.Drone == Good.Food && output[p.Index] > 0 && TopTwo(game, p)).ToList();
                    if (options.Count == 0) break;
                    var plot = rng.Pick(options);
                    output[plot.Index] = 0;
                    hit.Add(plot.Index);
                }
                return "plots " + string.Join(",", hit);
            }
            case RoundEventKind.Pirates:
                foreach (var p in game.Players) p.Held[(int)Good.Crystite] = 0;
                foreach (var p in map.Plots) if (p.Drone == Good.Crystite) output[p.Index] = 0;
                return "";
            case RoundEventKind.IceRaiders:
                foreach (var p in game.Players) p.Held[(int)Good.Helion] -= (int)(p.Held[(int)Good.Helion] * e.IceRaidersShare);
                return "";
            case RoundEventKind.AcidRain:
            {
                var centre = rng.Pick(map.Plots);
                foreach (var p in map.Plots)
                {
                    bool inside = map.Distance(p, centre) <= e.AcidRadius;
                    if (p.Drone == Good.Food) output[p.Index] = Math.Clamp(output[p.Index] + (inside ? e.AcidFoodIn : e.AcidFoodOut), 0, max);
                    if (p.Drone == Good.Energy) output[p.Index] = Math.Clamp(output[p.Index] + (inside ? e.AcidEnergyIn : e.AcidEnergyOut), 0, max);
                }
                return "centre " + centre.Index;
            }
            case RoundEventKind.Quake:
            {
                foreach (var p in map.Plots)
                    if (p.Drone is Good.Ore or Good.Crystite) output[p.Index] /= 2;
                if (!rng.Chance(e.QuakeShiftChance)) return "";
                var moves = map.Plots.Where(p => p.Terrain == Terrain.Mountain)
                    .SelectMany(m => map.Neighbours(m).Where(n => n.Terrain == Terrain.Plain).Select(n => (m, n))).ToList();
                if (moves.Count == 0) return "";
                var (from, to) = rng.Pick(moves);
                // H5: a mountain unit shifts; drones on both plots survive.
                from.OreUnits--;
                if (from.OreUnits <= 0) { from.Terrain = Terrain.Plain; from.OreUnits = 0; }
                to.Terrain = Terrain.Mountain;
                to.OreUnits = 1;
                to.Bank = false;
                return $"shift {from.Index}->{to.Index}";
            }
            case RoundEventKind.Sunspots:
                foreach (var p in map.Plots) if (p.Drone == Good.Energy) output[p.Index] = Math.Min(max, output[p.Index] + e.SunspotEnergy);
                return "";
            case RoundEventKind.Meteor:
            {
                var options = map.Plots.Where(p => !p.IsWater && !p.IsTown && p.Crystite >= 0 && p.Crystite <= e.MeteorMaxBefore).ToList();
                if (options.Count == 0) return "";
                var plot = rng.Pick(options);
                plot.Crystite = e.MeteorLevel;
                plot.Drone = null;
                output[plot.Index] = 0;
                return "plot " + plot.Index;
            }
            case RoundEventKind.Radiation:
            {
                var hit = new List<int>();
                for (int k = 0; k < Scaled(game, e.RadiationDronesPer44); k++)
                {
                    var options = map.Plots.Where(p => p.Drone != null && TopTwo(game, p)).ToList();
                    if (options.Count == 0) break;
                    var plot = rng.Pick(options);
                    plot.Drone = null;
                    output[plot.Index] = 0;
                    hit.Add(plot.Index);
                }
                return "plots " + string.Join(",", hit);
            }
            case RoundEventKind.StoreFire:
                game.Store.Stock[(int)Good.Food] = game.Store.Stock[(int)Good.Energy] = game.Store.Stock[(int)Good.Ore] = 0;
                return "";
            case RoundEventKind.Comet:
            {
                var free = map.FreePlots.Where(p => !p.IsWater).ToList();
                if (free.Count == 0) return "";
                var plot = rng.Pick(free);
                map.RaiseAround(plot, s.Map.SeedLevel, helion: true);
                return "plot " + plot.Index;
            }
            case RoundEventKind.SupplyShip:
                game.Store.Stock[(int)Good.Food] += e.SupplyShipFood;
                game.Store.Stock[(int)Good.Energy] += e.SupplyShipEnergy;
                return "";
            case RoundEventKind.Malfunction:
            {
                var options = map.Plots.Where(p => p.Drone != null && TopTwo(game, p)).ToList();
                if (options.Count == 0) return "";
                var plot = rng.Pick(options);
                var others = Goods.All.Where(g => g != plot.Drone && Production.Base(s, plot, g) != null).ToList();
                if (others.Count == 0) return "";
                var other = rng.Pick(others);
                prod.JobOverride[plot.Index] = other;
                output[plot.Index] = Math.Clamp(Production.Base(s, plot, other)!.Value + rng.RoundNormal(s.Production.LuckSpread), 0, max);
                return $"plot {plot.Index} makes {other}";
            }
        }
        return "";
    }
}
