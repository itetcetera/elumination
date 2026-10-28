namespace Drone.Engine;

/// <summary>
/// One game of DRONE. <see cref="Run"/> is the whole game as a coroutine: it yields how many ticks
/// to let pass before it continues. A live server waits that long in real time and feeds the
/// players' inputs to their seats; the simulator just adds it up, so a headless game takes
/// milliseconds. Both run the same rules.
/// </summary>
public sealed class Game
{
    public Settings Settings { get; }
    public Rng Rng { get; }
    /// <summary>A separate stream for CPU thinking, so CPU changes never move the world's draws.</summary>
    public Rng CpuRng { get; }
    public MapSize Size { get; }
    public Map Map { get; }
    public IReadOnlyList<Player> Players { get; }
    public ISeat[] Seats { get; }
    public Store Store { get; } = new();
    public int Rounds { get; }
    public int Round { get; private set; }
    public long Tick { get; private set; }
    public List<GameEvent> Events { get; } = [];
    public bool Over { get; private set; }

    /// <summary>The phase under way, for live clients and tests.</summary>
    public object? Phase { get; internal set; }

    /// <summary>Plots marked for sale, in the order they were marked (auctioned next round).</summary>
    internal List<int> MarkedForSale { get; } = [];
    /// <summary>Store land sale prices this round (for the L4 averaging variant).</summary>
    internal List<int> StoreSalePrices { get; } = [];
    internal List<RoundEventKind> Deck { get; } = [];
    /// <summary>Drones left in the store after each round's turns (for statistics).</summary>
    public List<int> StoreDronesAfterTurns { get; } = [];

    public Game(Settings settings, int rounds, IReadOnlyList<SeatSetup> seats, ulong seed, Func<Game, Player, ISeat>? seatFor = null)
    {
        Settings = settings;
        Rng = new Rng(seed);
        CpuRng = Rng.Fork();
        Rounds = rounds;
        if (!settings.Game.RoundChoices.Contains(rounds)) throw new ArgumentException($"rounds must be one of {string.Join("/", settings.Game.RoundChoices)}");
        if (seats.Count != settings.Game.Players) throw new ArgumentException($"need {settings.Game.Players} seats");
        Size = MapGen.SizeFor(settings, rounds);
        Map = MapGen.Generate(settings, Size, Rng);

        // A9: everyone gets a random seat number, human or CPU.
        var order = seats.ToList();
        Rng.Shuffle(order);
        var speciesLeft = settings.Species.Keys.Where(k => k != "plain").ToList();
        foreach (var s in order) if (s.Species != null) speciesLeft.Remove(s.Species);
        var players = new List<Player>();
        for (int i = 0; i < order.Count; i++)
        {
            var setup = order[i];
            string species = !settings.Game.SpeciesEnabled ? "plain"
                : setup.Species ?? TakeRandom(speciesLeft);
            var sp = settings.Species[species];
            var p = new Player { Seat = i, Name = setup.Name, IsCpu = setup.IsCpu, Beginner = setup.Beginner, SpeciesId = species, Species = sp };
            p.Cash = setup.IsCpu ? settings.Start.CpuCash : settings.Start.Cash + sp.CashDelta;
            p.Held[(int)Good.Food] = settings.Start.Food;
            p.Held[(int)Good.Energy] = settings.Start.Energy;
            players.Add(p);
        }
        Players = players;

        var st = settings.Store;
        Store.Stock[(int)Good.Food] = st.Food;
        Store.Stock[(int)Good.Energy] = st.Energy;
        Store.Stock[(int)Good.Ore] = st.Ore;
        Store.DroneCap = Store.Drones = (int)Math.Round(st.DronesPer44Plots * PlotScale);
        foreach (var g in Goods.All) Store.Price[(int)g] = settings.Goods.Of(g).StartPrice;
        Store.DronePrice = st.StartDronePrice;
        Store.LandPrice = settings.Land.StoreStartPrice;

        Seats = players.Select(p => seatFor?.Invoke(this, p) ?? new Cpu.CpuSeat()).ToArray();
        BuildDeck();
        Scoring.Rank(this);
    }

    string TakeRandom(List<string> left)
    {
        if (left.Count == 0) throw new InvalidOperationException("more players than species");
        var s = left[Rng.Next(left.Count)];
        left.Remove(s);
        return s;
    }

    /// <summary>Claimable plots relative to the original's 44.</summary>
    public double PlotScale => Map.ClaimableCount / 44.0;

    public int Ticks(double seconds) => Math.Max(0, (int)Math.Round(seconds * Settings.Game.TicksPerSecond));
    public double Seconds(long ticks) => ticks / (double)Settings.Game.TicksPerSecond;

    /// <summary>The round as the 12-round rules see it (stretched or as is, per settings).</summary>
    public double EffectiveRound => Settings.Game.RoundScaling == RoundScaling.Stretch ? Round * 12.0 / Rounds : Round;

    /// <summary>The original's four reward bands: rounds 1-3, 4-7, 8-11, and the last round.</summary>
    public int Band
    {
        get
        {
            if (Round >= Rounds) return 3;
            double r = EffectiveRound;
            return r <= 3 ? 0 : r <= 7 ? 1 : r <= 11 ? 2 : 3;
        }
    }

    public bool LastRound => Round >= Rounds;

    /// <summary>Food each player needs for a round (thirds of the game, I2), plus species need.</summary>
    public int FoodNeed(Player p, int? round = null)
    {
        int r = Math.Clamp(round ?? Round, 1, Rounds);
        int third = Math.Min(2, (r - 1) * 3 / Rounds);
        return Settings.Game.FoodNeedByThird[third] + p.Species.ExtraFoodNeed;
    }

    /// <summary>Energy a player's drones use: every non-energy drone (G5), less for the Engineer.</summary>
    public int EnergyNeed(Player p)
    {
        int drones = Map.Plots.Count(x => x.Owner == p.Seat && x.Drone is { } d && d != Good.Energy);
        int free = p.Species.FreeEnergyEvery > 0 ? drones / p.Species.FreeEnergyEvery * p.Species.FreeEnergy : 0;
        return (int)Math.Ceiling(drones * Settings.Production.EnergyPerDrone - 1e-9) - free;
    }

    public IEnumerable<Plot> PlotsOf(Player p) => Map.Plots.Where(x => x.Owner == p.Seat);

    /// <summary>Players from the leader down.</summary>
    public IReadOnlyList<Player> ByRank => Players.OrderBy(p => p.Rank).ToList();

    public int DronePriceFor(Player p) => Store.DronePrice + p.Species.DroneSurcharge;

    public void Emit(GameEvent e) => Events.Add(e);

    void BuildDeck()
    {
        // H1: a card every event round, drawn without replacement from a deck sized to the game.
        double scale = Math.Max(1.0, (Rounds - 1) / 11.0);
        foreach (var (kind, cards) in Settings.RoundEvents.Cards)
        {
            if (!Settings.Game.HelionEnabled && kind is RoundEventKind.IceRaiders or RoundEventKind.Comet) continue;
            int n = (int)Math.Round(cards * scale);
            for (int i = 0; i < n; i++) Deck.Add(kind);
        }
    }

    /// <summary>The whole game. Each yielded number is a count of ticks to let pass.</summary>
    public IEnumerable<int> Run()
    {
        for (int r = 1; r <= Rounds; r++)
        {
            Round = r;
            StoreSalePrices.Clear();
            foreach (var w in LandGrant.Run(this)) yield return Pass(w);
            foreach (var w in LandAuction.RunPlayerSales(this)) yield return Pass(w);
            foreach (var w in LandAuction.RunStoreSales(this)) yield return Pass(w);
            if (Settings.Land.UseAveragePrice && StoreSalePrices.Count > 0)
                Store.LandPrice = Math.Max(Settings.Land.MinPrice, (int)StoreSalePrices.Average() - Settings.Land.SoldDrop);
            foreach (var w in DevTurn.RunAll(this)) yield return Pass(w);
            StoreDronesAfterTurns.Add(Store.Drones);
            var production = Production.Run(this);
            if (!LastRound) RoundEvents.Run(this, production);
            production.Commit();
            Prices.BeforeAuctions(this);
            foreach (var good in Settings.Goods.AuctionOrder)
            {
                if (good == Good.Helion && !Settings.Game.HelionEnabled) continue;
                foreach (var w in GoodsAuction.Run(this, good)) yield return Pass(w);
            }
            Prices.BuildDrones(this);
            Scoring.Run(this);
        }
        Scoring.End(this);
        Over = true;
    }

    int Pass(int ticks)
    {
        Tick += ticks;
        return ticks;
    }

    /// <summary>Plays the whole game headless.</summary>
    public Game Play()
    {
        foreach (var _ in Run()) { }
        return this;
    }
}
