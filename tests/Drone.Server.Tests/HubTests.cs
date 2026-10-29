using System.Collections.Concurrent;
using System.Text.Json;
using Drone.Server.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Drone.Server.Tests;

/// <summary>Players in browsers, simulated: they talk to the real server through its hub.</summary>
public class HubTests(HubTests.FastServer server) : IClassFixture<HubTests.FastServer>
{
    /// <summary>The server with the game clock running as fast as it can.</summary>
    public sealed class FastServer : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(s => s.Configure<TickOptions>(o => { o.TicksPerSecond = 0; o.TicksPerFrame = 500; }));
    }

    sealed record Join(string Token, string PlayerId, Lobby Room);
    sealed record Lobby(string Code, string Phase, string HostId, JsonElement Options, List<MemberInfo> Members);
    sealed record MemberInfo(string Id, string Name, string? Species, bool Connected, int Seat);
    sealed record Frame(long Tick, List<JsonElement> Events, JsonElement State, bool Full);

    /// <summary>A browser: its connection and everything the server sent it.</summary>
    sealed class Browser : IAsyncDisposable
    {
        public HubConnection Hub = null!;
        public readonly ConcurrentQueue<Lobby> Lobbies = new();
        public readonly ConcurrentQueue<Frame> Frames = new();
        public readonly TaskCompletionSource GameOver = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Browser> Connect(FastServer server)
        {
            var b = new Browser();
            b.Hub = new HubConnectionBuilder()
                .WithUrl(new Uri(server.Server.BaseAddress, "hub"), o => o.HttpMessageHandlerFactory = _ => server.Server.CreateHandler())
                .Build();
            b.Hub.On<Lobby>("lobby", l => b.Lobbies.Enqueue(l));
            b.Hub.On<Frame>("frame", f =>
            {
                b.Frames.Enqueue(f);
                if (f.Events.Any(e => e.GetProperty("t").GetString() == "GameOver")) b.GameOver.TrySetResult();
            });
            await b.Hub.StartAsync();
            return b;
        }

        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }

    static async Task Until(Func<bool> done, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(60);
        while (!done())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Two_friends_play_a_whole_game_with_a_drop_and_a_rejoin()
    {
        await using var ann = await Browser.Connect(server);
        var bob = await Browser.Connect(server);

        var a = await ann.Hub.InvokeAsync<Join>("CreateRoom", "Ann");
        string code = a.Room.Code;
        Assert.Equal(6, code.Length);
        var b = await bob.Hub.InvokeAsync<Join>("JoinRoom", code.ToLowerInvariant(), "Bob", null);
        Assert.Equal(2, b.Room.Members.Count);

        await ann.Hub.InvokeAsync("SetSpecies", "engineer");
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => bob.Hub.InvokeAsync("SetOptions", 18, "sealed"));
        Assert.Contains("host", refused.Message);
        await ann.Hub.InvokeAsync("SetOptions", 12, "live");
        await ann.Hub.InvokeAsync("Start");

        // Bob drops mid-game and comes back with his token: he gets a full frame.
        await Until(() => bob.Frames.Count > 0, "first frame");
        await bob.DisposeAsync();
        await using var bob2 = await Browser.Connect(server);
        var back = await bob2.Hub.InvokeAsync<Join>("JoinRoom", code, "Bob", b.Token);
        Assert.Equal(b.PlayerId, back.PlayerId);
        await Until(() => bob2.Frames.Any(f => f.Full), "full frame after rejoining");
        var full = bob2.Frames.First(f => f.Full);
        Assert.Equal(4, full.State.GetProperty("players").GetArrayLength());

        await ann.GameOver.Task.WaitAsync(TimeSpan.FromSeconds(120));
        var last = ann.Frames.Last();
        Assert.True(last.State.GetProperty("over").GetBoolean());
        Assert.Contains(ann.Frames.SelectMany(f => f.Events), e => e.GetProperty("t").GetString() == "Draft");
        await Until(() => ann.Lobbies.LastOrDefault()?.Phase == "over", "lobby says the game is over");
    }

    [Fact]
    public async Task Bad_codes_and_actions_come_back_as_readable_errors()
    {
        await using var ann = await Browser.Connect(server);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => ann.Hub.InvokeAsync<Join>("JoinRoom", "NOPE00", "Ann", null));
        Assert.Contains("no room", e.Message);
        await ann.Hub.InvokeAsync<Join>("CreateRoom", "Ann");
        e = await Assert.ThrowsAnyAsync<Exception>(() => ann.Hub.InvokeAsync("Draft", new[] { 1 }));
        Assert.Contains("not on", e.Message);
    }
}
