namespace Drone.Engine;

/// <summary>Up, down or stay put on an auction scale.</summary>
public enum Move { Hold = 0, Up = 1, Down = -1 }

public enum Side { Buy, Sell }

/// <summary>A goods auction move: the direction, and whether a food or energy seller pushes on
/// below their own need line (I12).</summary>
public readonly record struct GoodsMove(Move Move, bool BelowNeed = false)
{
    public static readonly GoodsMove Hold = new(Move.Hold);
}

/// <summary>
/// Whoever plays a seat: the CPU, or (in a live game) a human's latest input. Every call is made
/// on the game's clock; returning null means "not decided yet": the engine lets a tick pass and asks
/// again, until that phase's clock runs out. A CPU answers at once, which is what lets headless
/// games run as fast as the machine allows.
/// </summary>
public interface ISeat
{
    /// <summary>The land draft: up to N plot indexes in order of preference (empty = pass).</summary>
    IReadOnlyList<int>? Draft(Game game, Player me);

    /// <summary>Live land auction, each tick, as a bidder.</summary>
    Move LandBid(Game game, Player me, LandAuction auction);

    /// <summary>Live land auction, each tick, as the seller of the plot (moves the whole scale).</summary>
    Move LandSell(Game game, Player me, LandAuction auction);

    /// <summary>Sealed land auction: a bid (0 = none).</summary>
    int? SealedBid(Game game, Player me, LandAuction auction);

    /// <summary>The next thing to do in a development turn.</summary>
    TurnCommand? Turn(Game game, Player me, DevTurn turn);

    /// <summary>Goods auction: the side to take (null keeps the default).</summary>
    Side? GoodsSide(Game game, Player me, GoodsAuction auction);

    /// <summary>Goods auction, each tick.</summary>
    GoodsMove Goods(Game game, Player me, GoodsAuction auction);
}
