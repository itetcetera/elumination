using Drone.Engine;
using Drone.Server.Identity;
using Drone.Server.Rooms;
using Microsoft.AspNetCore.SignalR;

namespace Drone.Server.Hubs;

/// <summary>What a browser gets back on joining: who it is, the token to come back with, and the room.</summary>
public sealed record JoinResult(string Token, string PlayerId, LobbyView Room);

/// <summary>A development-turn action from the browser. Type is one of: walkTo, goToTown (plot),
/// buyDrone, outfit (good), sellDrone, press, release, assay (plot), markForSale (plot),
/// quickSale (plot), catchGlimmer, catchCrate, pub, endTurn.</summary>
public sealed record TurnAction(string Type, int? Plot = null, Good? Good = null);

/// <summary>
/// The browsers' connection (see docs/PROTOCOL.md). The server sends "lobby" (a <see cref="LobbyView"/>)
/// when the room changes and "frame" (a <see cref="FrameMessage"/>) several times a second in a game.
/// </summary>
public sealed class GameHub(RoomRegistry rooms, IIdentityService identities) : Hub
{
    const string RoomKey = "room", PlayerKey = "player";

    public async Task<JoinResult> CreateRoom(string name) => await Enter(rooms.Create(), name, null);

    /// <summary>Joins with the code. With the token from an earlier join it takes the same place back.</summary>
    public async Task<JoinResult> JoinRoom(string code, string name, string? token)
    {
        var room = rooms.Find(code) ?? throw new HubException("There is no room with that code.");
        return await Enter(room, name, token);
    }

    public async Task SetSpecies(string? species)
    {
        var (room, id) = Current();
        Run(() => room.SetSpecies(id, species));
        await Broadcast(room);
    }

    /// <summary>Host only: the game length (12 or 18 rounds) and "live" or "sealed" land auctions.</summary>
    public async Task SetOptions(int rounds, string landAuction)
    {
        var (room, id) = Current();
        if (!Enum.TryParse<LandAuctionMode>(landAuction, true, out var mode)) throw new HubException("Land auctions are live or sealed.");
        Run(() => room.SetOptions(id, rounds, mode));
        await Broadcast(room);
    }

    public async Task Start()
    {
        var (room, id) = Current();
        Run(() => room.Start(id));
        await Broadcast(room);
    }

    /// <summary>The land draft: plot indexes, most wanted first (an empty list passes).</summary>
    public void Draft(int[] plots) => Act((room, id) => room.Draft(id, plots ?? []));

    /// <summary>Live land auction: 1 holds up, -1 holds down, 0 lets go. Send it again when it changes.</summary>
    public void LandMove(int direction) => Act((room, id) => room.LandMove(id, direction));

    public void SealedBid(int bid) => Act((room, id) => room.SealedBid(id, bid));

    public void Turn(TurnAction action) => Act((room, id) => room.Turn(id, ToCommand(action)));

    /// <summary>"buy" or "sell", in the moment before a goods auction starts.</summary>
    public void GoodsSide(string side)
    {
        if (!Enum.TryParse<Side>(side, true, out var s)) throw new HubException("The side is buy or sell.");
        Act((room, id) => room.GoodsSide(id, s));
    }

    /// <summary>Goods auction: 1 holds up, -1 holds down, 0 lets go; belowNeed lets a seller go on
    /// below their food or energy need line.</summary>
    public void GoodsMove(int direction, bool belowNeed) => Act((room, id) => room.GoodsMove(id, direction, belowNeed));

    public async Task Leave()
    {
        var (room, id) = Current();
        room.Leave(id, dropped: false);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, room.Code);
        Context.Items.Remove(RoomKey);
        await Broadcast(room);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RoomKey, out var r) && r is Room room && Context.Items[PlayerKey] is string id)
        {
            room.Leave(id, dropped: true);
            await Broadcast(room);
        }
        await base.OnDisconnectedAsync(exception);
    }

    static TurnCommand ToCommand(TurnAction a)
    {
        int Plot() => a.Plot ?? throw new HubException($"{a.Type} needs a plot.");
        return a.Type switch
        {
            "walkTo" => new WalkTo(Plot()),
            "goToTown" => new GoToTown(Plot()),
            "buyDrone" => new BuyDrone(),
            "outfit" => new OutfitDrone(a.Good ?? throw new HubException("outfit needs a good.")),
            "sellDrone" => new SellDrone(),
            "press" => new PressButton(),
            "release" => new ReleaseDrone(),
            "assay" => new Assay(Plot()),
            "markForSale" => new MarkForSale(Plot()),
            "quickSale" => new QuickSale(Plot()),
            "catchGlimmer" => new CatchGlimmer(),
            "catchCrate" => new CatchCrate(),
            "pub" => new Pub(),
            "endTurn" => new EndTurn(),
            _ => throw new HubException($"Unknown action '{a.Type}'."),
        };
    }

    async Task<JoinResult> Enter(Room room, string name, string? token)
    {
        var who = identities.Resolve(token);
        if (who == null) (who, token) = identities.CreateGuest(name);
        Run(() => room.Join(who));
        Context.Items[RoomKey] = room;
        Context.Items[PlayerKey] = who.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        await Broadcast(room);
        // A game under way: everything on screen, then the frames as they come.
        if (room.Phase != RoomPhase.Lobby) room.WantFull(Context.ConnectionId);
        return new JoinResult(token!, who.Id, room.Lobby());
    }

    (Room Room, string Id) Current() =>
        Context.Items.TryGetValue(RoomKey, out var r) && r is Room room && Context.Items[PlayerKey] is string id
            ? (room, id)
            : throw new HubException("Join a room first.");

    void Act(Action<Room, string> action)
    {
        var (room, id) = Current();
        Run(() => action(room, id));
    }

    Task Broadcast(Room room) => Clients.Group(room.Code).SendAsync("lobby", room.Lobby());

    static void Run(Action a)
    {
        try { a(); }
        catch (RoomException e) { throw new HubException(e.Message); }
    }
}
