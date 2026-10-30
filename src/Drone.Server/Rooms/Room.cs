using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Drone.Engine;
using Drone.Server.Identity;
using Drone.Server.Wire;

namespace Drone.Server.Rooms;

public enum RoomPhase { Lobby, Playing, Over }

/// <summary>A player in a room, in the order they joined.</summary>
public sealed class Member
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    /// <summary>The species picked in the lobby, or null for a random one.</summary>
    public string? Species { get; set; }
    public bool Connected { get; set; } = true;
    /// <summary>The seat in the game, or -1 before it starts.</summary>
    public int Seat { get; set; } = -1;
}

/// <summary>The host's choices for the game.</summary>
public sealed record RoomOptions(int Rounds, LandAuctionMode LandAuction);

/// <summary>What everyone in the room sees before (and around) the game.</summary>
public sealed record LobbyView(string Code, RoomPhase Phase, string HostId, RoomOptions Options, IReadOnlyList<string> RoundChoices,
    IReadOnlyList<SpeciesView> Species, IReadOnlyList<MemberView> Members);
public sealed record MemberView(string Id, string Name, string? Species, bool Connected, int Seat);
public sealed record SpeciesView(string Id, string Ability);

/// <summary>A message to the browsers: the events since the last one, in order, and the game as it
/// stands. Full is true for the first frame a browser gets after (re)joining a game under way.</summary>
public sealed record FrameMessage(long Tick, IReadOnlyList<JsonObject> Events, JsonNode? State, bool Full);

public sealed class RoomException(string message) : Exception(message);

/// <summary>
/// A room: players join with the code, pick a species, and the host picks the game length and the
/// land auction style, then starts. Empty seats are played by CPUs. The game runs on the room's clock,
/// one tick a call to <see cref="Step"/> (60 a second); human seats are fed by the browsers between
/// ticks. All methods lock the room, so the hub and the ticker can call them from any thread.
/// </summary>
public sealed class Room
{
    readonly object _sync = new();
    readonly List<Member> _members = [];
    readonly Dictionary<int, HumanSeat> _humans = [];
    readonly List<JsonObject> _pending = [];
    readonly ConcurrentQueue<string> _wantFull = new();
    readonly Settings _defaults;
    readonly ulong _seed;
    Game? _game;
    IEnumerator<int>? _run;
    int _wait;
    int _seen;
    object? _phase;
    string? _lastMap;
    long _phaseStart;

    public string Code { get; }
    public RoomPhase Phase { get; private set; }
    public string HostId { get; private set; } = "";
    public RoomOptions Options { get; private set; }
    public long Tick { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public int MaxPlayers => _defaults.Game.Players;

    public Room(string code, Settings? defaults = null, ulong? seed = null)
    {
        Code = code;
        _defaults = defaults ?? Settings.LoadDefault();
        _seed = seed ?? (ulong)Random.Shared.NextInt64();
        Options = new RoomOptions(_defaults.Game.RoundChoices[0], _defaults.Game.LandAuctionMode);
    }

    public bool AnyoneConnected { get { lock (_sync) return _members.Any(m => m.Connected); } }

    /// <summary>The game, for tests. Read it only under the room's lock (or once the game is over).</summary>
    internal Game? Game => _game;

    // ---------- Lobby ----------

    /// <summary>Takes a place in the room, or takes it back after a drop.</summary>
    public Member Join(IPlayerIdentity who)
    {
        lock (_sync)
        {
            LastActivity = DateTime.UtcNow;
            var m = _members.FirstOrDefault(x => x.Id == who.Id);
            if (m != null) { m.Connected = true; return m; }
            if (Phase != RoomPhase.Lobby) throw new RoomException("The game has already started.");
            if (_members.Count >= MaxPlayers) throw new RoomException("The room is full.");
            m = new Member { Id = who.Id, Name = who.Name };
            _members.Add(m);
            if (_members.Count == 1) HostId = who.Id;
            return m;
        }
    }

    /// <summary>Leaves for good (in the lobby) or drops out (in a game: the seat stays, with no input).</summary>
    public void Leave(string id, bool dropped)
    {
        lock (_sync)
        {
            LastActivity = DateTime.UtcNow;
            var m = _members.FirstOrDefault(x => x.Id == id);
            if (m == null) return;
            if (Phase == RoomPhase.Lobby && !dropped)
            {
                _members.Remove(m);
                if (HostId == id) HostId = _members.FirstOrDefault()?.Id ?? "";
                return;
            }
            m.Connected = false;
            if (m.Seat >= 0 && _humans.TryGetValue(m.Seat, out var seat)) seat.Release();
            if (Phase == RoomPhase.Lobby && HostId == id)
                HostId = _members.FirstOrDefault(x => x.Connected)?.Id ?? HostId;
        }
    }

    /// <summary>Picks a species (null or "random" for a random one).</summary>
    public void SetSpecies(string id, string? species)
    {
        lock (_sync)
        {
            InLobby();
            if (species is null or "" or "random") { Get(id).Species = null; return; }
            if (!_defaults.Game.SpeciesEnabled || !_defaults.Species.ContainsKey(species) || species == "plain")
                throw new RoomException("Unknown species.");
            if (_members.Any(m => m.Id != id && m.Species == species)) throw new RoomException("Someone has already picked that species.");
            Get(id).Species = species;
        }
    }

    public void SetOptions(string id, int rounds, LandAuctionMode landAuction)
    {
        lock (_sync)
        {
            InLobby();
            if (id != HostId) throw new RoomException("Only the host can change the game.");
            if (!_defaults.Game.RoundChoices.Contains(rounds)) throw new RoomException($"A game is {string.Join(" or ", _defaults.Game.RoundChoices)} rounds.");
            Options = new RoomOptions(rounds, landAuction);
        }
    }

    /// <summary>The host starts; CPUs take the empty seats.</summary>
    public void Start(string id)
    {
        lock (_sync)
        {
            InLobby();
            if (id != HostId) throw new RoomException("Only the host can start the game.");
            var settings = Options.LandAuction == _defaults.Game.LandAuctionMode
                ? _defaults
                : _defaults.With($"game.landAuctionMode={Options.LandAuction}");
            var seats = _members.Select(m => new SeatSetup(m.Name, false, m.Species, Key: m.Id))
                .Concat(Enumerable.Range(1, MaxPlayers - _members.Count).Select(i => new SeatSetup($"CPU {i}", true)))
                .ToList();
            _game = new Game(settings, Options.Rounds, seats, _seed, (g, p) =>
            {
                if (p.IsCpu) return new Engine.Cpu.CpuSeat();
                var seat = new HumanSeat();
                _humans[p.Seat] = seat;
                return seat;
            });
            foreach (var p in _game.Players.Where(p => !p.IsCpu)) _members.First(m => m.Id == p.Key).Seat = p.Seat;
            _run = _game.Run().GetEnumerator();
            _wait = 0;
            Phase = RoomPhase.Playing;
            LastActivity = DateTime.UtcNow;
        }
    }

    // ---------- The game clock ----------

    /// <summary>Runs one tick of the game (a 60th of a second). True while the game goes on.</summary>
    public bool Step()
    {
        lock (_sync)
        {
            if (Phase != RoomPhase.Playing || _run == null) return false;
            // The engine yields how long to let pass before it goes on; inputs that arrive meanwhile
            // are read when it does.
            while (_wait <= 0)
            {
                if (!_run.MoveNext())
                {
                    Phase = RoomPhase.Over;
                    LastActivity = DateTime.UtcNow;
                    Collect();
                    return true;
                }
                _wait = _run.Current;
                Collect();
            }
            _wait--;
            Tick++;
            return true;
        }
    }

    void Collect()
    {
        if (_game!.Phase != _phase) { _phase = _game.Phase; _phaseStart = Tick; }
        var events = _game.Events;
        for (; _seen < events.Count; _seen++) _pending.Add(EventWire.ToJson(events[_seen]));
    }

    /// <summary>The events since the last frame and the game as it stands, or null before the game.</summary>
    public FrameMessage? TakeFrame()
    {
        lock (_sync)
        {
            if (_game == null) return null;
            var state = EventWire.ToJson(StateView.Capture(_game, Tick, _phaseStart))!.AsObject();
            // The map is the bulk of a frame and seldom changes: it goes out only when it does.
            string map = state["map"]!.ToJsonString();
            if (map == _lastMap) state.Remove("map");
            else _lastMap = map;
            var msg = new FrameMessage(Tick, _pending.ToArray(), state, false);
            _pending.Clear();
            return msg;
        }
    }

    /// <summary>Everything visible now, for a player who (re)joins a game under way.</summary>
    public FrameMessage? FullFrame()
    {
        lock (_sync)
            return _game == null ? null : new FrameMessage(Tick, [], EventWire.ToJson(StateView.Capture(_game, Tick, _phaseStart)), true);
    }

    /// <summary>A connection that joined a game under way gets a full frame from the ticker, right after
    /// the next frame, so nothing newer can reach it first.</summary>
    public void WantFull(string connectionId) => _wantFull.Enqueue(connectionId);

    public bool TakeWantFull(out string connectionId) => _wantFull.TryDequeue(out connectionId!);

    // ---------- Players' inputs ----------

    /// <summary>The land draft: plots in order of preference (an empty list passes).</summary>
    public void Draft(string id, int[] plots)
    {
        lock (_sync)
        {
            var (seat, _) = Seat(id);
            if (_game!.Phase is not LandGrant.Draft d) throw new RoomException("The land draft is not on.");
            seat.SetDraft(d, plots);
        }
    }

    /// <summary>A live land auction: hold up (1), down (-1) or let go (0). The seller moves the whole scale.</summary>
    public void LandMove(string id, int direction)
    {
        lock (_sync)
        {
            var (seat, _) = Seat(id);
            if (_game!.Phase is not LandAuction a) throw new RoomException("No land auction is on.");
            seat.SetLandMove(a, ToMove(direction));
        }
    }

    public void SealedBid(string id, int bid)
    {
        lock (_sync)
        {
            var (seat, _) = Seat(id);
            if (_game!.Phase is not LandAuction a) throw new RoomException("No land auction is on.");
            seat.SetSealedBid(a, Math.Max(0, bid));
        }
    }

    /// <summary>Something to do in the player's own development turn.</summary>
    public void Turn(string id, TurnCommand command)
    {
        lock (_sync)
        {
            var (seat, p) = Seat(id);
            if (_game!.Phase is not DevTurn t || t.Player != p) throw new RoomException("It is not your turn.");
            if (command is HuntGlimmer) return;
            seat.AddCommand(t, command);
        }
    }

    public void GoodsSide(string id, Side side)
    {
        lock (_sync)
        {
            var (seat, _) = Seat(id);
            if (_game!.Phase is not GoodsAuction a) throw new RoomException("No goods auction is on.");
            seat.SetGoodsSide(a, side);
        }
    }

    /// <summary>A goods auction: hold up (1), down (-1) or let go (0); belowNeed lets a food or energy
    /// seller go on selling below their need line.</summary>
    public void GoodsMove(string id, int direction, bool belowNeed)
    {
        lock (_sync)
        {
            var (seat, _) = Seat(id);
            if (_game!.Phase is not GoodsAuction a) throw new RoomException("No goods auction is on.");
            seat.SetGoodsMove(a, new Engine.GoodsMove(ToMove(direction), belowNeed));
        }
    }

    // ---------- Helpers ----------

    public LobbyView Lobby()
    {
        lock (_sync)
            return new LobbyView(Code, Phase, HostId, Options, _defaults.Game.RoundChoices.Select(r => r.ToString()).ToList(),
                _defaults.Game.SpeciesEnabled
                    ? _defaults.Species.Where(kv => kv.Key != "plain").Select(kv => new SpeciesView(kv.Key, kv.Value.Ability)).ToList()
                    : [],
                _members.Select(m => new MemberView(m.Id, m.Name, m.Species, m.Connected, m.Seat)).ToList());
    }

    static Move ToMove(int direction) => direction > 0 ? Move.Up : direction < 0 ? Move.Down : Move.Hold;

    void InLobby()
    {
        if (Phase != RoomPhase.Lobby) throw new RoomException("The game has already started.");
    }

    Member Get(string id) => _members.FirstOrDefault(x => x.Id == id) ?? throw new RoomException("You are not in this room.");

    (HumanSeat Seat, Player Player) Seat(string id)
    {
        if (Phase != RoomPhase.Playing || _game == null) throw new RoomException("The game is not on.");
        var m = Get(id);
        LastActivity = DateTime.UtcNow;
        return (_humans[m.Seat], _game.Players[m.Seat]);
    }
}
