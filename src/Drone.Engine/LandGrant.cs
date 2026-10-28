namespace Drone.Engine;

/// <summary>
/// The land draft (D1, D2): each player secretly ranks as many free plots as there are players.
/// Resolved like fantasy waivers: last place first, each player takes their highest pick still
/// free, one plot each. A Settler drafts first (species ability). A player who hands in nothing
/// before the clock runs out gets random free plots ranked for them; an empty list is a pass.
/// </summary>
public static class LandGrant
{
    public sealed class Draft
    {
        public required IReadOnlyList<int>?[] Lists { get; init; }
        public required long Deadline { get; init; }
    }

    public static IEnumerable<int> Run(Game game)
    {
        if (!game.Map.FreePlots.Any()) yield break;
        int n = game.Players.Count;
        var draft = new Draft { Lists = new IReadOnlyList<int>?[n], Deadline = game.Ticks(game.Settings.Land.DraftSeconds) };
        game.Phase = draft;
        for (long t = 0; ; t++)
        {
            for (int i = 0; i < n; i++)
                draft.Lists[i] ??= Clean(game, game.Seats[i].Draft(game, game.Players[i]));
            if (draft.Lists.All(l => l != null) || t >= draft.Deadline) break;
            yield return 1;
        }
        for (int i = 0; i < n; i++)
            draft.Lists[i] ??= RandomList(game);
        Resolve(game, draft.Lists!);
        game.Phase = null;
    }

    static IReadOnlyList<int>? Clean(Game game, IReadOnlyList<int>? list)
        => list?.Where(i => i >= 0 && i < game.Map.Plots.Length && game.Map.Plots[i].Free).Distinct().Take(game.Players.Count).ToList();

    static List<int> RandomList(Game game)
    {
        var free = game.Map.FreePlots.Select(p => p.Index).ToList();
        game.Rng.Shuffle(free);
        return free.Take(game.Players.Count).ToList();
    }

    /// <summary>Draft order: last place first, Settlers ahead of everyone.</summary>
    public static IReadOnlyList<Player> Order(Game game)
        => game.Players.OrderByDescending(p => p.Species.DraftFirst).ThenByDescending(p => p.Rank).ToList();

    public static void Resolve(Game game, IReadOnlyList<int>[] lists)
    {
        foreach (var p in Order(game))
        {
            foreach (int idx in lists[p.Seat])
            {
                var plot = game.Map.Plots[idx];
                if (!plot.Free) continue;
                plot.Owner = p.Seat;
                game.Emit(new DraftEvent(game.Round, p.Seat, idx));
                break;
            }
        }
    }
}
