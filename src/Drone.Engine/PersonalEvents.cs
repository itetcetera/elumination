namespace Drone.Engine;

/// <summary>
/// Personal events at the start of a development turn (F12-F14): a chance each turn, each event at
/// most once per game, good events never to the leader, bad events never to the last two. A
/// non-leader with no food always gets the care package instead. A penalty that can't be paid is
/// skipped and stays in the pool. Species divisors shrink money and goods events (the
/// Survivor: bad ones cost half, good ones pay half); the care package and land events are whole.
/// </summary>
public static class PersonalEvents
{
    public static PersonalEventHappened? Roll(Game game, Player p)
    {
        var s = game.Settings.PersonalEvents;
        if (!game.Rng.Chance(s.ChancePerTurn)) return null;
        var unused = s.Events.Where(e => !p.EventsUsed.Contains(e.Id)).ToList();
        if (unused.Count == 0) return null;
        var ev = game.Rng.Pick(unused);
        int n = game.Players.Count;
        bool good = IsGood(game, p, ev);
        if (good && p.Rank == 0) return null;
        if (!good && p.Rank >= n - 2) return null;
        if (ev.NeedsDrone && !game.PlotsOf(p).Any(x => x.Drone != null)) return null;
        // F13: a non-leader with no food gets the care package whatever was drawn.
        if (p.Rank != 0 && p.Held[(int)Good.Food] == 0)
        {
            var care = s.Events.FirstOrDefault(e => e.Kind == PersonalEventKind.CarePackage);
            if (care != null) { ev = care; good = true; }
        }
        int amount = Apply(game, p, ev, good);
        if (amount == int.MinValue) return null;
        p.EventsUsed.Add(ev.Id);
        var happened = new PersonalEventHappened(game.Round, p.Seat, ev.Id, amount);
        game.Emit(happened);
        return happened;
    }

    /// <summary>Whether an event helps the player (decides who it may go to).</summary>
    public static bool IsGood(Game game, Player p, PersonalEvent ev) => ev.Kind switch
    {
        PersonalEventKind.CarePackage or PersonalEventKind.OreGift or PersonalEventKind.GainPlot => true,
        PersonalEventKind.FoodHalved or PersonalEventKind.LosePlot => false,
        _ => ev.Q > 0,
    };

    static int Step(Game game) => (int)Math.Floor(game.EffectiveRound) / game.Settings.PersonalEvents.RoundsPerStep + 1;

    /// <summary>Applies the event; int.MinValue when it can't happen (it stays in the pool).</summary>
    static int Apply(Game game, Player p, PersonalEvent ev, bool good)
    {
        var s = game.Settings.PersonalEvents;
        int div = Math.Max(1, good ? p.Species.GoodEventDivisor : p.Species.BadEventDivisor);
        int unit = s.Unit * Step(game);
        switch (ev.Kind)
        {
            case PersonalEventKind.CarePackage:
                p.Held[(int)Good.Food] += s.CareFood;
                p.Held[(int)Good.Energy] += s.CareEnergy;
                return s.CareFood;
            case PersonalEventKind.OreGift:
                p.Held[(int)Good.Ore] += s.OreGift / div;
                return s.OreGift / div;
            case PersonalEventKind.FoodHalved:
            {
                int lost = p.Held[(int)Good.Food] / 2 / div;
                p.Held[(int)Good.Food] -= lost;
                return -lost;
            }
            case PersonalEventKind.Cash:
                return Pay(p, ev.Q * unit / div);
            case PersonalEventKind.PerOreExportDrone:
                return Pay(p, ev.Q * unit * Count(game, p, g => g.IsMined()) / div);
            case PersonalEventKind.PerEnergyDrone:
                return Pay(p, ev.Q * unit * Count(game, p, g => g == Good.Energy) / div);
            case PersonalEventKind.PerFoodDrone:
                return Pay(p, ev.Q * unit * Count(game, p, g => g == Good.Food) / div);
            case PersonalEventKind.LosePlot:
            {
                // The highest-numbered plot with a drone goes, drone and all.
                var plot = game.PlotsOf(p).Where(x => x.Drone != null).MaxBy(x => x.Index);
                if (plot == null) return int.MinValue;
                plot.Owner = -1;
                plot.Drone = null;
                if (plot.ForSale) { plot.ForSale = false; game.MarkedForSale.Remove(plot.Index); }
                return -plot.Index - 1;
            }
            case PersonalEventKind.GainPlot:
            {
                var free = game.Map.FreePlots.ToList();
                if (free.Count == 0) return int.MinValue;
                var plot = game.Rng.Pick(free);
                plot.Owner = p.Seat;
                return plot.Index + 1;
            }
        }
        return int.MinValue;
    }

    static int Count(Game game, Player p, Func<Good, bool> job) => game.PlotsOf(p).Count(x => x.Drone is { } d && job(d));

    static int Pay(Player p, int amount)
    {
        if (amount < 0 && p.Cash < -amount) return int.MinValue;
        p.Cash += amount;
        return amount;
    }
}
