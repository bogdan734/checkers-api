using System.Diagnostics;
using Checkers.Core;

namespace CheckersApi.Tests.Core;

public class EngineCoreTests
{
    [Fact]
    public void Tablebase_builds_quickly_and_solves_trivial_win()
    {
        var sw = Stopwatch.StartNew();
        var tb = EndgameTablebase.GetOrBuild(3);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"build took {sw.Elapsed}");
        Assert.True(tb.Entries > 100_000);

        var p = Pdn.Parse("B:W26:B22");
        var r = tb.Probe(p);
        Assert.NotNull(r);
        Assert.Equal(Wdl.Win, r!.Result);
        Assert.Equal(1, r.Plies);
        Assert.Equal("22x31", Pdn.FormatMove(r.Best));
    }

    [Fact]
    public void Tablebase_probe_is_instant_after_build()
    {
        var tb = EndgameTablebase.GetOrBuild(3);
        var p = Pdn.Parse("B:WK14:BK1,10");
        tb.Probe(p); // warm JIT
        var sw = Stopwatch.StartNew();
        var r = tb.Probe(p);
        Assert.True(sw.ElapsedMilliseconds < 20);
        Assert.NotNull(r);
    }

    [Fact]
    public void Tablebase_returns_null_above_limit()
    {
        var tb = EndgameTablebase.GetOrBuild(3);
        Assert.Null(tb.Probe(Position.Start));
    }

    [Fact]
    public void Search_takes_free_capture_and_respects_soft_time()
    {
        var p = Pdn.Parse("B:W18,28,31,32:B9,12,13,14");
        var s = new Searcher();
        var sw = Stopwatch.StartNew();
        var o = s.Search(p, 12, 200);
        Assert.NotNull(o);
        Assert.True(sw.ElapsedMilliseconds < 600);
        var buf = new Move[MoveGen.MaxMoves];
        int n = MoveGen.Generate(p, buf);
        Assert.Contains(Enumerable.Range(0, n), i => buf[i] == o!.Best);
    }

    [Fact]
    public void Search_is_deterministic_for_fixed_depth()
    {
        var p = Pdn.Parse("B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12");
        var a = new Searcher().Search(p, 7, 5000)!;
        var b = new Searcher().Search(p, 7, 5000)!;
        Assert.Equal(Pdn.FormatMove(a.Best), Pdn.FormatMove(b.Best));
        Assert.Equal(a.ScoreCp, b.ScoreCp);
    }

    [Fact]
    public void Search_cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new Searcher().Search(Position.Start, 18, 5000, cts.Token));
    }

    [Fact]
    public void Start_position_strong_search_reaches_useful_depth()
    {
        var o = new Searcher().Search(Position.Start, 16, 550)!;
        Assert.InRange(o.ElapsedMs, 0, 700);
        Assert.True(o.Depth >= 6, $"depth {o.Depth} nodes {o.Nodes}");
    }
}
