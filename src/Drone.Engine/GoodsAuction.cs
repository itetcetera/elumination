namespace Drone.Engine;

/// <summary>
/// One good's live auction (section I), the heart of the game. Before it opens the good is used,
/// spoils and is produced. Each player takes a side; buyers climb the price scale, sellers come
/// down it; when the best buyer meets the best seller, units change hands one at a time at the
/// seller's price. The store sells at the top while it has stock and always buys at the bottom.
/// The clock stops while units move, and runs 4x faster while nobody moves.
/// </summary>
public sealed class GoodsAuction
{
    public required Good Good { get; init; }
    public required int StartPrice { get; init; }
    public required int Step { get; init; }
    public int Bottom { get; private set; }
    public int Top { get; private set; }
    public required Side[] Sides { get; init; }
    /// <summary>Each player's place on the scale (a price). Buyers wait below the bottom, sellers above the top.</summary>
    public required int[] Pos { get; init; }
    /// <summary>Food and energy: the need line a seller stops at (I12). 0 for other goods.</summary>
    public required int[] Line { get; init; }
    public double Timer { get; private set; } = 1.0;
    public int Units { get; private set; }
    public long TradeSum { get; private set; }
    /// <summary>Who is trading right now (seat; -1 = the store), or null.</summary>
    public (int Buyer, int Seller, int Price)? Trading { get; private set; }
    public bool StoreSells(Game g) => !Good.IsExport() && g.Store.Stock[(int)Good] > 0;

    readonly int[] _cooldown = new int[8];
    readonly bool[] _belowNeed = new bool[8];

    public bool Active(int seat) => Sides[seat] == Side.Buy ? Pos[seat] >= Bottom : Pos[seat] <= Top;
    public int OutBelow => Bottom - Step;
    public int OutAbove => Top + Step;

    /// <summary>Units a seller can still offer (down to the need line unless pushing on below it).</summary>
    public int Spare(Player p) => p.Held[(int)Good] - (_belowNeed[p.Seat] ? 0 : Line[p.Seat]);

    public static IEnumerable<int> Run(Game game, Good good)
    {
        var s = game.Settings;
        var gs = s.Goods.Of(good);
        Prepare(game, good);

        // I8: skip any auction with nothing to trade.
        bool any = game.Store.Stock[(int)good] > 0 && !good.IsExport() || game.Players.Any(p => p.Held[(int)good] > 0);
        int price = game.Store.Price[(int)good];
        if (!any)
        {
            game.Emit(new AuctionEvent(game.Round, good, price, price, 0, true));
            if (good.IsExport()) game.Store.Stock[(int)good] = 0;
            yield break;
        }

        int n = game.Players.Count;
        int step = gs.AuctionStep;
        int bottom = good is Good.Food or Good.Energy ? price - gs.ScaleBelow * step : price / step * step;
        var a = new GoodsAuction
        {
            Good = good,
            StartPrice = price,
            Step = step,
            Bottom = bottom,
            Top = bottom + gs.ScaleLines * step,
            Sides = new Side[n],
            Pos = new int[n],
            Line = new int[n],
        };
        foreach (var p in game.Players)
        {
            a.Line[p.Seat] = NeedLine(game, p, good);
            a.Sides[p.Seat] = DefaultSide(p, good, a.Line[p.Seat]);
        }
        game.Phase = a;

        // I9: a short window to switch side.
        var decided = new bool[n];
        long sideTicks = game.Ticks(s.Auction.SideSeconds);
        for (long t = 0; t < sideTicks; t++)
        {
            foreach (var p in game.Players)
            {
                if (decided[p.Seat]) continue;
                var side = game.Seats[p.Seat].GoodsSide(game, p, a);
                if (side != null) { a.Sides[p.Seat] = side.Value; decided[p.Seat] = true; }
            }
            if (decided.All(d => d)) break;
            yield return 1;
        }
        foreach (var p in game.Players) a.Pos[p.Seat] = a.Sides[p.Seat] == Side.Buy ? a.OutBelow : a.OutAbove;

        foreach (var w in a.Trade(game)) yield return w;

        if (a.Units > 0)
        {
            double avg = (double)a.TradeSum / a.Units;
            double w = s.Goods.TradeAverageWeight;
            game.Store.Price[(int)good] = (int)Math.Round(w * avg + (1 - w) * price);
        }
        game.Emit(new AuctionEvent(game.Round, good, price, game.Store.Price[(int)good], a.Units, false));
        // C3: exports are shipped off; the store never resells them.
        if (good.IsExport()) game.Store.Stock[(int)good] = 0;
        game.Phase = null;
    }

    /// <summary>Usage, spoilage and production before the auction (I3).</summary>
    public static void Prepare(Game game, Good good)
    {
        var gs = game.Settings.Goods.Of(good);
        foreach (var p in game.Players)
        {
            int i = (int)good;
            int used = good switch { Good.Food => game.FoodNeed(p), Good.Energy => game.EnergyNeed(p), _ => 0 };
            int held = Math.Max(0, p.Held[i] - used);
            held -= (int)(held * gs.Spoilage);
            if (gs.HoldCap > 0) held = Math.Min(held, gs.HoldCap);
            p.Held[i] = Math.Min(gs.MaxHeld, held + p.Produced[i]);
        }
    }

    public static int NeedLine(Game game, Player p, Good good) => good switch
    {
        // The last round too uses next round's need, as if the game went on.
        Good.Food => game.FoodNeed(p, game.Round + 1),
        Good.Energy => game.EnergyNeed(p) + game.Settings.Auction.EnergyLineExtra,
        _ => 0,
    };

    static Side DefaultSide(Player p, Good good, int line)
        => p.Held[(int)good] > line ? Side.Sell : Side.Buy;

    IEnumerable<int> Trade(Game game)
    {
        var s = game.Settings.Auction;
        double slow = 1.0 / Math.Max(1, game.Ticks(s.TimerSlowSeconds));
        double fast = 1.0 / Math.Max(1, game.Ticks(s.TimerFastSeconds));
        int unitTicks = 0;
        double unitSeconds = s.FirstUnitSeconds;
        while (Timer > 0)
        {
            bool moving = false;
            foreach (var p in game.Players)
            {
                var m = game.Seats[p.Seat].Goods(game, p, this);
                _belowNeed[p.Seat] = m.BelowNeed;
                if (m.Move == Move.Hold) continue;
                moving = true;
                if (_cooldown[p.Seat] > 0) continue;
                MovePlayer(game, p, m.Move);
                _cooldown[p.Seat] = Math.Max(1, game.Ticks(s.MoveSeconds * s.SpeciesMoveBase / p.Species.AuctionLines));
            }
            for (int i = 0; i < _cooldown.Length; i++) if (_cooldown[i] > 0) _cooldown[i]--;

            var match = Match(game);
            if (match != Trading)
            {
                // A new pairing starts slow; the same pairing speeds up unit by unit (I11).
                if (match is not { } mm || Trading is not { } tt || mm.Buyer != tt.Buyer || mm.Seller != tt.Seller)
                {
                    unitSeconds = s.FirstUnitSeconds;
                    unitTicks = game.Ticks(unitSeconds);
                }
                Trading = match;
            }
            if (Trading is { } trade)
            {
                if (--unitTicks <= 0)
                {
                    Exchange(game, trade);
                    unitSeconds = Math.Max(s.MinUnitSeconds, unitSeconds - s.UnitSpeedUp);
                    unitTicks = Math.Max(1, game.Ticks(unitSeconds));
                }
            }
            else Timer -= moving ? slow : fast;
            yield return 1;
        }
        Trading = null;
    }

    void MovePlayer(Game game, Player p, Move m)
    {
        int pos = Pos[p.Seat] + (int)m * Step;
        if (Sides[p.Seat] == Side.Buy)
        {
            pos = Math.Max(OutBelow, pos);
            if (pos > Top)
            {
                // The store's sell line holds buyers back while it has stock; when it runs dry a
                // buyer pushing at the top lifts the ceiling.
                if (StoreSells(game)) pos = Top;
                else { Top = pos; }
            }
        }
        else pos = Math.Clamp(pos, Bottom, OutAbove);
        Pos[p.Seat] = pos;
    }

    /// <summary>The best buyer and best seller who meet, at the seller's price (I10, I16). Ties go to
    /// the lower-ranked player; a player beats the store at the same price.</summary>
    (int Buyer, int Seller, int Price)? Match(Game game)
    {
        int bestBuyer = -1, bestSeller = -1;
        foreach (var p in game.Players.OrderByDescending(p => p.Rank))
        {
            int seat = p.Seat;
            if (Sides[seat] == Side.Buy)
            {
                if (Pos[seat] < Bottom || p.Cash < Pos[seat]) continue;
                if (bestBuyer < 0 || Pos[seat] > Pos[bestBuyer]) bestBuyer = seat;
            }
            else
            {
                if (Pos[seat] > Top || Spare(p) <= 0) continue;
                if (bestSeller < 0 || Pos[seat] < Pos[bestSeller]) bestSeller = seat;
            }
        }
        bool store = StoreSells(game);
        if (bestBuyer >= 0)
        {
            int bid = Pos[bestBuyer];
            if (bestSeller >= 0 && Pos[bestSeller] <= bid)
            {
                if (store && Top < Pos[bestSeller]) return (bestBuyer, -1, Top);
                return (bestBuyer, bestSeller, Pos[bestSeller]);
            }
            if (store && bid >= Top) return (bestBuyer, -1, Top);
        }
        if (bestSeller >= 0 && Pos[bestSeller] <= Bottom) return (-1, bestSeller, Bottom);
        return null;
    }

    void Exchange(Game game, (int Buyer, int Seller, int Price) t)
    {
        int i = (int)Good;
        if (t.Buyer >= 0)
        {
            var b = game.Players[t.Buyer];
            if (b.Cash < t.Price) return;
            b.Cash -= t.Price;
            b.Held[i]++;
            Pos[t.Buyer] = t.Price;
        }
        else game.Store.Stock[i]++;
        if (t.Seller >= 0)
        {
            var sl = game.Players[t.Seller];
            sl.Cash += t.Price;
            sl.Held[i]--;
        }
        else game.Store.Stock[i]--;
        Units++;
        TradeSum += t.Price;
        game.Emit(new TradeEvent(game.Round, Good, t.Seller, t.Buyer, t.Price));
    }
}
