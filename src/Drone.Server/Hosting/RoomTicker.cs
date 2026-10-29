using System.Diagnostics;
using Drone.Server.Hubs;
using Drone.Server.Rooms;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Drone.Server.Hosting;

public sealed class TickOptions
{
    /// <summary>Game ticks per second: 60. 0 runs as fast as possible (tests).</summary>
    public int TicksPerSecond { get; set; } = 60;
    /// <summary>Ticks per message to the browsers (4: 15 a second).</summary>
    public int TicksPerFrame { get; set; } = 4;
    /// <summary>How long an empty or finished room is kept.</summary>
    public TimeSpan RoomIdle { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Runs every room's game on the clock and sends the browsers what happened.</summary>
public sealed class RoomTicker(RoomRegistry rooms, IHubContext<GameHub> hub, IOptions<TickOptions> options, ILogger<RoomTicker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        var clock = Stopwatch.StartNew();
        long ticks = 0;
        var lastSweep = clock.Elapsed;
        while (!stop.IsCancellationRequested)
        {
            if (o.TicksPerSecond > 0)
            {
                // Hold the pace of the clock; after a stall, catch up at most a few ticks.
                var due = TimeSpan.FromSeconds((double)(ticks + 1) / o.TicksPerSecond);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, stop).ContinueWith(_ => { });
                else if (-wait > TimeSpan.FromSeconds(0.1)) ticks = (long)(clock.Elapsed.TotalSeconds * o.TicksPerSecond);
            }
            else await Task.Yield();
            ticks++;
            foreach (var room in rooms.All)
            {
                bool running;
                try { running = room.Step(); }
                catch (Exception e)
                {
                    log.LogError(e, "Room {Code} failed", room.Code);
                    continue;
                }
                if (!running) continue;
                bool over = room.Phase == RoomPhase.Over;
                if (room.Tick % o.TicksPerFrame == 0 || over)
                {
                    var frame = room.TakeFrame();
                    if (frame != null) await hub.Clients.Group(room.Code).SendAsync("frame", frame, stop);
                    while (room.TakeWantFull(out var connection))
                        if (room.FullFrame() is { } full) await hub.Clients.Client(connection).SendAsync("frame", full, stop);
                    if (over) await hub.Clients.Group(room.Code).SendAsync("lobby", room.Lobby(), stop);
                }
            }
            if (clock.Elapsed - lastSweep > TimeSpan.FromMinutes(1))
            {
                lastSweep = clock.Elapsed;
                int n = rooms.Sweep(o.RoomIdle);
                if (n > 0) log.LogInformation("Removed {Count} idle rooms", n);
            }
        }
    }
}
