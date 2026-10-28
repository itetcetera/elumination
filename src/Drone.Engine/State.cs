namespace Drone.Engine;

public sealed class Player
{
    public required int Seat { get; init; }
    public required string Name { get; init; }
    public required bool IsCpu { get; init; }
    /// <summary>Beginner assists (set per player before the game).</summary>
    public required bool Beginner { get; init; }
    public required string SpeciesId { get; init; }
    public required SpeciesSettings Species { get; init; }

    public int Cash { get; set; }
    public int[] Held { get; } = new int[Goods.Count];
    /// <summary>This round's production, per good (shown before each auction).</summary>
    public int[] Produced { get; } = new int[Goods.Count];

    /// <summary>Rank after the last scoring: 0 = leader.</summary>
    public int Rank { get; set; }
    public Score Score { get; set; } = new();
    public HashSet<string> EventsUsed { get; } = [];

    public override string ToString() => $"{Name}({SpeciesId})";
}

public sealed record Score
{
    public int Money { get; init; }
    public int Land { get; init; }
    public int GoodsValue { get; init; }
    public int Total => Money + Land + GoodsValue;
}

public sealed class Store
{
    public int[] Stock { get; } = new int[Goods.Count];
    public int Drones { get; set; }
    public int DroneCap { get; set; }
    public int[] Price { get; } = new int[Goods.Count];
    public int DronePrice { get; set; }
    /// <summary>The store's land start price for its next auction.</summary>
    public int LandPrice { get; set; }
}

/// <summary>What happened, for the simulator's statistics and for live clients.</summary>
public abstract record GameEvent(int Round);
public sealed record DraftEvent(int Round, int Seat, int Plot) : GameEvent(Round);
public sealed record LandSoldEvent(int Round, int Plot, int Seller, int Buyer, int Price, bool Store) : GameEvent(Round);
public sealed record LandUnsoldEvent(int Round, int Plot, int Seller, int StartPrice) : GameEvent(Round);
public sealed record QuickSaleEvent(int Round, int Plot, int Seat, int Price) : GameEvent(Round);
public sealed record TurnEvent(int Round, int Seat, double Seconds, bool Starving) : GameEvent(Round);
public sealed record TurnActionEvent(int Round, int Seat, string Action, int Plot, int Amount) : GameEvent(Round);
public sealed record PersonalEventHappened(int Round, int Seat, string Id, int Amount) : GameEvent(Round);
public sealed record RoundEventHappened(int Round, RoundEventKind Kind, string Detail) : GameEvent(Round);
public sealed record ProductionEvent(int Round, int Seat, int[] Produced, int Shortage) : GameEvent(Round);
public sealed record TradeEvent(int Round, Good Good, int Seller, int Buyer, int Price) : GameEvent(Round);
public sealed record AuctionEvent(int Round, Good Good, int StartPrice, int NewPrice, int Units, bool Skipped) : GameEvent(Round);
public sealed record ScoreEvent(int Round, int Seat, int Total, int Rank) : GameEvent(Round);
public sealed record GameOverEvent(int Round, int Colony, int Rating, string Verdict, int Winner, bool FirstFounder) : GameEvent(Round);

/// <summary>Who sits where, before the game starts.</summary>
public sealed record SeatSetup(string Name, bool IsCpu, string? Species = null, bool Beginner = false);
