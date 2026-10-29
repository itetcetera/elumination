namespace Drone.Engine.Cpu;

/// <summary>
/// DRONE's computer player (K1), written fresh for DRONE's rules. It also plays every balance
/// simulation, so it aims to be a solid, readable baseline rather than a clever one: it values each
/// job on each plot by expected output times what a unit is worth to it, buys land below that
/// worth, keeps itself fed and powered, and trades toward a limit price as the auction clock runs.
/// Every number it uses lives in the "cpu" block of the settings.
/// </summary>
public sealed class CpuSeat : ISeat
{
    // ---------- What things are worth to this player ----------

    /// <summary>What one unit of a good is worth to the player this round, with bonuses when short.</summary>
    public static double UnitValue(Game game, Player me, Good good)
    {
        var s = game.Settings;
        double price = good.IsExport()
            ? (s.Goods.Of(good).RandomMin + s.Goods.Of(good).RandomMax) / 2.0
            : game.Store.Price[(int)good];
        if (game.Round <= 1 && !good.IsExport()) price = s.Goods.Of(good).StartPrice;
        var c = s.Cpu;
        if (good == Good.Food)
        {
            int prod = ProducedEstimate(game, me, Good.Food);
            if (me.Held[(int)Good.Food] + prod < game.FoodNeed(me, Math.Min(game.Round + 1, game.Rounds))) price += c.ShortBonus;
            else if (prod == 0) price += c.NoneYetBonus;
        }
        else if (good == Good.Energy)
        {
            int prod = ProducedEstimate(game, me, Good.Energy);
            if (me.Held[(int)Good.Energy] + prod < game.EnergyNeed(me) + s.Auction.EnergyLineExtra) price += c.ShortBonus;
            else if (prod == 0) price += c.NoneYetBonus;
        }
        return price;
    }

    static int ProducedEstimate(Game game, Player me, Good good)
        => game.PlotsOf(me).Where(p => p.Drone == good).Sum(p => Expected(game, me, p, good));

    /// <summary>The deposit level the player can count on: the true level once assayed (results are
    /// public, F9), otherwise what nearby assays prove (a seed drops 1 per hex).</summary>
    public static int KnownDeposit(Game game, Plot plot, Good good)
    {
        if (plot.Deposit(good) < 0) return 0;
        if (plot.Assayed) return plot.Deposit(good);
        int best = 0;
        foreach (var q in game.Map.Plots)
            if (q.Assayed && q.Deposit(good) > 0 && game.Map.Distance(plot, q) <= game.Settings.Map.SeedRadius)
                best = Math.Max(best, q.Deposit(good) - game.Map.Distance(plot, q) * game.Settings.Map.SeedDropPerHex);
        return best;
    }

    /// <summary>Expected output of a job on a plot (no luck), using known deposits only.</summary>
    public static int Expected(Game game, Player me, Plot plot, Good good, bool adding = false)
    {
        var s = game.Settings;
        int? b = Production.Base(s, plot, good);
        if (b == null) return 0;
        if (good.IsExport()) b = KnownDeposit(game, plot, good);
        if (good.IsExport() && b == 0) return 0;
        int same = game.PlotsOf(me).Count(x => x.Drone == good && x != plot) + 1;
        int learn = Production.Learning(game, me, plot, good);
        int v = Math.Clamp(b.Value + same / s.Production.ScaleStep * s.Production.ScaleBonus + learn, 0, s.Production.MaxOutput);
        return good == Good.Food ? Production.FoodAfterSpecies(me, v) : v;
    }

    /// <summary>The best job for a plot and its value per round.</summary>
    public static (Good Good, double Value) BestJob(Game game, Player me, Plot plot, bool foodOnly = false)
    {
        var best = (Good: Good.Food, Value: 0.0);
        foreach (var g in Engine.Goods.All)
        {
            if (foodOnly && g != Good.Food) continue;
            if (g == Good.Helion && !game.Settings.Game.HelionEnabled) continue;
            if (Production.Base(game.Settings, plot, g) == null) continue;
            double v = Expected(game, me, plot, g) * UnitValue(game, me, g) - EnergyCost(game, me, g);
            if (v > best.Value) best = (g, v);
        }
        return best;
    }

    /// <summary>What running a drone of this job costs in energy each round.</summary>
    public static double EnergyCost(Game game, Player me, Good job)
        => job == Good.Energy ? 0 : (game.Settings.Production.EnergyPerDrone
            - (me.Species.FreeEnergyEvery > 0 ? (double)me.Species.FreeEnergy / me.Species.FreeEnergyEvery : 0)) * UnitValue(game, me, Good.Energy);

    // ---------- Land draft ----------

    public IReadOnlyList<int>? Draft(Game game, Player me)
    {
        double pen = game.Settings.Cpu.DistancePenalty;
        return game.Map.FreePlots
            .Select(p => (p, score: BestJob(game, me, p).Value - pen * game.Settings.Cpu.DraftDistanceCost * game.Map.TownDistance(p) + game.CpuRng.NextDouble()))
            .OrderByDescending(x => x.score)
            .Take(game.Players.Count)
            .Select(x => x.p.Index)
            .ToList();
    }

    // ---------- Land auctions ----------

    readonly Dictionary<LandAuction, int> _budgets = [];

    /// <summary>What a plot is worth to the player: its score value plus what it will make in the
    /// rounds left, less cash the player keeps back.</summary>
    public int LandBudget(Game game, Player me, LandAuction a)
    {
        if (_budgets.TryGetValue(a, out int b)) return b;
        var c = game.Settings.Cpu;
        int roundsLeft = game.Rounds - game.Round + 1;
        var job = BestJob(game, me, a.Plot);
        double worth = game.Settings.Scoring.PlotValue + job.Value * roundsLeft * c.LandHorizonShare;
        int develop = game.DronePriceFor(me) + game.Settings.Goods.Of(job.Good).OutfitCost;
        int extra = game.CpuRng.Next(Math.Max(1, c.LandReserveRandom));
        // No free money to develop it: a plot without a drone only scores its flat value.
        if (me.Cash - extra < develop) worth = Math.Min(worth, game.Settings.Scoring.PlotValue * 0.8);
        // Usually it keeps enough to put a drone on the plot (and a little more); sometimes it overreaches.
        int keep = game.CpuRng.Chance(c.LandOverreachChance) ? extra / 2 : develop + c.LandReserve + extra;
        b = (int)Math.Min(LandAuction.MaxBid(me) - keep, worth);
        _budgets[a] = b;
        return b;
    }

    public Move LandBid(Game game, Player me, LandAuction a)
    {
        int budget = LandBudget(game, me, a);
        if (a.HighBidder == me.Seat) return Move.Hold;
        int pos = a.Position[me.Seat];
        int target = Math.Max(a.StartPrice, a.HighBid + 1);
        if (target > budget) return pos > a.Bottom(game.Settings) && pos > budget ? Move.Down : Move.Hold;
        int next = Math.Max(pos, a.Bottom(game.Settings)) + LandAuction.Step(game.Settings, Math.Max(pos, a.StartPrice));
        return next <= budget || pos < a.StartPrice ? Move.Up : Move.Hold;
    }

    public Move LandSell(Game game, Player me, LandAuction a) => Move.Hold;

    public int? SealedBid(Game game, Player me, LandAuction a)
    {
        int budget = LandBudget(game, me, a);
        return budget >= a.StartPrice ? budget : 0;
    }

    // ---------- Development turn ----------

    DevTurn? _turn;
    readonly Queue<TurnCommand> _plan = new();
    readonly HashSet<int> _tried = [];
    int _commands;
    int _crateTarget = -1;
    double _huntUntil = -1;

    public TurnCommand? Turn(Game game, Player me, DevTurn t)
    {
        if (_turn != t)
        {
            _turn = t;
            _plan.Clear();
            _tried.Clear();
            _commands = 0;
            _crateTarget = -1;
            _huntUntil = -1;
        }
        else if (t.Message != null)
        {
            // The last step failed: drop the plan.
            _plan.Clear();
        }
        if (++_commands > 80) return new EndTurn();
        if (_plan.Count > 0) return _plan.Dequeue();
        if (t.Leading) return t.InTown ? new SellDrone() : new ReleaseDrone();
        if (PlanJob(game, me, t)) return _plan.Dequeue();
        if (PlanAssay(game, me, t)) return _plan.Dequeue();
        if (PlanCrate(game, me, t)) return _plan.Dequeue();
        if (HuntGlimmer(game, me, t) is { } hunt) return hunt;
        return new Pub();
    }

    double TownTrip(Game game, DevTurn t, Plot town)
    {
        var s = game.Settings.Turn;
        return t.InTown && t.Location == town ? 0 : (t.InTown ? s.ExitTownSeconds : 0) + t.WalkSeconds(t.Location, town) + s.EnterTownSeconds;
    }

    bool PlanJob(Game game, Player me, DevTurn t)
    {
        var c = game.Settings.Cpu;
        var s = game.Settings.Turn;
        if (t.TimeLeft < c.MinJobSeconds) return false;
        int dronePrice = game.DronePriceFor(me);
        (Plot plot, Good job, double gain, double time, bool empty)? best = null;
        // Like a sensible player, it keeps its own drones making enough food and energy for what it
        // runs (give or take CoverSlack units it expects to buy) before anything else.
        Good? must = null;
        if (!t.Starving)
        {
            int next = Math.Min(game.Round + 1, game.Rounds);
            if (c.CoverFood && ProducedEstimate(game, me, Good.Food) + c.CoverSlack < game.FoodNeed(me, next)) must = Good.Food;
            else if (c.CoverEnergy && ProducedEstimate(game, me, Good.Energy) + c.CoverSlack < game.EnergyNeed(me)) must = Good.Energy;
        }
        foreach (var plot in game.PlotsOf(me))
        foreach (var job in Engine.Goods.All)
        {
            if (_tried.Contains(plot.Index) || plot.Drone == job) continue;
            if (t.Starving && job != Good.Food) continue;
            if (must is { } m && (job != m || plot.Drone == Good.Food || plot.Drone == Good.Energy)) continue;
            if (job == Good.Helion && !game.Settings.Game.HelionEnabled) continue;
            if (Production.Base(game.Settings, plot, job) == null) continue;
            double value = Expected(game, me, plot, job) * UnitValue(game, me, job) - EnergyCost(game, me, job);
            double current = plot.Drone is { } d ? Expected(game, me, plot, d) * UnitValue(game, me, d) - EnergyCost(game, me, d) : 0;
            // Switching a drone to mining costs part of this round's output, spread over the rounds left.
            if (plot.Drone != null && job.IsMined())
                value -= value * (1 - 1.0 / game.Settings.Production.MiningSwitchDivisor) / Math.Max(1, game.Rounds - game.Round + 1);
            int outfit = game.Settings.Goods.Of(job).OutfitCost;
            double gainUnits = (value - current) / Math.Max(1, UnitValue(game, me, job));
            if (must == null && (c.MinJobValue > 0 ? value - current < c.MinJobValue : gainUnits < c.MinJobGain)) continue;
            var town = game.Map.NearestTown(plot);
            double time;
            bool empty = plot.Drone == null;
            if (empty)
            {
                if (game.Store.Drones <= 0 || me.Cash < dronePrice + outfit) continue;
                time = TownTrip(game, t, town) + s.CorralSeconds + s.OutfitSeconds + s.ExitTownSeconds + t.WalkSeconds(town, plot) + s.InstallSeconds;
            }
            else
            {
                if (me.Cash < outfit) continue;
                time = (t.InTown ? s.ExitTownSeconds : 0) + t.WalkSeconds(t.Location, plot) + s.InstallSeconds
                    + t.WalkSeconds(plot, town) + s.EnterTownSeconds + s.OutfitSeconds + s.ExitTownSeconds + t.WalkSeconds(town, plot) + s.InstallSeconds;
            }
            if (time > t.TimeLeft - 0.5) continue;
            double gain = value - current;
            if (best == null || gain > best.Value.gain) best = (plot, job, gain, time, empty);
        }
        if (best is not { } b) return false;
        _tried.Add(b.plot.Index);
        var nearest = game.Map.NearestTown(b.plot);
        if (b.empty)
        {
            _plan.Enqueue(new GoToTown(nearest.Index));
            _plan.Enqueue(new BuyDrone());
        }
        else
        {
            _plan.Enqueue(new WalkTo(b.plot.Index));
            _plan.Enqueue(new PressButton());
            _plan.Enqueue(new GoToTown(nearest.Index));
        }
        _plan.Enqueue(new OutfitDrone(b.job));
        _plan.Enqueue(new WalkTo(b.plot.Index));
        _plan.Enqueue(new PressButton());
        return true;
    }

    bool PlanAssay(Game game, Player me, DevTurn t)
    {
        var c = game.Settings.Cpu;
        if (t.TimeLeft < c.MinAssaySeconds || t.Leading) return false;
        int high = game.Map.Plots.Count(p => p.Assayed && (p.Crystite >= game.Settings.Map.SeedLevel || p.Helion >= game.Settings.Map.SeedLevel) && (p.Owner == me.Seat || p.Free));
        if (high >= c.AssayStopAtHigh) return false;
        var s = game.Settings.Turn;
        var store = game.Map.Store;
        var options = game.Map.Plots
            .Where(p => !p.Assayed && !p.IsWater && !p.IsTown && (p.Owner == me.Seat || p.Free))
            .Select(p => (p, est: Math.Max(KnownDeposit(game, p, Good.Crystite), KnownDeposit(game, p, Good.Helion)) + (p.Owner == me.Seat ? 0.5 : 0) + game.CpuRng.NextDouble() * 0.4))
            .OrderByDescending(x => x.est)
            .ToList();
        foreach (var (p, _) in options)
        {
            double trip = (s.AssayOfficeSeconds + s.ExitTownSeconds + 2 * t.WalkSeconds(store, p) + s.SampleSeconds + s.EnterTownSeconds) / me.Species.AssayDivisor
                + TownTrip(game, t, store);
            if (trip > t.TimeLeft - 1) continue;
            _plan.Enqueue(new Assay(p.Index));
            return true;
        }
        return false;
    }

    /// <summary>Hunts the Glimmer like a player would: it only sees where it shows right now, walks
    /// there after a moment's reaction, and waits on that mountain in case it shows there again.</summary>
    TurnCommand? HuntGlimmer(Game game, Player me, DevTurn t)
    {
        var c = game.Settings.Cpu;
        var s = game.Settings.Turn;
        if (!c.HuntSideRewards || t.Leading || t.Glimmer.Caught || t.Glimmer.At < 0) return null;
        double pubTrip = TownTrip(game, t, game.Map.Store) + s.PubSeconds;
        if (_huntUntil < 0)
        {
            // Worth it when half the reward beats what the hunting time would cost at the pub.
            double hunt = Math.Min(c.GlimmerHuntSeconds, t.TimeLeft - pubTrip - 2);
            if (hunt < 3) return null;
            double pubLoss = DevTurn.ExpectedPub(game, t.TimeLeft - pubTrip) - DevTurn.ExpectedPub(game, t.TimeLeft - pubTrip - hunt);
            if (s.GlimmerReward[game.Band] / 2.0 <= pubLoss) return null;
            _huntUntil = t.Elapsed + hunt;
            return new HuntGlimmer();
        }
        if (t.Elapsed >= _huntUntil || t.TimeLeft - pubTrip < 2) return null;
        if (t.Glimmer.Showing is { } at)
        {
            _plan.Enqueue(new WalkTo(at));
            _commands--;
            return new Wait(c.GlimmerReactSeconds);
        }
        // It won't show while we stand in the mountains: step off to watch.
        if (!t.InTown && t.Location.Terrain == Terrain.Mountain
            && game.Map.Neighbours(t.Location).Where(n => n.Terrain is Terrain.Plain).MinBy(n => n.Index) is { } off)
            return new WalkTo(off.Index);
        _commands--;
        return new Wait(0.1);
    }

    bool PlanCrate(Game game, Player me, DevTurn t)
    {
        if (!game.Settings.Cpu.HuntSideRewards || t.Leading || t.Crate.Caught || _crateTarget >= 0) return false;
        var crate = t.Crate;
        double value = crate.Cash ? crate.Amount : crate.Amount * UnitValue(game, me, crate.Good);
        var s = game.Settings.Turn;
        var store = game.Map.Store;
        foreach (var plot in game.Map.Plots.Where(p => p.Row == crate.Row && !p.IsTown))
        {
            if (crate.WhenOver(game.Map, plot) is not { } w) continue;
            double arrive = t.Elapsed + t.SecondsTo(plot);
            if (arrive >= w.To - 0.05) continue;
            double waitUntil = Math.Max(arrive, w.From) + 0.02;
            double afterCatch = t.Seconds - waitUntil - t.WalkSeconds(plot, store) - s.EnterTownSeconds - s.PubSeconds;
            if (afterCatch < 0) continue;
            double withCrate = value + DevTurn.ExpectedPub(game, afterCatch);
            double pubNow = DevTurn.ExpectedPub(game, t.TimeLeft - TownTrip(game, t, store) - s.PubSeconds);
            if (withCrate <= pubNow) continue;
            _crateTarget = plot.Index;
            _plan.Enqueue(new WalkTo(plot.Index));
            double wait = waitUntil - arrive;
            if (wait > 0) _plan.Enqueue(new Wait(wait));
            _plan.Enqueue(new CatchCrate());
            return true;
        }
        return false;
    }

    // ---------- Goods auctions ----------

    sealed class GoodsPlan
    {
        public Side Side;
        public int Units;
        public int Limit;
        public int Start;
    }

    readonly Dictionary<GoodsAuction, GoodsPlan> _goods = [];

    GoodsPlan Plan(Game game, Player me, GoodsAuction a)
    {
        if (_goods.TryGetValue(a, out var plan)) return plan;
        if (_goods.Count > 16) _goods.Clear();
        var c = game.Settings.Cpu;
        int held = me.Held[(int)a.Good];
        int line = a.Line[me.Seat];
        plan = new GoodsPlan();
        if (game.LastRound)
        {
            // Held goods score at the current price: nothing left to gain by trading in the last round.
            plan.Side = held > line ? Side.Sell : Side.Buy;
            plan.Units = 0;
        }
        else if (a.Good is Good.Food or Good.Energy)
        {
            // Energy: enough for the drones it has plus the 1 or 2 it plans to add next turn.
            int want = a.Good == Good.Energy
                ? game.EnergyNeed(me) + (int)Math.Ceiling(game.CpuRng.Pick(c.EnergyPlanDrones) * game.Settings.Production.EnergyPerDrone)
                : line;
            if (held > want)
            {
                plan.Side = Side.Sell;
                plan.Units = held - want;
                plan.Limit = Math.Max(a.Bottom, (int)Math.Round(a.StartPrice * (1 - c.SellerMinUnder)));
            }
            else
            {
                plan.Side = Side.Buy;
                plan.Units = want - held;
                // Sometimes it buys the store out, to keep energy from the others.
                if (a.Good == Good.Energy && game.CpuRng.Chance(c.EnergyCornerChance)) plan.Units += game.Store.Stock[(int)Good.Energy];
                int budget = me.Cash - game.DronePriceFor(me) - c.GoodsReserveExtra;
                if (a.Good == Good.Food && held == 0) budget = me.Cash;
                int limit = (int)Math.Round(a.StartPrice * (1 + c.BuyerMaxOver));
                // What it needs is worth the store's price when the store has stock.
                if (a.StoreSells(game)) limit = Math.Max(limit, a.Top);
                plan.Limit = Math.Min(limit, Math.Max(0, budget));
                if (plan.Limit < a.Bottom) plan.Units = 0;
            }
        }
        else
        {
            // Ore and exports: sell them all at a good price (the store buys at the bottom). At a low
            // price (exports in the bottom of their band, often; ore below its sell-all price) hold
            // them, selling only enough to bring cash up to a target, as the original did with $1000.
            var gs = game.Settings.Goods.Of(a.Good);
            bool low = a.Good.IsExport()
                ? a.StartPrice < gs.RandomMin + c.ExportHoardBelow * (gs.RandomMax - gs.RandomMin) && game.CpuRng.Chance(c.ExportHoardChance)
                : a.StartPrice < c.OreSellAllFrom;
            int units = held;
            if (low)
            {
                int toCash = me.Cash >= c.SellToCash ? 0 : (int)Math.Ceiling((c.SellToCash - me.Cash) / (double)Math.Max(1, a.Bottom));
                int overCap = gs.HoldCap > 0 ? Math.Max(0, held - gs.HoldCap) : 0;
                units = Math.Min(held, Math.Max(toCash, overCap));
            }
            plan.Side = units > 0 ? Side.Sell : Side.Buy;
            plan.Units = units;
            plan.Limit = a.Bottom;
        }
        plan.Start = plan.Side == Side.Buy ? me.Held[(int)a.Good] : me.Held[(int)a.Good];
        _goods[a] = plan;
        return plan;
    }

    public Side? GoodsSide(Game game, Player me, GoodsAuction a) => Plan(game, me, a).Side;

    public GoodsMove Goods(Game game, Player me, GoodsAuction a)
    {
        var plan = Plan(game, me, a);
        var c = game.Settings.Cpu;
        int pos = a.Pos[me.Seat];
        int held = me.Held[(int)a.Good];
        double progress = Math.Min(1.0, (1 - a.Timer) / Math.Max(0.01, c.ConcedeShare));
        if (plan.Side == Side.Buy)
        {
            bool done = plan.Units <= 0 || held - plan.Start >= plan.Units;
            if (done) return new GoodsMove(pos > a.OutBelow ? Move.Down : Move.Hold);
            int target = a.Bottom + (int)Math.Round((plan.Limit - a.Bottom) * progress);
            if (a.StoreSells(game)) target = Math.Min(target, a.Top);
            if (pos < target) return new GoodsMove(Move.Up);
            if (pos > plan.Limit) return new GoodsMove(Move.Down);
            return GoodsMove.Hold;
        }
        else
        {
            bool done = plan.Units <= 0 || plan.Start - held >= plan.Units;
            if (done) return new GoodsMove(pos < a.OutAbove ? Move.Up : Move.Hold);
            int target = a.Top - (int)Math.Round((a.Top - plan.Limit) * progress);
            if (pos > target) return new GoodsMove(Move.Down);
            if (pos < plan.Limit) return new GoodsMove(Move.Up);
            return GoodsMove.Hold;
        }
    }
}
