namespace Drone.Engine;

/// <summary>Something a player does in their development turn. Each costs turn time.</summary>
public abstract record TurnCommand;
/// <summary>Walk on the map to a plot (leaving town first if needed). A human client sends one hex
/// at a time; the CPU sends its destination.</summary>
public sealed record WalkTo(int Plot) : TurnCommand;
/// <summary>Walk into a town building: the store, or an outpost (buy and outfit only).</summary>
public sealed record GoToTown(int Plot) : TurnCommand;
public sealed record BuyDrone : TurnCommand;
public sealed record OutfitDrone(Good Good) : TurnCommand;
/// <summary>Walk the led drone back into the corral: full drone price back (F6).</summary>
public sealed record SellDrone : TurnCommand;
/// <summary>The button on a plot: install, swap or pick up on your own plot; elsewhere a led drone
/// runs away (F8).</summary>
public sealed record PressButton : TurnCommand;
/// <summary>Let the led drone go on purpose (F8): it runs off and is lost.</summary>
public sealed record ReleaseDrone : TurnCommand;
/// <summary>The whole assay round trip from the store to a plot and back (F9).</summary>
public sealed record Assay(int Plot) : TurnCommand;
/// <summary>From the menu, any time: auctioned next round (E2, E10).</summary>
public sealed record MarkForSale(int Plot) : TurnCommand;
/// <summary>The store buys the plot now at a low price (E2).</summary>
public sealed record QuickSale(int Plot) : TurnCommand;
public sealed record CatchGlimmer : TurnCommand;
public sealed record CatchCrate : TurnCommand;
public sealed record Wait(double Seconds) : TurnCommand;
/// <summary>Marks the start of a Glimmer hunt (no time; for the hunt statistics).</summary>
public sealed record HuntGlimmer : TurnCommand;
/// <summary>Gamble at the pub (in the store): ends the turn (F10).</summary>
public sealed record Pub : TurnCommand;
public sealed record EndTurn : TurnCommand;

/// <summary>
/// One player's development turn (section F). Players go one at a time, leader first (reversed when
/// the store is short of drones). Each command costs time; the turn ends when the time is gone, at
/// the pub, or when the player ends it. A drone still being led at the end is lost.
/// </summary>
public sealed class DevTurn
{
    public required Game Game { get; init; }
    public required Player Player { get; init; }
    public double Seconds { get; init; }
    public double TimeLeft { get; private set; }
    public double Elapsed => Seconds - TimeLeft;
    /// <summary>Held up by the food floor: may only outfit food drones this turn (F3).</summary>
    public bool Starving { get; init; }
    /// <summary>Where the player is: a map plot, or a town building's plot when in town.</summary>
    public Plot Location { get; private set; } = null!;
    public bool InTown { get; private set; } = true;
    public bool Leading { get; private set; }
    public Good? LeadJob { get; private set; }
    public bool Ended { get; private set; }
    public string? Message { get; private set; }
    public Glimmer Glimmer { get; init; } = null!;
    public Crate Crate { get; init; } = null!;
    public PersonalEventHappened? Event { get; init; }

    Settings S => Game.Settings;
    TurnSettings T => Game.Settings.Turn;

    public static IEnumerable<int> RunAll(Game game)
    {
        var order = game.ByRank.ToList();
        int flip = (int)Math.Round(game.Settings.Store.TurnFlipDronesPer44Plots * game.PlotScale);
        if (game.Store.Drones < flip) order.Reverse();
        foreach (var p in order)
            foreach (var w in Run(game, p)) yield return w;
    }

    public static IEnumerable<int> Run(Game game, Player p)
    {
        // F13: the personal event comes before the turn time is set.
        var ev = PersonalEvents.Roll(game, p);
        var t = game.Settings.Turn;
        int need = game.FoodNeed(p);
        int food = p.Held[(int)Good.Food];
        double share = need <= 0 || food >= need ? 1.0 : (double)food / need;
        // Starving means no food at all; the floor still gives some time, for food drones only (F3).
        bool starving = food == 0 && need > 0;
        share = Math.Max(share, t.FoodFloor);
        double seconds = t.Seconds * (1 - t.FoodScaledShare + t.FoodScaledShare * share);
        var turn = new DevTurn
        {
            Game = game,
            Player = p,
            Seconds = seconds,
            Starving = starving,
            Glimmer = Glimmer.Create(game, seconds),
            Crate = Crate.Create(game),
            Event = ev,
        };
        turn.TimeLeft = seconds;
        turn.Location = game.Map.Store;
        game.Phase = turn;
        game.Emit(new TurnEvent(game.Round, p.Seat, seconds, starving));
        while (!turn.Ended && turn.TimeLeft > 0)
        {
            var cmd = game.Seats[p.Seat].Turn(game, p, turn);
            if (cmd == null)
            {
                // The clock runs while a player thinks.
                turn.Spend(1.0 / game.Settings.Game.TicksPerSecond);
                yield return 1;
                continue;
            }
            foreach (var w in turn.Do(cmd)) yield return w;
        }
        if (turn.Leading)
        {
            // F5: a drone still being led when time runs out is lost.
            turn.Leading = false;
            turn.LeadJob = null;
            game.Emit(new TurnActionEvent(game.Round, p.Seat, "droneLost", -1, 0));
        }
        game.Phase = null;
    }

    void Spend(double s) => TimeLeft = Math.Max(0, TimeLeft - s);

    /// <summary>Spends time and yields it as ticks; false when the clock ran out first.</summary>
    IEnumerable<int> Take(double seconds, Action<bool> ok)
    {
        if (seconds > TimeLeft + 1e-9)
        {
            int left = Game.Ticks(TimeLeft);
            TimeLeft = 0;
            ok(false);
            if (left > 0) yield return left;
            yield break;
        }
        Spend(seconds);
        ok(true);
        int ticks = Game.Ticks(seconds);
        if (ticks > 0) yield return ticks;
    }

    void Log(string action, int plot = -1, int amount = 0)
        => Game.Emit(new TurnActionEvent(Game.Round, Player.Seat, action, plot, amount));

    public IEnumerable<int> Do(TurnCommand cmd)
    {
        Message = null;
        bool ok = true;
        switch (cmd)
        {
            case EndTurn:
                Ended = true;
                break;
            case Wait w:
                foreach (var x in Take(Math.Max(w.Seconds, 1.0 / S.Game.TicksPerSecond), r => ok = r)) yield return x;
                break;
            case WalkTo w:
                foreach (var x in Walk(Game.Map.Plots[w.Plot])) yield return x;
                break;
            case GoToTown g:
                foreach (var x in EnterTown(Game.Map.Plots[g.Plot])) yield return x;
                break;
            case BuyDrone:
                foreach (var x in Buy()) yield return x;
                break;
            case OutfitDrone o:
                foreach (var x in Outfit(o.Good)) yield return x;
                break;
            case SellDrone:
                foreach (var x in SellBack()) yield return x;
                break;
            case PressButton:
                foreach (var x in Press()) yield return x;
                break;
            case ReleaseDrone:
                if (Leading) { Leading = false; LeadJob = null; Log("droneReleased", Location.Index); }
                break;
            case Assay a:
                foreach (var x in DoAssay(Game.Map.Plots[a.Plot])) yield return x;
                break;
            case MarkForSale m:
                foreach (var x in Mark(Game.Map.Plots[m.Plot])) yield return x;
                break;
            case QuickSale q:
                foreach (var x in SellToStore(Game.Map.Plots[q.Plot])) yield return x;
                break;
            case CatchGlimmer:
                TryGlimmer();
                break;
            case HuntGlimmer:
                Log("glimmerHunt");
                break;
            case CatchCrate:
                TryCrate();
                break;
            case Pub:
                foreach (var x in GoToPub()) yield return x;
                break;
            default:
                throw new ArgumentException($"unknown turn command {cmd}");
        }
    }

    double StepCost(Plot to)
    {
        double s = to.Terrain switch
        {
            Terrain.Mountain => T.WalkMountainSeconds,
            Terrain.River or Terrain.Lake => T.WalkWaterSeconds,
            _ => T.WalkPlainSeconds,
        };
        return s * 100 / Player.Species.WalkSpeedPercent;
    }

    /// <summary>The quickest path between two plots (terrain slows walking).</summary>
    public List<Plot> Path(Plot from, Plot to)
    {
        var map = Game.Map;
        var dist = new double[map.Plots.Length];
        var prev = new int[map.Plots.Length];
        Array.Fill(dist, double.MaxValue);
        Array.Fill(prev, -1);
        dist[from.Index] = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(from.Index, 0);
        while (queue.TryDequeue(out int i, out double d))
        {
            if (d > dist[i]) continue;
            if (i == to.Index) break;
            foreach (var n in map.Neighbours(map.Plots[i]))
            {
                double nd = d + StepCost(n);
                if (nd < dist[n.Index]) { dist[n.Index] = nd; prev[n.Index] = i; queue.Enqueue(n.Index, nd); }
            }
        }
        var path = new List<Plot>();
        for (int i = to.Index; i != from.Index && i >= 0; i = prev[i]) path.Add(map.Plots[i]);
        path.Reverse();
        return path;
    }

    /// <summary>Seconds to walk from one plot to another.</summary>
    public double WalkSeconds(Plot from, Plot to) => Path(from, to).Sum(StepCost);

    /// <summary>Seconds from where the player is to a plot on the map.</summary>
    public double SecondsTo(Plot to) => (InTown ? T.ExitTownSeconds : 0) + WalkSeconds(Location, to);

    IEnumerable<int> Walk(Plot to)
    {
        if (Leading && LeadJob == null && InTown)
        {
            // The store will not let you out with a drone that has no job.
            Message = "Go to a building first";
            yield break;
        }
        bool ok = true;
        if (InTown)
        {
            foreach (var x in Take(T.ExitTownSeconds, r => ok = r)) yield return x;
            if (!ok) yield break;
            InTown = false;
        }
        foreach (var step in Path(Location, to))
        {
            foreach (var x in Take(StepCost(step), r => ok = r)) yield return x;
            if (!ok) yield break;
            Location = step;
        }
    }

    IEnumerable<int> EnterTown(Plot town)
    {
        if (!town.IsTown) { Message = "Not a town building"; yield break; }
        if (InTown && Location == town) yield break;
        bool ok = true;
        foreach (var x in Walk(town)) yield return x;
        if (Location != town) yield break;
        foreach (var x in Take(T.EnterTownSeconds, r => ok = r)) yield return x;
        if (ok) InTown = true;
    }

    IEnumerable<int> Buy()
    {
        if (!InTown) { Message = "Not in town"; yield break; }
        if (Leading) { Message = "Already leading a drone"; yield break; }
        int price = Game.DronePriceFor(Player);
        if (Game.Store.Drones <= 0) { Message = "No drones left"; yield break; }
        if (Player.Cash < price) { Message = "Not enough money"; yield break; }
        bool ok = true;
        foreach (var x in Take(T.CorralSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        Player.Cash -= price;
        Game.Store.Drones--;
        Leading = true;
        LeadJob = null;
        Log("buyDrone", -1, price);
    }

    IEnumerable<int> Outfit(Good good)
    {
        if (!InTown) { Message = "Not in town"; yield break; }
        if (!Leading) { Message = "Need a drone"; yield break; }
        if (good == Good.Helion && !S.Game.HelionEnabled) { Message = "No Helion outfit"; yield break; }
        if (Starving && good != Good.Food) { Message = "Starving: food drones only"; yield break; }
        int cost = S.Goods.Of(good).OutfitCost;
        // F7: re-outfitting charges in full; beginners pay only the difference.
        if (Player.Beginner && LeadJob is { } old) cost = Math.Max(0, cost - S.Goods.Of(old).OutfitCost);
        if (Player.Cash < cost) { Message = "Not enough money"; yield break; }
        bool ok = true;
        foreach (var x in Take(T.OutfitSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        Player.Cash -= cost;
        LeadJob = good;
        Log("outfit", -1, cost);
    }

    IEnumerable<int> SellBack()
    {
        if (!InTown || !Leading) { Message = "Bring the drone to the corral"; yield break; }
        bool ok = true;
        foreach (var x in Take(T.CorralSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        int price = Game.DronePriceFor(Player);
        Player.Cash += price;
        Game.Store.Drones++;
        Leading = false;
        LeadJob = null;
        Log("sellDrone", -1, price);
    }

    IEnumerable<int> Press()
    {
        if (InTown) { Message = "Not on the map"; yield break; }
        var plot = Location;
        if (plot.Owner != Player.Seat)
        {
            if (Leading)
            {
                if (Player.Beginner) { Message = "Not your plot"; yield break; }
                Leading = false;
                LeadJob = null;
                Message = "Your drone ran away";
                Log("droneRanAway", plot.Index);
            }
            else Message = "Not your plot";
            yield break;
        }
        if (Leading && LeadJob is { } job && job.IsMined() && plot.IsWater) { Message = "No mining on the water"; yield break; }
        bool ok = true;
        foreach (var x in Take(T.InstallSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        var had = plot.Drone;
        if (Leading)
        {
            plot.Drone = LeadJob;
            Log("install", plot.Index, (int)LeadJob!.Value);
            Leading = had != null;
            LeadJob = had;
        }
        else if (had != null)
        {
            plot.Drone = null;
            Leading = true;
            LeadJob = had;
            Log("pickUp", plot.Index);
        }
    }

    IEnumerable<int> DoAssay(Plot plot)
    {
        if (Leading) { Message = "No drones in the assay office"; yield break; }
        bool ok = true;
        foreach (var x in EnterTown(Game.Map.Store)) yield return x;
        if (!InTown || Location != Game.Map.Store) yield break;
        // The office, out to the plot for a sample, and (unless a beginner, who reads it at the plot) back.
        double f = 1.0 / Player.Species.AssayDivisor;
        double trip = T.AssayOfficeSeconds + T.ExitTownSeconds + WalkSeconds(Game.Map.Store, plot) + T.SampleSeconds;
        if (!Player.Beginner) trip += WalkSeconds(plot, Game.Map.Store) + T.EnterTownSeconds;
        foreach (var x in Take(trip * f, r => ok = r)) yield return x;
        if (!ok) yield break;
        if (Player.Beginner) { InTown = false; Location = plot; }
        plot.Assayed = true;
        Log("assay", plot.Index, Math.Max(0, plot.Crystite) * 10 + Math.Max(0, plot.Helion));
    }

    IEnumerable<int> Mark(Plot plot)
    {
        if (plot.Owner != Player.Seat || plot.ForSale) { Message = "Can't mark that plot"; yield break; }
        bool ok = true;
        foreach (var x in Take(T.MarkForSaleSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        plot.ForSale = true;
        Game.MarkedForSale.Add(plot.Index);
        Log("markForSale", plot.Index);
    }

    IEnumerable<int> SellToStore(Plot plot)
    {
        if (plot.Owner != Player.Seat) { Message = "Not your plot"; yield break; }
        bool ok = true;
        foreach (var x in Take(T.MarkForSaleSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        int price = QuickSalePrice(Game);
        if (plot.Drone != null)
        {
            // Assumed (design doc E2): a drone on it is sold back at the drone price.
            price += Game.Store.DronePrice;
            Game.Store.Drones++;
            plot.Drone = null;
        }
        Player.Cash += price;
        plot.Owner = -1;
        if (plot.ForSale) { plot.ForSale = false; Game.MarkedForSale.Remove(plot.Index); }
        Game.Emit(new QuickSaleEvent(Game.Round, plot.Index, Player.Seat, price));
    }

    public static int QuickSalePrice(Game game) => (int)Math.Round(game.Store.LandPrice * game.Settings.Land.QuickSaleShare);

    void TryGlimmer()
    {
        if (InTown || Leading) { Message = "No catch"; return; }
        if (!Glimmer.TryCatch(Location, Elapsed)) { Message = "Missed"; return; }
        int reward = T.GlimmerReward[Game.Band];
        Player.Cash += reward;
        Log("glimmer", Location.Index, reward);
    }

    void TryCrate()
    {
        if (InTown || Leading) { Message = "No catch"; return; }
        if (!Crate.TryCatch(Game, Location, Elapsed)) { Message = "Missed"; return; }
        if (Crate.Cash) { Player.Cash += Crate.Amount; Log("crateCash", Location.Index, Crate.Amount); }
        else
        {
            Player.Held[(int)Crate.Good] = Math.Min(S.Goods.Of(Crate.Good).MaxHeld, Player.Held[(int)Crate.Good] + Crate.Amount);
            Log("crate" + Crate.Good, Location.Index, Crate.Amount);
        }
    }

    IEnumerable<int> GoToPub()
    {
        if (Leading) { Message = "No drones in the pub"; yield break; }
        bool ok = true;
        foreach (var x in EnterTown(Game.Map.Store)) yield return x;
        if (!InTown || Location != Game.Map.Store) yield break;
        foreach (var x in Take(T.PubSeconds, r => ok = r)) yield return x;
        if (!ok) yield break;
        int win = PubPayout(Game, TimeLeft);
        Player.Cash += win;
        Log("pub", -1, win);
        Ended = true;
    }

    /// <summary>F10: random[0, k x seconds left) + round bonus, capped.</summary>
    public static int PubPayout(Game game, double timeLeft)
    {
        var t = game.Settings.Turn;
        int range = (int)(t.PubPerSecondLeft * timeLeft);
        int win = (range > 0 ? game.Rng.Next(range) : 0) + t.PubRoundBonus[game.Band];
        return win > t.PubCap ? t.PubCapTo : win;
    }

    /// <summary>The pub payout a player can expect with this much time left.</summary>
    public static double ExpectedPub(Game game, double timeLeft)
    {
        var t = game.Settings.Turn;
        double win = t.PubPerSecondLeft * timeLeft / 2 + t.PubRoundBonus[game.Band];
        return Math.Min(win, t.PubCapTo);
    }
}

/// <summary>The Glimmer (F11): a shy lizard on the mountains that shows for a moment, hides, and
/// sometimes moves. Catch it while it shows, standing on its plot with no drone. Once per turn.</summary>
public sealed class Glimmer
{
    public readonly record struct Window(double From, double To, int Plot);
    public List<Window> Shows { get; } = [];
    public bool Caught { get; private set; }

    public static Glimmer Create(Game game, double seconds)
    {
        var g = new Glimmer();
        var t = game.Settings.Turn;
        var mountains = game.Map.Plots.Where(p => p.Terrain == Terrain.Mountain).ToList();
        if (mountains.Count == 0) return g;
        var at = game.Rng.Pick(mountains);
        double time = 0;
        while (time < seconds)
        {
            double show = game.Rng.Range(t.GlimmerShowSeconds[0], t.GlimmerShowSeconds[1]);
            g.Shows.Add(new Window(time, time + show, at.Index));
            time += show + game.Rng.Range(t.GlimmerHideSeconds[0], t.GlimmerHideSeconds[1]);
            if (game.Rng.Chance(t.GlimmerMoveChance)) at = game.Rng.Pick(mountains);
        }
        return g;
    }

    /// <summary>Where it shows right now, if it does.</summary>
    public int? ShowingAt(double elapsed) => Caught ? null : Shows.FirstOrDefault(w => elapsed >= w.From && elapsed < w.To) is { To: > 0 } w ? w.Plot : null;

    public bool TryCatch(Plot at, double elapsed)
    {
        if (ShowingAt(elapsed) != at.Index) return false;
        Caught = true;
        return true;
    }
}

/// <summary>The drifting supply crate (F11): on a parachute it drifts across one row of the map,
/// one hex every few seconds. Catch it on its plot for cash or a good. Once per turn.</summary>
public sealed class Crate
{
    public int Row { get; init; }
    public bool FromLeft { get; init; }
    public double HexSeconds { get; init; }
    public bool Cash { get; init; }
    public Good Good { get; init; }
    public int Amount { get; init; }
    public bool Caught { get; private set; }

    public static Crate Create(Game game)
    {
        var t = game.Settings.Turn;
        bool cash = game.Rng.Chance(t.CrateCashChance);
        var good = game.Rng.Pick(new[] { Good.Food, Good.Energy, Good.Ore });
        return new Crate
        {
            Row = game.Rng.Next(game.Map.Rows),
            FromLeft = game.Rng.Chance(0.5),
            HexSeconds = t.CrateHexSeconds,
            Cash = cash,
            Good = good,
            Amount = cash ? t.CrateCash[game.Band] : t.CrateGoods[game.Band],
        };
    }

    /// <summary>The plot under the crate at a moment of the turn, or null once it has drifted off.</summary>
    public int? PlotAt(Map map, double elapsed)
    {
        if (Caught) return null;
        int step = (int)(elapsed / HexSeconds);
        if (step >= map.Cols) return null;
        int col = FromLeft ? step : map.Cols - 1 - step;
        return map.At(col, Row).Index;
    }

    /// <summary>When the crate is over a plot of its row: [from, to) seconds into the turn.</summary>
    public (double From, double To)? WhenOver(Map map, Plot plot)
    {
        if (Caught || plot.Row != Row) return null;
        int step = FromLeft ? plot.Col : map.Cols - 1 - plot.Col;
        return (step * HexSeconds, (step + 1) * HexSeconds);
    }

    public bool TryCatch(Game game, Plot at, double elapsed)
    {
        if (PlotAt(game.Map, elapsed) != at.Index) return false;
        Caught = true;
        return true;
    }
}
