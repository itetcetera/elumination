using Drone.Engine;

namespace Drone.Server.Rooms;

/// <summary>
/// A human's seat: it hands the engine what the player's browser last sent. Every input is tied to
/// the phase it was sent for (the draft, an auction, a turn), so nothing carries over into the next
/// one; with no input the engine's clock runs on, as it does for a player who walked away. The room
/// locks around every call, so this class needs no locking of its own.
/// </summary>
public sealed class HumanSeat : ISeat
{
    object? _draftPhase;
    IReadOnlyList<int>? _draft;
    object? _landPhase;
    Move _landMove;
    object? _sealedPhase;
    int? _sealed;
    DevTurn? _turn;
    readonly Queue<TurnCommand> _commands = new();
    object? _goodsPhase;
    Side? _side;
    GoodsMove _goodsMove = GoodsMove.Hold;

    public void SetDraft(object phase, IReadOnlyList<int> plots) => (_draftPhase, _draft) = (phase, plots);

    public void SetLandMove(object phase, Move move) => (_landPhase, _landMove) = (phase, move);

    public void SetSealedBid(object phase, int bid) => (_sealedPhase, _sealed) = (phase, bid);

    public void AddCommand(DevTurn turn, TurnCommand command)
    {
        if (_turn != turn) { _turn = turn; _commands.Clear(); }
        _commands.Enqueue(command);
    }

    public void SetGoodsSide(object phase, Side side)
    {
        if (_goodsPhase != phase) { _goodsPhase = phase; _goodsMove = GoodsMove.Hold; }
        _side = side;
    }

    public void SetGoodsMove(object phase, GoodsMove move)
    {
        if (_goodsPhase != phase) { _goodsPhase = phase; _side = null; }
        _goodsMove = move;
    }

    /// <summary>A dropped player holds nothing down: auction moves stop until they come back.</summary>
    public void Release()
    {
        _landMove = Move.Hold;
        _goodsMove = GoodsMove.Hold;
    }

    public IReadOnlyList<int>? Draft(Game game, Player me) => _draftPhase == game.Phase ? _draft : null;

    public Move LandBid(Game game, Player me, LandAuction auction) => _landPhase == auction ? _landMove : Move.Hold;

    public Move LandSell(Game game, Player me, LandAuction auction) => _landPhase == auction ? _landMove : Move.Hold;

    public int? SealedBid(Game game, Player me, LandAuction auction) => _sealedPhase == auction ? _sealed : null;

    public TurnCommand? Turn(Game game, Player me, DevTurn turn)
        => _turn == turn && _commands.Count > 0 ? _commands.Dequeue() : null;

    public Side? GoodsSide(Game game, Player me, GoodsAuction auction) => _goodsPhase == auction ? _side : null;

    public GoodsMove Goods(Game game, Player me, GoodsAuction auction) => _goodsPhase == auction ? _goodsMove : GoodsMove.Hold;
}
