namespace Drone.Engine;

/// <summary>
/// Prices before the goods auctions (I4-I6) and the store's drone building (I7). Food and energy
/// follow supply and demand; ore follows the store's stock with a random swing; exports get a fresh
/// random price each round.
/// </summary>
public static class Prices
{
    public static void BeforeAuctions(Game game)
    {
        var s = game.Settings.Goods;
        var store = game.Store;
        int next = Math.Min(game.Round + 1, game.Rounds);

        // Food: what players will have spare after this round's need and spoilage, plus production.
        int foodSupply = store.Stock[(int)Good.Food];
        double foodDemand = 0;
        foreach (var p in game.Players)
        {
            foodSupply += Math.Max(0, p.Held[(int)Good.Food] - game.FoodNeed(p)) / 2 + p.Produced[(int)Good.Food];
            foodDemand += game.FoodNeed(p, next) * s.FoodDemandPerNeed;
        }
        store.Price[(int)Good.Food] = SupplyDemand(s, s.Food, store.Price[(int)Good.Food], (int)Math.Round(foodDemand), foodSupply, null);

        int energySupply = store.Stock[(int)Good.Energy], energyDemand = s.EnergyDemandExtra;
        foreach (var p in game.Players)
        {
            int need = game.EnergyNeed(p);
            int x = Math.Max(0, p.Held[(int)Good.Energy] - need);
            energySupply += x - x / 4 + p.Produced[(int)Good.Energy];
            energyDemand += need;
        }
        var st = game.Settings.Store;
        int reserve = (int)Math.Ceiling(st.EnergyReserveDrones * game.Settings.Production.EnergyPerDrone);
        int stock = store.Stock[(int)Good.Energy];
        if (reserve > 0 && stock >= reserve)
            // The store has its reserve: the price eases off toward the floor.
            store.Price[(int)Good.Energy] = Math.Max(s.Energy.MinPrice, store.Price[(int)Good.Energy] - st.EnergyPriceDrop);
        else
            store.Price[(int)Good.Energy] = SupplyDemand(s, s.Energy, store.Price[(int)Good.Energy], energyDemand + Math.Max(0, reserve - stock), energySupply, null);

        // Ore: the store's stock (half its ore plus its drones) against the drones the colony wants.
        double scale = game.PlotScale;
        int oreSupply = store.Stock[(int)Good.Ore] / game.Settings.Store.OrePerDrone + store.Drones;
        int free = game.Map.FreePlots.Count();
        int empty = game.Map.Plots.Count(p => p.Owner >= 0 && p.Drone == null);
        int oreDemand = (int)Math.Min(Math.Min(free, s.OreDemandFreePlots * scale) + empty, s.OreDemandCap * scale);
        int ore = SupplyDemand(s, s.Ore, store.Price[(int)Good.Ore], oreDemand, oreSupply, (s.OreRatioMin, s.OreRatioMax));
        ore += s.OreSwing * game.Rng.RoundNormal();
        store.Price[(int)Good.Ore] = Math.Clamp(ore, s.OreFloorAfterSwing, s.Ore.MaxPrice);

        foreach (var g in new[] { Good.Crystite, Good.Helion })
        {
            var gs = s.Of(g);
            store.Price[(int)g] = game.Rng.Range(gs.RandomMin, gs.RandomMax);
        }
    }

    /// <summary>The original's supply-and-demand step: ratio r = 8 x demand / supply (capped, not
    /// wrapped: L3), T = P x r / 16, new price = P/4 + T + T/2, then the good's cap and minimum.</summary>
    public static int SupplyDemand(GoodsSettings s, GoodSettings g, int price, int demand, int supply, (int min, int max)? clamp)
    {
        long r = (long)s.RatioScale * demand / Math.Max(1, supply);
        r = Math.Min(r, s.RatioCap);
        if (clamp is { } c) r = Math.Clamp(r, c.min, c.max);
        long t = price * r / 16;
        long p = price / 4 + t + t / 2;
        if (p >= g.MaxPrice) p = g.MaxPrice;
        return (int)Math.Max(p, g.MinPrice);
    }

    /// <summary>I7: the store builds drones from ore up to its cap; drone price follows the ore price.</summary>
    public static void BuildDrones(Game game)
    {
        var st = game.Settings.Store;
        var store = game.Store;
        int buildable = store.Stock[(int)Good.Ore] / st.OrePerDrone;
        int missing = Math.Max(0, store.DroneCap - store.Drones);
        int n = Math.Min(buildable, missing);
        store.Stock[(int)Good.Ore] -= n * st.OrePerDrone;
        store.Drones += n;
        store.DronePrice = store.Price[(int)Good.Ore] / st.DronePriceDivisor * st.DronePriceMultiplier;
    }
}
