namespace Drone.Engine;

/// <summary>Goods, in the index order used by every per-good array.</summary>
public enum Good { Food = 0, Energy = 1, Ore = 2, Crystite = 3, Helion = 4 }

public static class Goods
{
    public const int Count = 5;
    public static readonly Good[] All = [Good.Food, Good.Energy, Good.Ore, Good.Crystite, Good.Helion];

    /// <summary>Export goods are shipped off-world: no local use, the store never resells them.</summary>
    public static bool IsExport(this Good g) => g is Good.Crystite or Good.Helion;
    /// <summary>Mined goods cannot be made on the river or a lake.</summary>
    public static bool IsMined(this Good g) => g is Good.Ore or Good.Crystite or Good.Helion;

    public static GoodSettings Of(this GoodsSettings s, Good g) => g switch
    {
        Good.Food => s.Food,
        Good.Energy => s.Energy,
        Good.Ore => s.Ore,
        Good.Crystite => s.Crystite,
        _ => s.Helion,
    };
}

/// <summary>
/// Seeded random numbers (xoshiro256**). Every random draw in a game comes from the game's own
/// generator, so a seed replays a game exactly.
/// </summary>
public sealed class Rng
{
    ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
    }

    static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        ulong result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, n).</summary>
    public int Next(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        return (int)((NextULong() >> 33) % (ulong)n);
    }

    /// <summary>Uniform in [min, max] inclusive.</summary>
    public int Range(int min, int max) => min + Next(max - min + 1);

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));
    public double Range(double min, double max) => min + (max - min) * NextDouble();
    public bool Chance(double p) => NextDouble() < p;

    /// <summary>A bell-curve value like the original's: twelve uniforms summed, minus 6.</summary>
    public double Normal()
    {
        double s = 0;
        for (int i = 0; i < 12; i++) s += NextDouble();
        return s - 6;
    }

    /// <summary>round(N(0,1) x spread), rounding halves away from zero.</summary>
    public int RoundNormal(double spread = 1) => (int)Math.Round(Normal() * spread, MidpointRounding.AwayFromZero);

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Picks an index with the given weights.</summary>
    public int Weighted(IReadOnlyList<double> weights)
    {
        double total = weights.Sum(), r = NextDouble() * total;
        for (int i = 0; i < weights.Count; i++)
        {
            r -= weights[i];
            if (r < 0) return i;
        }
        return weights.Count - 1;
    }

    /// <summary>An independent generator for a side stream (CPU thinking), so changing the CPU never
    /// changes the world's draws.</summary>
    public Rng Fork() => new(NextULong());
}

/// <summary>
/// Hex grid helpers. The map uses flat-top hexes in "odd-q" offset layout: columns are straight
/// lines top to bottom (the river is one column) and odd columns sit half a hex lower.
/// </summary>
public static class Hex
{
    static readonly (int dq, int dr)[] CubeDirs = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)];

    public static (int q, int r) ToAxial(int col, int row) => (col, row - (col - (col & 1)) / 2);
    public static (int col, int row) FromAxial(int q, int r) => (q, r + (q - (q & 1)) / 2);

    public static int Distance(int c1, int r1, int c2, int r2)
    {
        var (q1, a1) = ToAxial(c1, r1);
        var (q2, a2) = ToAxial(c2, r2);
        int dq = q1 - q2, dr = a1 - a2;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    /// <summary>The up-to-six neighbours inside a cols x rows map.</summary>
    public static IEnumerable<(int col, int row)> Neighbours(int col, int row, int cols, int rows)
    {
        var (q, r) = ToAxial(col, row);
        foreach (var (dq, dr) in CubeDirs)
        {
            var (c, w) = FromAxial(q + dq, r + dr);
            if (c >= 0 && c < cols && w >= 0 && w < rows) yield return (c, w);
        }
    }
}
