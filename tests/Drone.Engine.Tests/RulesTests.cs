namespace Drone.Engine.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_and_every_variant_load()
    {
        var s = TestGames.Defaults;
        Assert.Equal(4, s.Game.Players);
        foreach (var v in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Settings.DefaultPath())!, "variants"), "*.json"))
            Assert.NotNull(Settings.LoadDefault([v]));
    }

    [Fact]
    public void Overrides_change_one_value()
    {
        var s = TestGames.Defaults.With("land.storeStartPrice=200", "goods.food.spoilage=0.25", "land.bidSteps=[4,4,4]");
        Assert.Equal(200, s.Land.StoreStartPrice);
        Assert.Equal(0.25, s.Goods.Food.Spoilage);
        Assert.Equal([4, 4, 4], s.Land.BidSteps);
    }

    [Fact]
    public void Unknown_settings_are_refused()
    {
        Assert.Throws<ArgumentException>(() => TestGames.Defaults.With("land.storeStartPrise=200"));
        Assert.Throws<ArgumentException>(() => Settings.LoadDefault(overrides: ["nope.x=1"]));
    }

    [Fact]
    public void A_missing_setting_is_refused()
    {
        var json = TestGames.Defaults.ToJson();
        json["land"]!.AsObject().Remove("minPrice");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Settings.FromJson(json));
    }
}

public class MapTests
{
    [Theory]
    [InlineData(12, 9, 5)]
    [InlineData(18, 9, 7)]
    [InlineData(24, 13, 7)]
    public void Map_scales_with_game_length(int rounds, int cols, int rows)
    {
        var s = TestGames.Defaults;
        for (ulong seed = 0; seed < 50; seed++)
        {
            var map = MapGen.Generate(s, MapGen.SizeFor(s, rounds), new Rng(seed));
            Assert.Equal(cols, map.Cols);
            Assert.Equal(rows, map.Rows);
            Assert.Equal((cols / 2, rows / 2), (map.Store.Col, map.Store.Row));
            // One straight river down the centre column (with the store and any outposts on it).
            Assert.All(map.Plots.Where(p => p.Col == cols / 2), p => Assert.True(p.IsWater || p.IsTown));
            Assert.All(map.Plots.Where(p => p.Col != cols / 2), p => Assert.False(p.Terrain is Terrain.River or Terrain.Store or Terrain.Outpost));
            Assert.Equal(MapGen.SizeFor(s, rounds).Outposts, map.Outposts.Count);
            for (int r = 0; r < rows; r++)
            {
                var row = map.Plots.Where(p => p.Row == r).ToList();
                Assert.Contains(row, p => p.Terrain == Terrain.Mountain && p.Col < cols / 2);
                Assert.Contains(row, p => p.Terrain == Terrain.Mountain && p.Col > cols / 2);
            }
            Assert.All(map.Plots.Where(p => p.Terrain == Terrain.Mountain), p => Assert.InRange(p.OreUnits, 1, 3));
            Assert.All(map.Plots.Where(p => p.IsWater || p.IsTown), p => Assert.True(p.Crystite == -1 && p.Helion == -1));
        }
    }

    [Fact]
    public void Helion_and_crystite_seed_areas_never_overlap()
    {
        var s = TestGames.Defaults;
        for (ulong seed = 0; seed < 300; seed++)
        {
            var map = MapGen.Generate(s, MapGen.SizeFor(s, 24), new Rng(seed));
            Assert.DoesNotContain(map.Plots, p => p.Crystite >= s.Map.SeedLevel - s.Map.SeedDropPerHex && p.Helion >= s.Map.SeedLevel - s.Map.SeedDropPerHex);
        }
    }

    [Fact]
    public void Deposits_drop_one_per_hex()
    {
        var s = TestGames.Defaults.With("game.helionEnabled=false");
        var map = MapGen.Generate(s, MapGen.SizeFor(s, 12), new Rng(5));
        foreach (var p in map.Plots.Where(p => p.Crystite > 0))
            Assert.Contains(map.Neighbours(p).Append(p), n => n.Crystite >= p.Crystite || n.IsWater || n.IsTown);
    }

    [Fact]
    public void Hex_neighbours_are_at_distance_one()
    {
        for (int c = 0; c < 9; c++)
            for (int r = 0; r < 5; r++)
            {
                var ns = Hex.Neighbours(c, r, 9, 5).ToList();
                Assert.InRange(ns.Count, 2, 6);
                Assert.All(ns, n => Assert.Equal(1, Hex.Distance(c, r, n.col, n.row)));
            }
        Assert.Equal(6, Hex.Neighbours(4, 2, 9, 5).Count());
    }

    [Fact]
    public void Plains_by_the_water_get_the_food_bonus()
    {
        var s = TestGames.Defaults;
        var map = MapGen.Generate(s, MapGen.SizeFor(s, 12), new Rng(3));
        var bank = map.Plots.First(p => p.Bank);
        Assert.Equal(s.Production.Plain[0] + s.Production.BankFoodBonus, Production.Base(s, bank, Good.Food));
        var river = map.Plots.First(p => p.Terrain == Terrain.River);
        Assert.Null(Production.Base(s, river, Good.Ore));
        Assert.Equal(s.Production.Water[0], Production.Base(s, river, Good.Food));
    }
}

public class GameTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(24)]
    public void Cpu_games_finish(int rounds)
    {
        var game = TestGames.New(rounds: rounds, seed: 11).Play();
        Assert.True(game.Over);
        Assert.Equal(rounds, game.Round);
        Assert.Single(game.Events.OfType<GameOverEvent>());
        Assert.All(game.Players, p => Assert.True(p.Cash >= 0));
        Assert.All(game.Players, p => Assert.All(p.Held, h => Assert.True(h >= 0)));
        Assert.Equal(Enumerable.Range(0, 4), game.Players.Select(p => p.Rank).OrderBy(r => r));
    }

    [Fact]
    public void A_seed_replays_the_same_game()
    {
        string Log(Game g) => string.Join("\n", g.Events.Select(e => e is ProductionEvent pe ? $"{pe.Seat}:{string.Join(",", pe.Produced)}" : e.ToString()));
        var a = TestGames.New(seed: 42).Play();
        var b = TestGames.New(seed: 42).Play();
        Assert.Equal(Log(a), Log(b));
        var c = TestGames.New(seed: 43).Play();
        Assert.NotEqual(Log(a), Log(c));
    }

    [Fact]
    public void Species_are_distinct_and_cpus_get_their_own_cash()
    {
        var game = TestGames.New(setup: [new("Ann", false, "broker"), new("CPU", true), new("CPU", true), new("CPU", true)]);
        Assert.Equal(4, game.Players.Select(p => p.SpeciesId).Distinct().Count());
        var ann = game.Players.Single(p => !p.IsCpu);
        Assert.Equal(game.Settings.Start.Cash + game.Settings.Species["broker"].CashDelta, ann.Cash);
        Assert.All(game.Players.Where(p => p.IsCpu), p => Assert.Equal(game.Settings.Start.CpuCash, p.Cash));
    }

    [Fact]
    public void Species_off_means_plain_rules_for_everyone()
    {
        var game = TestGames.New(Settings.LoadDefault(["original"]));
        Assert.All(game.Players, p => Assert.Equal("plain", p.SpeciesId));
        Assert.DoesNotContain(game.Map.Plots, p => p.Helion > 0);
    }

    [Fact]
    public void The_original_variant_has_no_new_events()
    {
        var game = TestGames.New(Settings.LoadDefault(["original"]));
        Assert.DoesNotContain(game.Deck, k => k is RoundEventKind.IceRaiders or RoundEventKind.Comet or RoundEventKind.SupplyShip or RoundEventKind.Malfunction);
        Assert.Equal(20, game.Deck.Count);
    }

    [Fact]
    public void The_deck_grows_with_the_game()
    {
        Assert.True(TestGames.New(rounds: 24).Deck.Count > TestGames.New(rounds: 12).Deck.Count * 2 - 2);
    }

    [Fact]
    public void Food_need_rises_by_thirds_and_with_species()
    {
        var game = TestGames.New(rounds: 18, setup: TestGames.Cpus(4, "sprinter", "broker"));
        var sprinter = game.Players.Single(p => p.SpeciesId == "sprinter");
        var broker = game.Players.Single(p => p.SpeciesId == "broker");
        Assert.Equal(7, game.FoodNeed(broker, 6));
        Assert.Equal(9, game.FoodNeed(broker, 7));
        Assert.Equal(11, game.FoodNeed(broker, 13));
        Assert.Equal(9, game.FoodNeed(sprinter, 1));
    }
}
