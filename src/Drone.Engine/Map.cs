namespace Drone.Engine;

public enum Terrain { Plain, Mountain, River, Lake, Store, Outpost }

/// <summary>One hex of ELUM.</summary>
public sealed class Plot
{
    public required int Index { get; init; }
    public required int Col { get; init; }
    public required int Row { get; init; }
    public Terrain Terrain { get; set; }
    /// <summary>Ore units of a mountain (1-3).</summary>
    public int OreUnits { get; set; }
    /// <summary>True deposit levels; -1 means no mining here (river, lake, town).</summary>
    public int Crystite { get; set; }
    public int Helion { get; set; }
    /// <summary>Plains touching the river or a lake (food bonus).</summary>
    public bool Bank { get; set; }

    public int Owner { get; set; } = -1;
    /// <summary>The installed drone's job, if any.</summary>
    public Good? Drone { get; set; }
    /// <summary>Marked for sale by its owner: auctioned next round (E2).</summary>
    public bool ForSale { get; set; }
    /// <summary>Assayed: the deposit levels are known to everyone (F9).</summary>
    public bool Assayed { get; set; }
    /// <summary>The job it had before this round's development turns (for the mining switch rule).</summary>
    public Good? JobBefore { get; set; }

    public bool IsWater => Terrain is Terrain.River or Terrain.Lake;
    public bool IsTown => Terrain is Terrain.Store or Terrain.Outpost;
    /// <summary>Can be owned (everything but the store and outposts).</summary>
    public bool Claimable => !IsTown;
    public bool Free => Claimable && Owner < 0;

    public int Deposit(Good g) => g == Good.Crystite ? Crystite : g == Good.Helion ? Helion : 0;

    public override string ToString() => $"#{Index}({Col},{Row}) {Terrain}";
}

public sealed class Map
{
    public int Cols { get; }
    public int Rows { get; }
    public Plot[] Plots { get; }
    public Plot Store { get; }
    public IReadOnlyList<Plot> Outposts { get; }
    readonly int[][] _neighbours;

    public Map(int cols, int rows, Plot[] plots)
    {
        Cols = cols;
        Rows = rows;
        Plots = plots;
        Store = plots.Single(p => p.Terrain == Terrain.Store);
        Outposts = plots.Where(p => p.Terrain == Terrain.Outpost).ToList();
        _neighbours = plots.Select(p => Hex.Neighbours(p.Col, p.Row, cols, rows).Select(n => n.row * cols + n.col).ToArray()).ToArray();
    }

    public Plot At(int col, int row) => Plots[row * Cols + col];
    public int RiverCol => Cols / 2;
    public IEnumerable<Plot> Neighbours(Plot p) => _neighbours[p.Index].Select(i => Plots[i]);
    public int Distance(Plot a, Plot b) => Hex.Distance(a.Col, a.Row, b.Col, b.Row);

    /// <summary>Plots that can be owned (the map minus the store and outposts).</summary>
    public int ClaimableCount => Plots.Count(p => p.Claimable);
    public IEnumerable<Plot> FreePlots => Plots.Where(p => p.Free);

    /// <summary>Distance to the nearest place to buy drones (store or outpost).</summary>
    public int TownDistance(Plot p) => Math.Min(Distance(p, Store), Outposts.Count == 0 ? int.MaxValue : Outposts.Min(o => Distance(p, o)));

    /// <summary>The town building nearest to a plot.</summary>
    public Plot NearestTown(Plot p) => Outposts.Prepend(Store).MinBy(t => Distance(p, t))!;

    public int SeedDropPerHex { get; init; } = 1;
    /// <summary>How far a deposit spreads from its seed, in hexes.</summary>
    public int SeedRadius { get; init; } = int.MaxValue;

    public void SetDepositsFromSeeds(IEnumerable<Plot> seeds, int level, bool helion)
    {
        foreach (var seed in seeds) RaiseAround(seed, level, helion);
    }

    /// <summary>Spreads a seed: each plot gets max(current, level - distance). Water and town never
    /// hold a mineable deposit, but a seed there still spreads to its neighbours.</summary>
    public void RaiseAround(Plot seed, int level, bool helion)
    {
        foreach (var p in Plots)
        {
            if (p.IsWater || p.IsTown) continue;
            int d = Distance(p, seed);
            if (d > SeedRadius) continue;
            int v = Math.Max(0, level - d * SeedDropPerHex);
            if (helion) p.Helion = Math.Max(p.Helion, v);
            else p.Crystite = Math.Max(p.Crystite, v);
        }
    }
}

public static class MapGen
{
    public static MapSize SizeFor(Settings s, int rounds)
        => s.Map.Sizes.FirstOrDefault(z => z.Rounds == rounds)
           ?? throw new ArgumentException($"no map size for {rounds} rounds in settings");

    public static Map Generate(Settings s, MapSize size, Rng rng)
    {
        int cols = size.Cols, rows = size.Rows, river = cols / 2;
        if (cols % 2 == 0) throw new ArgumentException("map columns must be odd (the river runs down the centre)");
        var plots = new Plot[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                plots[r * cols + c] = new Plot { Index = r * cols + c, Col = c, Row = r, Terrain = c == river ? Terrain.River : Terrain.Plain };
        plots[(rows / 2) * cols + river].Terrain = Terrain.Store;

        // Outposts sit on the river, above and below the store.
        var outpostRows = s.Map.OutpostRowShares.Select(f => (int)Math.Round(f * (rows - 1))).Distinct().ToList();
        rng.Shuffle(outpostRows);
        foreach (int r in outpostRows.Take(size.Outposts)) plots[r * cols + river].Terrain = Terrain.Outpost;

        // Mountains: the same share of each row as the original, split across both sides.
        int sideCols = river;
        for (int r = 0; r < rows; r++)
        {
            int count = Math.Max(2, (int)Math.Round((cols - 1) * s.Map.MountainShare));
            int left = count / 2, right = count / 2;
            if (count % 2 == 1) { if (rng.Chance(0.5)) left++; else right++; }
            PlaceMountains(plots, cols, r, 0, sideCols, left, s, rng);
            PlaceMountains(plots, cols, r, river + 1, sideCols, right, s, rng);
        }

        // Lakes: single water hexes away from the river and each other.
        var placedLakes = new List<Plot>();
        for (int i = 0; i < size.Lakes; i++)
        {
            var options = plots.Where(p => p.Terrain == Terrain.Plain
                && Math.Abs(p.Col - river) >= s.Map.LakeMinDistance
                && placedLakes.All(l => Hex.Distance(l.Col, l.Row, p.Col, p.Row) > s.Map.LakeMinDistance)).ToList();
            if (options.Count == 0) break;
            var lake = rng.Pick(options);
            lake.Terrain = Terrain.Lake;
            placedLakes.Add(lake);
        }

        var map = new Map(cols, rows, plots) { SeedDropPerHex = s.Map.SeedDropPerHex, SeedRadius = s.Map.SeedRadius };
        foreach (var p in plots)
        {
            bool mineable = !p.IsWater && !p.IsTown;
            p.Crystite = mineable ? 0 : -1;
            p.Helion = mineable ? 0 : -1;
            p.Bank = p.Terrain == Terrain.Plain && map.Neighbours(p).Any(n => n.IsWater);
        }

        // Crystite seeds may land anywhere, the river included (it spreads but is not mined there).
        var crystiteSeeds = DistinctPicks(plots, size.CrystiteSeeds, rng, _ => true);
        map.SetDepositsFromSeeds(crystiteSeeds, s.Map.SeedLevel, helion: false);
        if (s.Game.HelionEnabled)
        {
            var helionSeeds = DistinctPicks(plots, size.HelionSeeds, rng,
                p => crystiteSeeds.All(c => map.Distance(c, p) >= s.Map.HelionSeedMinDistance));
            map.SetDepositsFromSeeds(helionSeeds, s.Map.SeedLevel, helion: true);
        }
        else foreach (var p in plots) p.Helion = -1;
        return map;
    }

    static void PlaceMountains(Plot[] plots, int cols, int row, int firstCol, int width, int count, Settings s, Rng rng)
    {
        var colsLeft = Enumerable.Range(firstCol, width).Where(c => plots[row * cols + c].Terrain == Terrain.Plain).ToList();
        for (int i = 0; i < count && colsLeft.Count > 0; i++)
        {
            int c = colsLeft[rng.Next(colsLeft.Count)];
            colsLeft.Remove(c);
            var p = plots[row * cols + c];
            p.Terrain = Terrain.Mountain;
            p.OreUnits = 1 + rng.Weighted(s.Map.MountainUnitWeights);
        }
    }

    static List<Plot> DistinctPicks(Plot[] plots, int count, Rng rng, Func<Plot, bool> allowed)
    {
        var picked = new List<Plot>();
        for (int i = 0; i < count; i++)
        {
            var options = plots.Where(p => !picked.Contains(p) && allowed(p)).ToList();
            if (options.Count == 0) break;
            picked.Add(rng.Pick(options));
        }
        return picked;
    }
}
