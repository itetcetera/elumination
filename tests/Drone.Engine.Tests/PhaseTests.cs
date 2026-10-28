namespace Drone.Engine.Tests;

public class DraftTests
{
    [Fact]
    public void Last_place_picks_first_and_each_gets_their_best_free_pick()
    {
        var game = TestGames.New(TestGames.Defaults.With("game.speciesEnabled=false"));
        var want = game.Map.FreePlots.Take(3).Select(p => p.Index).ToList();
        var lists = game.Players.Select(_ => (IReadOnlyList<int>)want).ToArray();
        LandGrant.Resolve(game, lists);
        var byRank = game.ByRank;
        Assert.Equal(byRank[3].Seat, game.Map.Plots[want[0]].Owner);
        Assert.Equal(byRank[2].Seat, game.Map.Plots[want[1]].Owner);
        Assert.Equal(byRank[1].Seat, game.Map.Plots[want[2]].Owner);
        Assert.Empty(game.PlotsOf(byRank[0]));
    }

    [Fact]
    public void A_draft_first_species_drafts_first()
    {
        var game = TestGames.New(TestGames.Defaults.With("species.settler.draftFirst=true"), setup: TestGames.Cpus(4, "settler", "broker", "engineer", "survivor"));
        Assert.Equal("settler", LandGrant.Order(game)[0].SpeciesId);
    }

    [Fact]
    public void A_player_who_hands_in_nothing_gets_random_free_plots_after_the_clock()
    {
        var game = TestGames.New(seats: (g, p) => p.Seat == 0 ? new IdleSeat() : new Cpu.CpuSeat());
        long ticks = LandGrant.Run(game).Sum();
        Assert.Equal(game.Ticks(game.Settings.Land.DraftSeconds), ticks);
        Assert.All(game.Players, p => Assert.Single(game.PlotsOf(p)));
    }
}

public class LandAuctionTests
{
    static Game OnePlot(params string[] set) => TestGames.New(
        TestGames.Defaults.With(set.Append("land.storePlotsSpread=0").ToArray()),
        seats: (g, p) => p.Seat == 0 ? new ScriptedSeat { OnLandBid = (gg, me, a) => a.Position[me.Seat] < 200 ? Move.Up : Move.Hold } : new IdleSeat());

    [Fact]
    public void Unsold_store_plot_halves_the_next_start_price()
    {
        var game = TestGames.New(TestGames.Defaults.With("land.storePlotsSpread=0"), seats: (g, p) => new IdleSeat());
        foreach (var _ in LandAuction.RunStoreSales(game)) { }
        Assert.Equal(160 / 2 - 8, game.Store.LandPrice);
        Assert.Single(game.Events.OfType<LandUnsoldEvent>());
    }

    [Fact]
    public void Sold_store_plot_goes_to_the_high_bidder_at_their_bid()
    {
        var game = OnePlot();
        int cash = game.Players[0].Cash;
        foreach (var _ in LandAuction.RunStoreSales(game)) { }
        var sold = game.Events.OfType<LandSoldEvent>().Single();
        Assert.Equal(0, sold.Buyer);
        Assert.Equal(200, sold.Price);
        Assert.Equal(cash - 200, game.Players[0].Cash);
        Assert.Equal(200 - 60, game.Store.LandPrice);
    }

    [Fact]
    public void Bid_steps_grow_with_the_price()
    {
        var s = TestGames.Defaults;
        Assert.Equal(4, LandAuction.Step(s, 160));
        Assert.Equal(8, LandAuction.Step(s, 400));
        Assert.Equal(16, LandAuction.Step(s, 900));
    }

    [Fact]
    public void Sealed_bids_pay_the_second_highest()
    {
        var game = TestGames.New(TestGames.Defaults.With("land.storePlotsSpread=0", "game.landAuctionMode=sealed", "game.speciesEnabled=false"));
        foreach (var _ in LandAuction.RunStoreSales(game)) { }
        var sold = game.Events.OfType<LandSoldEvent>().SingleOrDefault();
        Assert.NotNull(sold);
        Assert.True(sold!.Price >= 160);
    }

    [Fact]
    public void A_land_surcharge_species_pays_more()
    {
        var game = TestGames.New(TestGames.Defaults.With("land.storePlotsSpread=0", "species.settler.landSurcharge=20"), setup: TestGames.Cpus(4, "settler", "broker", "engineer", "survivor"),
            seats: (g, p) => p.SpeciesId == "settler" ? new ScriptedSeat { OnLandBid = (gg, me, a) => a.Position[me.Seat] < 200 ? Move.Up : Move.Hold } : new IdleSeat());
        var settler = game.Players.Single(p => p.SpeciesId == "settler");
        int cash = settler.Cash;
        foreach (var _ in LandAuction.RunStoreSales(game)) { }
        Assert.Equal(cash - 220, settler.Cash);
    }
}

public class TurnTests
{
    static (Game game, Player me, ScriptedSeat seat) Setup(params string[] set)
    {
        var seat = new ScriptedSeat();
        var game = TestGames.New(TestGames.Defaults.With(set.Append("personalEvents.chancePerTurn=0").ToArray()),
            setup: [new("Ann", false, "broker"), new("CPU", true), new("CPU", true), new("CPU", true)],
            seats: (g, p) => p.IsCpu ? new IdleSeat() : seat);
        var me = game.Players.Single(p => !p.IsCpu);
        return (game, me, seat);
    }

    static Plot Claim(Game game, Player me, Func<Plot, bool> where)
    {
        var plot = game.Map.FreePlots.Where(where).OrderBy(p => game.Map.Distance(p, game.Map.Store)).First();
        plot.Owner = me.Seat;
        return plot;
    }

    [Fact]
    public void Buy_outfit_walk_and_install()
    {
        var (game, me, seat) = Setup();
        var plot = Claim(game, me, p => p.Terrain == Terrain.Plain);
        int cash = me.Cash, drones = game.Store.Drones;
        foreach (var c in new TurnCommand[] { new BuyDrone(), new OutfitDrone(Good.Energy), new WalkTo(plot.Index), new PressButton() }) seat.TurnScript.Enqueue(c);
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.Equal(Good.Energy, plot.Drone);
        Assert.Equal(cash - game.DronePriceFor(me) - game.Settings.Goods.Energy.OutfitCost, me.Cash);
        Assert.Equal(drones - 1, game.Store.Drones);
    }

    [Fact]
    public void A_drone_still_led_at_time_out_is_lost()
    {
        var (game, me, seat) = Setup();
        seat.TurnScript.Enqueue(new BuyDrone());
        seat.TurnScript.Enqueue(new OutfitDrone(Good.Food));
        seat.TurnScript.Enqueue(new Wait(999));
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.Contains(game.Events.OfType<TurnActionEvent>(), e => e.Action == "droneLost");
        Assert.DoesNotContain(game.Map.Plots, p => p.Drone != null);
    }

    [Fact]
    public void A_wrong_press_makes_the_drone_run_away_but_beginners_get_a_message()
    {
        foreach (bool beginner in new[] { false, true })
        {
            var seat = new ScriptedSeat();
            var game = TestGames.New(TestGames.Defaults.With("personalEvents.chancePerTurn=0"),
                setup: [new("Ann", false, "broker", beginner), new("CPU", true), new("CPU", true), new("CPU", true)],
                seats: (g, p) => p.IsCpu ? new IdleSeat() : seat);
            var me = game.Players.Single(p => !p.IsCpu);
            var other = game.Map.FreePlots.First(p => p.Terrain == Terrain.Plain);
            foreach (var c in new TurnCommand[] { new BuyDrone(), new OutfitDrone(Good.Food), new WalkTo(other.Index), new PressButton(), new EndTurn() }) seat.TurnScript.Enqueue(c);
            foreach (var _ in DevTurn.Run(game, me)) { }
            bool ranAway = game.Events.OfType<TurnActionEvent>().Any(e => e.Action == "droneRanAway");
            Assert.Equal(!beginner, ranAway);
        }
    }

    [Fact]
    public void No_mining_drones_on_the_water()
    {
        var (game, me, seat) = Setup();
        var river = Claim(game, me, p => p.Terrain == Terrain.River);
        foreach (var c in new TurnCommand[] { new BuyDrone(), new OutfitDrone(Good.Ore), new WalkTo(river.Index), new PressButton() }) seat.TurnScript.Enqueue(c);
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.Null(river.Drone);
    }

    [Fact]
    public void Starving_players_may_only_outfit_food()
    {
        var (game, me, seat) = Setup();
        me.Held[(int)Good.Food] = 0;
        seat.TurnScript.Enqueue(new BuyDrone());
        seat.TurnScript.Enqueue(new OutfitDrone(Good.Energy));
        seat.TurnScript.Enqueue(new SellDrone());
        foreach (var _ in DevTurn.Run(game, me)) { }
        var turn = game.Events.OfType<TurnEvent>().Single();
        Assert.True(turn.Starving);
        Assert.Equal(game.Settings.Turn.Seconds * (1 - game.Settings.Turn.FoodScaledShare * (1 - game.Settings.Turn.FoodFloor)), turn.Seconds, 3);
        Assert.DoesNotContain(game.Events.OfType<TurnActionEvent>(), e => e.Action == "outfit");
    }

    [Fact]
    public void Assay_results_are_public()
    {
        var (game, me, seat) = Setup();
        var plot = game.Map.Plots.First(p => p.Terrain == Terrain.Plain);
        seat.TurnScript.Enqueue(new Assay(plot.Index));
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.True(plot.Assayed);
    }

    [Fact]
    public void The_pub_ends_the_turn_and_pays()
    {
        var (game, me, seat) = Setup();
        int cash = me.Cash;
        seat.TurnScript.Enqueue(new Pub());
        seat.TurnScript.Enqueue(new BuyDrone());
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.InRange(me.Cash - cash, game.Settings.Turn.PubRoundBonus[0], game.Settings.Turn.PubCap);
        Assert.Equal(game.Settings.Store.DronesPer44Plots, game.Store.Drones);
    }

    [Fact]
    public void Catching_the_crate_pays_out()
    {
        var (game, me, seat) = Setup("turn.crateCashChance=1");
        int cash = me.Cash;
        seat.OnTurn = (g, p, t) =>
        {
            var target = g.Map.Plots.Where(x => x.Row == t.Crate.Row && !x.IsTown).MaxBy(x => t.Crate.WhenOver(g.Map, x)!.Value.From)!;
            var when = t.Crate.WhenOver(g.Map, target)!.Value;
            seat.TurnScript.Enqueue(new WalkTo(target.Index));
            seat.TurnScript.Enqueue(new Wait(Math.Max(0.02, when.From + 0.05 - t.Elapsed - t.SecondsTo(target))));
            seat.TurnScript.Enqueue(new CatchCrate());
            seat.OnTurn = null;
            return seat.TurnScript.Dequeue();
        };
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.Equal(cash + game.Settings.Turn.CrateCash[0], me.Cash);
    }

    [Fact]
    public void A_quick_sale_returns_the_plot_and_pays_half_the_land_price()
    {
        var (game, me, seat) = Setup();
        var plot = Claim(game, me, p => p.Terrain == Terrain.Plain);
        int cash = me.Cash;
        seat.TurnScript.Enqueue(new QuickSale(plot.Index));
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.True(plot.Free);
        Assert.Equal(cash + game.Store.LandPrice / 2, me.Cash);
    }

    [Fact]
    public void Marked_plots_are_auctioned_next_round_by_their_owner()
    {
        var (game, me, seat) = Setup();
        var plot = Claim(game, me, p => p.Terrain == Terrain.Plain);
        seat.TurnScript.Enqueue(new MarkForSale(plot.Index));
        foreach (var _ in DevTurn.Run(game, me)) { }
        Assert.True(plot.ForSale);
        foreach (var _ in LandAuction.RunPlayerSales(game)) { }
        Assert.False(plot.ForSale);
        Assert.Single(game.Events.OfType<LandUnsoldEvent>(), e => e.Seller == me.Seat);
    }
}

public class ProductionTests
{
    [Fact]
    public void Learning_curve_counts_matching_neighbours_up_to_the_cap()
    {
        var game = TestGames.New(TestGames.Defaults.With("production.luckSpread=0", "game.speciesEnabled=false", "production.learningCap=2"));
        var me = game.Players[0];
        var centre = game.Map.Plots.First(p => p.Terrain == Terrain.Plain && !p.Bank && game.Map.Neighbours(p).Count(n => n.Terrain == Terrain.Plain && !n.Bank) >= 3);
        var neighbours = game.Map.Neighbours(centre).Where(n => n.Terrain == Terrain.Plain && !n.Bank).Take(3).ToList();
        foreach (var p in neighbours.Append(centre)) { p.Owner = me.Seat; p.Drone = Good.Energy; }
        me.Held[(int)Good.Energy] = 0;
        var prod = Production.Run(game);
        // Base 3, +1 for 4 energy drones (scale step 3), +2 capped learning.
        Assert.Equal(3 + 1 + 2, prod.Output[centre.Index]);
    }

    [Fact]
    public void Each_missing_energy_unit_stops_a_plot()
    {
        var game = TestGames.New(TestGames.Defaults.With("production.luckSpread=0", "game.speciesEnabled=false"));
        var me = game.Players[0];
        var plots = game.Map.Plots.Where(p => p.Terrain == Terrain.Plain).Take(3).ToList();
        foreach (var p in plots) { p.Owner = me.Seat; p.Drone = Good.Food; }
        me.Held[(int)Good.Energy] = 1;
        var prod = Production.Run(game);
        Assert.Equal(2, plots.Count(p => prod.Output[p.Index] == 0));
        Assert.Equal(2, prod.Shortage[me.Seat]);
    }

    [Fact]
    public void Prices_follow_the_original_formula()
    {
        var s = TestGames.Defaults.Goods;
        // r = 8, T = 25*8/16 = 12, 25/4 + 12 + 6 = 24, raised to the food minimum 30.
        Assert.Equal(30, Prices.SupplyDemand(s, s.Food, 25, 12, 12, null));
        // r = 32, T = 100*32/16 = 200, 25 + 200 + 100 = 325 -> cap 250.
        Assert.Equal(250, Prices.SupplyDemand(s, s.Food, 100, 40, 10, null));
        // A huge shortage no longer wraps to a moderate price (L3).
        Assert.Equal(250, Prices.SupplyDemand(s, s.Food, 100, 255, 1, null));
    }
}

public class GoodsAuctionTests
{
    [Fact]
    public void Buyers_buy_from_the_store_at_the_top_and_sellers_sell_at_the_bottom()
    {
        var s = TestGames.Defaults.With("game.speciesEnabled=false");
        var game = TestGames.New(s, seats: (g, p) => p.Seat switch
        {
            0 => new ScriptedSeat { OnSide = (_, _, _) => Side.Buy, OnGoods = (gg, me, a) => me.Held[(int)Good.Food] < 6 ? new GoodsMove(Move.Up) : new GoodsMove(Move.Down) },
            1 => new ScriptedSeat { OnSide = (_, _, _) => Side.Sell, OnGoods = (gg, me, a) => new GoodsMove(Move.Down, BelowNeed: true) },
            _ => new IdleSeat(),
        });
        game.Players[0].Held[(int)Good.Food] = 3;
        game.Players[1].Held[(int)Good.Food] = 30;
        foreach (var p in game.Players.Skip(2)) p.Held[(int)Good.Food] = 3;
        foreach (var _ in GoodsAuction.Run(game, Good.Food)) { }
        var trades = game.Events.OfType<TradeEvent>().ToList();
        Assert.NotEmpty(trades);
        // Player 0 buys up to 6 food; player 1 sells everything it has left (27 after use, 14 after
        // spoilage) down to 0, pushing on below its need line, the rest to the store.
        // (It walks away one line at a time, so a few more units can go before it is clear of the seller.)
        Assert.InRange(game.Players[0].Held[(int)Good.Food], 6, 10);
        Assert.Equal(0, game.Players[1].Held[(int)Good.Food]);
        Assert.All(trades, t => Assert.True(t.Price >= 0));
        Assert.Contains(trades, t => t.Seller == 1 && t.Buyer == -1);
    }

    [Fact]
    public void Exports_are_shipped_off_after_their_auction()
    {
        var game = TestGames.New();
        game.Players[0].Held[(int)Good.Crystite] = 5;
        foreach (var _ in GoodsAuction.Run(game, Good.Crystite)) { }
        Assert.Equal(0, game.Store.Stock[(int)Good.Crystite]);
        Assert.Equal(0, game.Players[0].Held[(int)Good.Crystite]);
    }

    [Fact]
    public void An_auction_with_nothing_to_trade_is_skipped()
    {
        var game = TestGames.New();
        foreach (var _ in GoodsAuction.Run(game, Good.Helion)) { }
        Assert.True(game.Events.OfType<AuctionEvent>().Single().Skipped);
    }

    [Fact]
    public void Spoilage_happens_before_the_auction()
    {
        var game = TestGames.New(TestGames.Defaults.With("game.speciesEnabled=false"));
        var p = game.Players[0];
        p.Held[(int)Good.Food] = 13;
        GoodsAuction.Prepare(game, Good.Food);
        // 13 - 3 used = 10, half spoils = 5.
        Assert.Equal(5, p.Held[(int)Good.Food]);
        p.Held[(int)Good.Crystite] = 60;
        GoodsAuction.Prepare(game, Good.Crystite);
        Assert.Equal(48, p.Held[(int)Good.Crystite]);
    }
}
