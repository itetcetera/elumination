using Drone.Engine.Cpu;

namespace Drone.Engine.Tests;

static class TestGames
{
    public static Settings Defaults => Settings.LoadDefault();

    public static List<SeatSetup> Cpus(int n = 4, params string[] species)
        => Enumerable.Range(0, n).Select(i => new SeatSetup($"CPU {i + 1}", true, i < species.Length ? species[i] : null)).ToList();

    public static Game New(Settings? s = null, int rounds = 12, ulong seed = 1, Func<Game, Player, ISeat>? seats = null, List<SeatSetup>? setup = null)
        => new(s ?? Defaults, rounds, setup ?? Cpus(), seed, seats);

    /// <summary>Runs the game's coroutine until a condition holds (or the game ends).</summary>
    public static void RunUntil(Game game, IEnumerator<int> run, Func<bool> done)
    {
        while (!done() && run.MoveNext()) { }
    }
}

/// <summary>A seat that never decides anything (a human who walked away).</summary>
sealed class IdleSeat : ISeat
{
    public IReadOnlyList<int>? Draft(Game game, Player me) => null;
    public Move LandBid(Game game, Player me, LandAuction auction) => Move.Hold;
    public Move LandSell(Game game, Player me, LandAuction auction) => Move.Hold;
    public int? SealedBid(Game game, Player me, LandAuction auction) => null;
    public TurnCommand? Turn(Game game, Player me, DevTurn turn) => null;
    public Side? GoodsSide(Game game, Player me, GoodsAuction auction) => null;
    public GoodsMove Goods(Game game, Player me, GoodsAuction auction) => GoodsMove.Hold;
}

/// <summary>A CPU seat with some calls scripted.</summary>
sealed class ScriptedSeat : ISeat
{
    readonly CpuSeat _cpu = new();
    public Func<Game, Player, IReadOnlyList<int>?>? OnDraft;
    public Func<Game, Player, LandAuction, Move>? OnLandBid;
    public Queue<TurnCommand> TurnScript = new();
    public Func<Game, Player, DevTurn, TurnCommand?>? OnTurn;
    public Func<Game, Player, GoodsAuction, GoodsMove>? OnGoods;
    public Func<Game, Player, GoodsAuction, Side?>? OnSide;

    public IReadOnlyList<int>? Draft(Game game, Player me) => OnDraft != null ? OnDraft(game, me) : _cpu.Draft(game, me);
    public Move LandBid(Game game, Player me, LandAuction a) => OnLandBid != null ? OnLandBid(game, me, a) : _cpu.LandBid(game, me, a);
    public Move LandSell(Game game, Player me, LandAuction a) => Move.Hold;
    public int? SealedBid(Game game, Player me, LandAuction a) => _cpu.SealedBid(game, me, a);
    public TurnCommand? Turn(Game game, Player me, DevTurn t)
        => OnTurn != null ? OnTurn(game, me, t) : TurnScript.Count > 0 ? TurnScript.Dequeue() : new EndTurn();
    public Side? GoodsSide(Game game, Player me, GoodsAuction a) => OnSide != null ? OnSide(game, me, a) : _cpu.GoodsSide(game, me, a);
    public GoodsMove Goods(Game game, Player me, GoodsAuction a) => OnGoods != null ? OnGoods(game, me, a) : _cpu.Goods(game, me, a);
}
