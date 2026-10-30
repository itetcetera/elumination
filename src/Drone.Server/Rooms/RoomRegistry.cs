using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Drone.Server.Rooms;

/// <summary>All rooms, by their 6-character code.</summary>
public sealed class RoomRegistry
{
    // No 0/O or 1/I: codes are read out and typed.
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 6;

    readonly ConcurrentDictionary<string, Room> _rooms = new();

    public IEnumerable<Room> All => _rooms.Values;

    public Room Create()
    {
        while (true)
        {
            var code = new string(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
            var room = new Room(code);
            if (_rooms.TryAdd(code, room)) return room;
        }
    }

    public Room? Find(string? code) =>
        code != null && _rooms.TryGetValue(Normalize(code), out var r) ? r : null;

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    /// <summary>Drops rooms nobody has been in for a while, and finished games after a while.</summary>
    public int Sweep(TimeSpan idle)
    {
        int removed = 0;
        var now = DateTime.UtcNow;
        foreach (var r in _rooms.Values)
            if ((!r.AnyoneConnected || r.Phase == RoomPhase.Over) && now - r.LastActivity > idle && _rooms.TryRemove(r.Code, out _))
                removed++;
        return removed;
    }
}
