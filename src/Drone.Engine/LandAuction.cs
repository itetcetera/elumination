namespace Drone.Engine;

/// <summary>
/// A land auction for one plot: live by default (bidders climb a price scale against a clock), or
/// sealed second-price as a host option (E1). Player sales come first each round (plots marked
/// last round), then the store's plots.
/// </summary>
public sealed class LandAuction
{
    public required Plot Plot { get; init; }
    /// <summary>Seat of the seller, or -1 for the store.</summary>
    public required int Seller { get; init; }
    public int StartPrice { get; set; }
    /// <summary>Each player's bid position (a price). At or above the start price it is a bid.</summary>
    public required int[] Position { get; init; }
    public int HighBidder { get; private set; } = -1;
    public int HighBid { get; private set; }
    /// <summary>Clock left, as a share of the full clock.</summary>
    public double Timer { get; set; } = 1.0;
    public int[] Cooldown { get; init; } = [];
    /// <summary>When each bidder last raised their bid (first to a price holds it).</summary>
    public long[] ReachedAt { get; init; } = [];

    public bool IsStore => Seller < 0;
    public int Bottom(Settings s) => StartPrice - s.Land.BottomBelowStart;

    public static int Step(Settings s, int price)
    {
        int step = s.Land.BidSteps[0];
        for (int i = 0; i < s.Land.BidStepThresholds.Length; i++)
            if (price >= s.Land.BidStepThresholds[i]) step = s.Land.BidSteps[i];
        return step;
    }

    /// <summary>The most a player can bid: their cash, less any species land surcharge.</summary>
    public static int MaxBid(Player p) => p.Cash - p.Species.LandSurcharge;

    public static int PricePaid(Player p, int bid) => bid + p.Species.LandSurcharge;

    public static IEnumerable<int> RunPlayerSales(Game game)
    {
        var marked = game.MarkedForSale.ToList();
        game.MarkedForSale.Clear();
        foreach (int idx in marked)
        {
            var plot = game.Map.Plots[idx];
            plot.ForSale = false;
            if (plot.Owner < 0) continue;
            foreach (var w in RunOne(game, plot, plot.Owner, game.Store.LandPrice)) yield return w;
        }
    }

    public static IEnumerable<int> RunStoreSales(Game game)
    {
        var s = game.Settings.Land;
        int free = game.Map.FreePlots.Count();
        int count = Math.Min(free, (int)Math.Round(s.StorePlotsPer44Plots * game.PlotScale) + game.Rng.RoundNormal(s.StorePlotsSpread));
        int skip = 0;
        for (int i = 0; i < count; i++)
        {
            var options = game.Map.FreePlots.ToList();
            if (options.Count == 0) break;
            var plot = game.Rng.Pick(options);
            if (skip > 0) { skip--; continue; }
            int start = game.Store.LandPrice;
            bool sold = false;
            foreach (var w in RunOne(game, plot, -1, start, r => sold = r)) yield return w;
            if (!sold) skip = s.SkipAfterUnsold;
        }
    }

    static IEnumerable<int> RunOne(Game game, Plot plot, int seller, int start, Action<bool>? done = null)
    {
        var s = game.Settings;
        int n = game.Players.Count;
        var a = new LandAuction
        {
            Plot = plot,
            Seller = seller,
            StartPrice = start,
            Position = Enumerable.Repeat(start - s.Land.BottomBelowStart, n).ToArray(),
            Cooldown = new int[n],
            ReachedAt = new long[n],
        };
        game.Phase = a;
        var bidders = game.Players.Where(p => p.Seat != seller).ToList();
        if (s.Game.LandAuctionMode == LandAuctionMode.Sealed)
            foreach (var w in a.Sealed(game, bidders)) yield return w;
        else
            foreach (var w in a.Live(game, bidders)) yield return w;
        a.Finish(game);
        done?.Invoke(a.HighBidder >= 0);
        game.Phase = null;
    }

    IEnumerable<int> Live(Game game, List<Player> bidders)
    {
        var s = game.Settings.Land;
        int stepTicks = Math.Max(1, game.Ticks(s.BidStepSeconds));
        double slow = 1.0 / Math.Max(1, game.Ticks(s.TimerSlowSeconds));
        double fast = 1.0 / Math.Max(1, game.Ticks(s.TimerFastSeconds));
        for (long t = 0; Timer > 0; t++)
        {
            bool moving = false;
            if (!IsStore)
            {
                var seller = game.Players[Seller];
                var m = game.Seats[Seller].LandSell(game, seller, this);
                if (m != Move.Hold && Cooldown[Seller] == 0)
                {
                    int next = StartPrice + (int)m * s.SellerStep;
                    StartPrice = Math.Max(s.MinPrice, next);
                    Cooldown[Seller] = stepTicks;
                }
            }
            foreach (var p in bidders)
            {
                var m = game.Seats[p.Seat].LandBid(game, p, this);
                if (m != Move.Hold) moving = true;
                if (m == Move.Hold || Cooldown[p.Seat] > 0) continue;
                int pos = Position[p.Seat];
                if (m == Move.Up)
                {
                    int next = Math.Max(pos, Bottom(game.Settings)) + Step(game.Settings, Math.Max(pos, StartPrice));
                    if (next <= MaxBid(p)) { Position[p.Seat] = next; ReachedAt[p.Seat] = t; }
                }
                else Position[p.Seat] = Math.Max(Bottom(game.Settings), pos - Step(game.Settings, pos));
                Cooldown[p.Seat] = stepTicks;
            }
            for (int i = 0; i < Cooldown.Length; i++) if (Cooldown[i] > 0) Cooldown[i]--;
            UpdateHigh(bidders);
            Timer -= IsStore && !moving ? fast : slow;
            yield return 1;
        }
    }

    void UpdateHigh(List<Player> bidders)
    {
        // First to a price holds it: an equal bid never takes the lead (E8).
        int best = -1, bestBid = 0;
        foreach (var p in bidders)
        {
            int bid = Position[p.Seat];
            if (bid < StartPrice) continue;
            if (best < 0 || bid > bestBid || (bid == bestBid && ReachedAt[p.Seat] < ReachedAt[best]))
            {
                best = p.Seat;
                bestBid = bid;
            }
        }
        HighBidder = best;
        HighBid = best < 0 ? 0 : bestBid;
    }

    IEnumerable<int> Sealed(Game game, List<Player> bidders)
    {
        var bids = new int?[game.Players.Count];
        long deadline = game.Ticks(game.Settings.Land.SealedSeconds);
        for (long t = 0; ; t++)
        {
            foreach (var p in bidders)
                if (bids[p.Seat] == null) bids[p.Seat] = game.Seats[p.Seat].SealedBid(game, p, this);
            if (bidders.All(p => bids[p.Seat] != null) || t >= deadline) break;
            yield return 1;
        }
        // Highest bid at or above the start price wins and pays the second-highest (or the start price).
        var valid = bidders
            .Select(p => (p, bid: Math.Min(bids[p.Seat] ?? 0, MaxBid(p))))
            .Where(x => x.bid >= StartPrice)
            .OrderByDescending(x => x.bid).ThenBy(_ => game.Rng.Next(1 << 30))
            .ToList();
        if (valid.Count == 0) yield break;
        HighBidder = valid[0].p.Seat;
        HighBid = valid.Count > 1 ? Math.Max(StartPrice, valid[1].bid) : StartPrice;
        yield return 1;
    }

    void Finish(Game game)
    {
        var s = game.Settings.Land;
        if (HighBidder >= 0)
        {
            var buyer = game.Players[HighBidder];
            int paid = Math.Min(buyer.Cash, PricePaid(buyer, HighBid));
            buyer.Cash -= paid;
            if (!IsStore) game.Players[Seller].Cash += HighBid;
            Plot.Owner = buyer.Seat;
            game.Emit(new LandSoldEvent(game.Round, Plot.Index, Seller, buyer.Seat, HighBid, IsStore));
            if (IsStore)
            {
                game.StoreSalePrices.Add(HighBid);
                game.Store.LandPrice = Math.Max(s.MinPrice, HighBid - s.SoldDrop);
            }
        }
        else
        {
            game.Emit(new LandUnsoldEvent(game.Round, Plot.Index, Seller, StartPrice));
            if (IsStore) game.Store.LandPrice = Math.Max(s.MinPrice, StartPrice / 2 - s.UnsoldDrop);
        }
    }
}
