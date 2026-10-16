using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MMOCore.GameServer.World;
using Xunit;

namespace MMOCore.GameServer.Tests;

/// <summary>
/// Invariant gate for the broad-phase spatial grid that backs BOTH the per-client AoI
/// snapshot pass and the area-effect damage tick (rebuilt once per tick under _simLock).
///
/// Why this file exists: a grid defect does NOT throw. It silently omits an entity from a
/// snapshot (the entity vanishes for that client) or from an area-effect query (the spell
/// misses someone standing in it). verify.ps1 goes green either way. These are the failures
/// that only surface mid-playtest as "desync", which is the expensive shape.
/// </summary>
public class SpatialGridTests
{
    private const float Cell = 32f;    // NetworkConfig cell size
    private const float Radius = 80f;  // AoI radius


    /// <summary>Read the private bucket dictionary without widening SpatialGrid's surface.
    /// (Same reflection approach AbilityCatalogTests uses to enumerate weapon lines.)</summary>
    private static int BucketCount<T>(SpatialGrid<T> grid)
    {
        var f = typeof(SpatialGrid<T>).GetField("_cells", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("SpatialGrid._cells was renamed - update this test.");
        return ((System.Collections.ICollection)f.GetValue(grid)!).Count;
    }

    private static List<int> Brute(IReadOnlyList<(float x, float z)> pts, float qx, float qz, float r)
    {
        var o = new List<int>();
        for (var i = 0; i < pts.Count; i++)
        {
            var dx = pts[i].x - qx; var dz = pts[i].z - qz;
            if (dx * dx + dz * dz <= r * r) o.Add(i);
        }
        return o;
    }

    /// <summary>
    /// THE critical invariant: broad phase may return extras, but must never MISS an entity
    /// that is genuinely within the radius. A false negative is an entity that disappears
    /// from a client's view. Randomized over a seeded corpus so it covers cell boundaries,
    /// negative coordinates and multi-cell footprints without hand-picking cases.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(12345)] [InlineData(99)]
    public void Query_NeverMissesAnEntityInsideTheRadius(int seed)
    {
        var rng = new Random(seed);
        var pts = Enumerable.Range(0, 400)
            .Select(_ => ((float)(rng.NextDouble() * 1000 - 500), (float)(rng.NextDouble() * 1000 - 500)))
            .ToList();

        var grid = new SpatialGrid<int>(Cell);
        for (var i = 0; i < pts.Count; i++) grid.Insert(i, pts[i].Item1, pts[i].Item2);

        for (var q = 0; q < 60; q++)
        {
            var qx = (float)(rng.NextDouble() * 1000 - 500);
            var qz = (float)(rng.NextDouble() * 1000 - 500);

            var got = new List<int>();
            grid.Query(qx, qz, Radius, got);
            var gotSet = got.ToHashSet();

            foreach (var expected in Brute(pts, qx, qz, Radius))
                Assert.True(gotSet.Contains(expected),
                    $"seed {seed}: entity {expected} at {pts[expected]} is within {Radius} of ({qx},{qz}) but broad phase omitted it");
        }
    }

    /// <summary>Negative world coordinates must bucket correctly. MathF.Floor (not truncation)
    /// is what makes this work; a refactor to (int) casting alone would break the -0..-1 band
    /// and only on one side of the origin.</summary>
    [Fact]
    public void NegativeCoordinates_BucketAndQueryCorrectly()
    {
        var grid = new SpatialGrid<string>(Cell);
        grid.Insert("neg", -10f, -10f);
        grid.Insert("pos", 10f, 10f);

        var got = new List<string>();
        grid.Query(-10f, -10f, 1f, got);
        Assert.Contains("neg", got);

        got.Clear();
        grid.Query(-200f, -200f, 1f, got);
        Assert.DoesNotContain("neg", got);
    }

    /// <summary>An entity sitting exactly on a cell edge belongs to exactly one bucket and is
    /// still found from both sides of the seam.</summary>
    [Fact]
    public void EntityOnCellBoundary_IsFoundFromBothSides()
    {
        var grid = new SpatialGrid<string>(Cell);
        grid.Insert("seam", Cell, Cell);

        foreach (var (qx, qz) in new[] { (Cell - 0.5f, Cell - 0.5f), (Cell + 0.5f, Cell + 0.5f) })
        {
            var got = new List<string>();
            grid.Query(qx, qz, 2f, got);
            Assert.Contains("seam", got);
        }
    }

    /// <summary>Clear() must empty the buckets. The tick rebuilds the grid every frame; a
    /// Clear that left stale entries would show last tick's positions to this tick's viewers.</summary>
    [Fact]
    public void Clear_RemovesAllEntitiesButGridStaysUsable()
    {
        var grid = new SpatialGrid<int>(Cell);
        grid.Insert(1, 0f, 0f);
        grid.Clear();

        var got = new List<int>();
        grid.Query(0f, 0f, Radius, got);
        Assert.Empty(got);

        grid.Insert(2, 0f, 0f);
        got.Clear();
        grid.Query(0f, 0f, Radius, got);
        Assert.Equal(new[] { 2 }, got);
    }

    /// <summary>Query APPENDS to the caller's list (documented contract, relied on by the
    /// scratch-list reuse in the tick). It must not clear what is already there.</summary>
    [Fact]
    public void Query_AppendsAndDoesNotClearCallerList()
    {
        var grid = new SpatialGrid<int>(Cell);
        grid.Insert(7, 0f, 0f);

        var got = new List<int> { 999 };
        grid.Query(0f, 0f, Radius, got);
        Assert.Equal(999, got[0]);
        Assert.Contains(7, got);
    }

    /// <summary>
    /// Determinism: identical insertion order must yield an identical result ORDER, not just an
    /// identical set. Downstream code that resolves "first target hit" off a query result would
    /// otherwise produce different combat outcomes from identical inputs.
    /// </summary>
    [Fact]
    public void Query_ResultOrder_IsDeterministicForIdenticalInsertionOrder()
    {
        static List<int> Run()
        {
            var g = new SpatialGrid<int>(Cell);
            for (var i = 0; i < 50; i++) g.Insert(i, i * 7f % 300f - 150f, i * 13f % 300f - 150f);
            var r = new List<int>();
            g.Query(0f, 0f, Radius, r);
            return r;
        }
        Assert.Equal(Run(), Run());
    }

    /// <summary>
    /// CHARACTERIZATION + LATENT DEFECT. A NaN (or infinite) coordinate does not throw:
    /// (int)MathF.Floor(NaN) is int.MinValue, so the entity is bucketed at
    /// (-2147483648, -2147483648) - a cell no realistic query range can ever span.
    ///
    /// Consequence: an entity whose X/Z went NaN upstream is still in _sessions, still
    /// simulated, still alive - and invisible to every AoI snapshot and every area-effect
    /// query, for every other player. It is the exact "player vanished, server thinks
    /// they are fine" desync, with no exception anywhere to catch.
    ///
    /// This test locks the behaviour so a NaN guard at the movement-integration site can be
    /// added deliberately rather than discovered in a playtest. If it starts failing, the
    /// float-to-int conversion changed and every caller needs rechecking.
    /// </summary>
    [Fact]
    public void NaNPosition_IsSilentlyBucketedOutOfReach_NotRejected()
    {
        var grid = new SpatialGrid<string>(Cell);

        var ex = Record.Exception(() => grid.Insert("ghost", float.NaN, float.NaN));
        Assert.Null(ex);                       // no loud failure

        // Not findable from the origin...
        var got = new List<string>();
        grid.Query(0f, 0f, Radius, got);
        Assert.DoesNotContain("ghost", got);

        // ...nor from anywhere a player could plausibly stand.
        foreach (var (qx, qz) in new[] { (0f, 0f), (500f, 500f), (-500f, -500f), (1e6f, 1e6f) })
        {
            got.Clear();
            grid.Query(qx, qz, 10_000f, got);
            Assert.DoesNotContain("ghost", got);
        }

        // It IS retained - it occupies a bucket, it is simply unreachable.
        Assert.Equal(1, BucketCount(grid));
    }

    /// <summary>Infinity collapses to the same unreachable cell as NaN.</summary>
    [Fact]
    public void InfinitePosition_CollapsesToTheSameUnreachableCell()
    {
        var grid = new SpatialGrid<string>(Cell);
        grid.Insert("posInf", float.PositiveInfinity, float.PositiveInfinity);
        grid.Insert("negInf", float.NegativeInfinity, float.NegativeInfinity);
        grid.Insert("nan", float.NaN, float.NaN);

        // All three share ONE bucket: +Inf, -Inf and NaN are indistinguishable after bucketing.
        Assert.Equal(1, BucketCount(grid));
    }

    /// <summary>
    /// CHARACTERIZATION: Clear() keeps bucket lists for GC reasons, but the bucket DICTIONARY
    /// never shrinks - a cell visited once is retained forever. Entities roaming a large world
    /// grow _cells without bound across a long server uptime.
    /// </summary>
    [Fact]
    public void BucketDictionary_NeverShrinks_AcrossRebuilds()
    {
        var grid = new SpatialGrid<int>(Cell);
        for (var i = 0; i < 500; i++)
        {
            grid.Clear();
            grid.Insert(i, i * Cell, i * Cell);   // a fresh cell every rebuild
        }
        Assert.Equal(500, BucketCount(grid));      // 1 live entity, 500 retained buckets
    }
}
