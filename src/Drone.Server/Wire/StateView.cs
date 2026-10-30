using Drone.Engine;

namespace Drone.Server.Wire;

/// <summary>
/// What a browser sees of a game: the map, the players, the store and the phase under way. Only
/// what the rules show everyone goes out: deposits only once a plot is assayed (F9 results are
/// public), the Glimmer only while it shows, and nothing of the CPUs' thinking, the event deck or the
/// plots' base outputs.
/// </summary>
/// <remarks>Phase is typed object so it serializes as its own kind of phase, with all its fields.</remarks>
public sealed record GameView(long Tick, int Round, int Rounds, bool Over, MapView Map, IReadOnlyList<PlayerView> Players,
    StoreView Store, object? Phase);

public sealed record MapView(int Cols, int Rows, IReadOnlyList<PlotView> Plots);

/// <summary>A plot. Owner -1 = nobody; Crystite and Helion are null until the plot is assayed.</summary>
public sealed record PlotView(int Index, int Col, int Row, Terrain Terrain, int OreUnits, int Owner, Good? Drone, bool ForSale,
    bool Assayed, int? Crystite, int? Helion);

public sealed record PlayerView(int Seat, string Name, string? Id, bool IsCpu, string Species, string Ability, int Cash, int[] Held,
    int Rank, int Money, int Land, int Goods, int Total, int FoodNeed, int EnergyNeed);

public sealed record StoreView(int[] Stock, int[] Price, int Drones, int DronePrice, int LandPrice, int[] OutfitCost);

/// <summary>The phase under way; Kind says which one ("draft", "land", "turn", "goods").</summary>
public abstract record PhaseView(string Kind);

/// <summary>The land draft: who has handed in a list, and the tick the draft closes at.</summary>
public sealed record DraftView(long EndsAt, bool[] Submitted) : PhaseView("draft");

/// <summary>A land auction. Seller -1 = the store. Positions are each player's bid line on the scale.</summary>
public sealed record LandView(int Plot, int Seller, int StartPrice, int Bottom, int[] Positions, int HighBidder, int HighBid,
    double Timer, bool Sealed) : PhaseView("land");

/// <summary>A development turn. GlimmerAt is set only while it shows; CrateAt is the plot under the
/// crate now (null once it has drifted off or been caught).</summary>
public sealed record TurnView(int Seat, double Seconds, double TimeLeft, int Location, bool InTown, bool Leading, Good? LeadJob,
    string? Message, bool Starving, int? GlimmerAt, CrateView Crate, string? EventId, int EventAmount) : PhaseView("turn");

public sealed record CrateView(int Row, bool FromLeft, double HexSeconds, bool Cash, Good Good, int Amount, bool Caught, int? At);

/// <summary>A goods auction. Pos is each player's line; Line their need line (food and energy).</summary>
public sealed record GoodsView(Good Good, int StartPrice, int Step, int Bottom, int Top, Side[] Sides, int[] Pos, int[] Line,
    double Timer, int Units, bool StoreSells, int[]? Trading) : PhaseView("goods");

public static class StateView
{
    /// <param name="phaseStart">The room tick the current phase began at (for the draft's deadline).</param>
    public static GameView Capture(Game game, long tick, long phaseStart)
    {
        var s = game.Settings;
        var plots = game.Map.Plots.Select(p => new PlotView(p.Index, p.Col, p.Row, p.Terrain, p.OreUnits, p.Owner, p.Drone, p.ForSale,
            p.Assayed, p.Assayed ? Math.Max(0, p.Crystite) : null, p.Assayed ? Math.Max(0, p.Helion) : null)).ToList();
        var players = game.Players.Select(p => new PlayerView(p.Seat, p.Name, p.Key, p.IsCpu, p.SpeciesId, p.Species.Ability, p.Cash,
            p.Held.ToArray(), p.Rank, p.Score.Money, p.Score.Land, p.Score.GoodsValue, p.Score.Total,
            game.FoodNeed(p, game.Round + 1), game.EnergyNeed(p))).ToList();
        var store = new StoreView(game.Store.Stock.ToArray(), game.Store.Price.ToArray(), game.Store.Drones, game.Store.DronePrice,
            game.Store.LandPrice, Engine.Goods.All.Select(g => s.Goods.Of(g).OutfitCost).ToArray());
        return new GameView(tick, game.Round, game.Rounds, game.Over, new MapView(game.Map.Cols, game.Map.Rows, plots), players, store,
            Phase(game, phaseStart));
    }

    static object? Phase(Game game, long phaseStart) => game.Phase switch
    {
        LandGrant.Draft d => new DraftView(phaseStart + d.Deadline, d.Lists.Select(l => l != null).ToArray()),
        LandAuction a => new LandView(a.Plot.Index, a.Seller, a.StartPrice, a.Bottom(game.Settings), a.Position.ToArray(), a.HighBidder,
            a.HighBid, a.Timer, game.Settings.Game.LandAuctionMode == LandAuctionMode.Sealed),
        DevTurn t => new TurnView(t.Player.Seat, t.Seconds, t.TimeLeft, t.Location.Index, t.InTown, t.Leading, t.LeadJob, t.Message,
            t.Starving, t.Glimmer.Showing,
            new CrateView(t.Crate.Row, t.Crate.FromLeft, t.Crate.HexSeconds, t.Crate.Cash, t.Crate.Good, t.Crate.Amount, t.Crate.Caught,
                t.Crate.PlotAt(game.Map, t.Elapsed)),
            t.Event?.Id, t.Event?.Amount ?? 0),
        GoodsAuction g => new GoodsView(g.Good, g.StartPrice, g.Step, g.Bottom, g.Top, g.Sides.ToArray(), g.Pos.ToArray(), g.Line.ToArray(),
            g.Timer, g.Units, g.StoreSells(game), g.Trading is { } tr ? [tr.Buyer, tr.Seller, tr.Price] : null),
        _ => null,
    };
}
