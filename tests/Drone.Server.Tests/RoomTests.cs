using Drone.Engine;
using Drone.Server.Identity;
using Drone.Server.Rooms;

namespace Drone.Server.Tests;

public class RoomTests
{
    static readonly GuestIdentityService Ids = new();

    static (Room Room, IPlayerIdentity Ann, IPlayerIdentity Bob) TwoPlayers(ulong seed = 3)
    {
        var room = new Room("TEST01", seed: seed);
        var ann = Ids.CreateGuest("Ann").Identity;
        var bob = Ids.CreateGuest("Bob").Identity;
        room.Join(ann);
        room.Join(bob);
        return (room, ann, bob);
    }

    static void StepUntil(Room room, Func<bool> done, int maxTicks = 2_000_000)
    {
        for (int i = 0; i < maxTicks && !done(); i++)
            if (!room.Step()) break;
        Assert.True(done(), "the room never got there");
    }

    static Player PlayerOf(Room room, IPlayerIdentity who) => room.Game!.Players.Single(p => p.Key == who.Id);

    [Fact]
    public void Humans_who_do_nothing_still_get_a_whole_game_with_CPUs_in_the_empty_seats()
    {
        var (room, ann, _) = TwoPlayers();
        room.SetOptions(ann.Id, 12, LandAuctionMode.Live);
        room.Start(ann.Id);
        Assert.Equal(2, room.Game!.Players.Count(p => p.IsCpu));
        Assert.All(room.Lobby().Members, m => Assert.True(m.Seat >= 0));
        StepUntil(room, () => room.Phase == RoomPhase.Over);
        var frame = room.TakeFrame()!;
        Assert.Contains(frame.Events, e => (string?)e["t"] == "GameOver");
        Assert.Equal(12, room.Game.Round);
    }

    [Fact]
    public void Only_the_host_starts_and_picks_the_options()
    {
        var (room, ann, bob) = TwoPlayers();
        Assert.Throws<RoomException>(() => room.Start(bob.Id));
        Assert.Throws<RoomException>(() => room.SetOptions(bob.Id, 18, LandAuctionMode.Sealed));
        Assert.Throws<RoomException>(() => room.SetOptions(ann.Id, 24, LandAuctionMode.Live));
        room.SetOptions(ann.Id, 18, LandAuctionMode.Sealed);
        room.Start(ann.Id);
        Assert.Equal(18, room.Game!.Rounds);
        Assert.Equal(LandAuctionMode.Sealed, room.Game.Settings.Game.LandAuctionMode);
        Assert.Throws<RoomException>(() => room.Join(Ids.CreateGuest("Cat").Identity));
    }

    [Fact]
    public void Two_players_cannot_pick_the_same_species()
    {
        var (room, ann, bob) = TwoPlayers();
        room.SetSpecies(ann.Id, "engineer");
        Assert.Throws<RoomException>(() => room.SetSpecies(bob.Id, "engineer"));
        Assert.Throws<RoomException>(() => room.SetSpecies(bob.Id, "dragon"));
        room.Start(ann.Id);
        Assert.Equal("engineer", PlayerOf(room, ann).SpeciesId);
    }

    [Fact]
    public void A_draft_list_gets_its_first_pick_and_a_turn_installs_a_drone()
    {
        var (room, ann, _) = TwoPlayers();
        room.Start(ann.Id);
        var game = room.Game!;
        var me = PlayerOf(room, ann);

        // Everyone else idles, so the first free plot on the list goes to Ann unless a CPU or an
        // earlier drafter wants it too: take the plot no CPU ranks, the farthest from town.
        StepUntil(room, () => game.Phase is LandGrant.Draft);
        var pick = game.Map.FreePlots.Where(p => !p.IsWater).MaxBy(p => game.Map.TownDistance(p))!;
        room.Draft(ann.Id, [pick.Index]);
        StepUntil(room, () => game.Phase is not LandGrant.Draft);
        Assert.Equal(me.Seat, pick.Owner);

        StepUntil(room, () => game.Phase is DevTurn t && t.Player == me);
        room.Turn(ann.Id, new BuyDrone());
        room.Turn(ann.Id, new OutfitDrone(Good.Food));
        room.Turn(ann.Id, new WalkTo(pick.Index));
        room.Turn(ann.Id, new PressButton());
        room.Turn(ann.Id, new EndTurn());
        StepUntil(room, () => game.Phase is not DevTurn t || t.Player != me);
        Assert.Equal(Good.Food, pick.Drone);
    }

    [Fact]
    public void Inputs_outside_their_phase_are_refused()
    {
        var (room, ann, bob) = TwoPlayers();
        Assert.Throws<RoomException>(() => room.Draft(ann.Id, [1]));
        room.Start(ann.Id);
        var game = room.Game!;
        StepUntil(room, () => game.Phase is LandGrant.Draft);
        Assert.Throws<RoomException>(() => room.Turn(ann.Id, new EndTurn()));
        Assert.Throws<RoomException>(() => room.GoodsMove(bob.Id, 1, false));
        StepUntil(room, () => game.Phase is DevTurn);
        var other = ((DevTurn)game.Phase!).Player.Key == ann.Id ? bob : ann;
        if (((DevTurn)game.Phase!).Player.IsCpu) other = ann;
        Assert.Throws<RoomException>(() => room.Turn(other.Id, new EndTurn()));
    }

    [Fact]
    public void A_seller_of_food_trades_in_the_goods_auction()
    {
        var (room, ann, _) = TwoPlayers();
        room.Start(ann.Id);
        var game = room.Game!;
        var me = PlayerOf(room, ann);
        StepUntil(room, () => game.Phase is GoodsAuction { Good: Good.Food });
        var auction = (GoodsAuction)game.Phase!;
        int before = me.Held[(int)Good.Food];
        room.GoodsSide(ann.Id, Side.Sell);
        room.GoodsMove(ann.Id, -1, belowNeed: true);
        StepUntil(room, () => game.Phase != auction);
        Assert.True(me.Held[(int)Good.Food] < before, "Ann sold no food");
    }

    [Fact]
    public void A_dropped_player_rejoins_and_the_frames_show_the_game()
    {
        var (room, ann, bob) = TwoPlayers();
        room.Start(ann.Id);
        StepUntil(room, () => room.Game!.Round == 2);
        room.Leave(bob.Id, dropped: true);
        Assert.False(room.Lobby().Members.Single(m => m.Id == bob.Id).Connected);
        room.Join(bob);
        Assert.True(room.Lobby().Members.Single(m => m.Id == bob.Id).Connected);
        var full = room.FullFrame()!;
        Assert.True(full.Full);
        var state = full.State!.AsObject();
        Assert.Equal(2, (int)state["round"]!);
        Assert.Equal(4, state["players"]!.AsArray().Count);
        // Deposits only show once assayed.
        foreach (var plot in state["map"]!["plots"]!.AsArray())
            if (!(bool)plot!["assayed"]!) Assert.Null(plot["crystite"]);
    }
}
