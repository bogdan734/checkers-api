using CheckersApi.Engine;
using CheckersApi.Services;

namespace CheckersApi.Tests.Api;

public class PoolAndCacheTests
{
    private sealed class FakeAdapter : IEngineAdapter
    {
        public int Id { get; init; }
        public static int MaxConcurrentPerWorker;
        private int _running;
        public int Calls;
        public int Delay { get; init; }
        public string Name => "fake";
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SetPositionAsync(string pdn, CancellationToken ct) => Task.CompletedTask;
        public async Task<EngineSearchResult> SearchAsync(EngineLimits l, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref _running);
            if (now > 1) MaxConcurrentPerWorker = Math.Max(MaxConcurrentPerWorker, now);
            Interlocked.Increment(ref Calls);
            try { await Task.Delay(Delay, ct); }
            finally { Interlocked.Decrement(ref _running); }
            return new EngineSearchResult("11-15", new[] { "11-15" }, ScoreOrWdl.FromCp(0), 1, 1, false);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Round_robin_alternates_between_idle_workers()
    {
        var a = new FakeAdapter { Id = 0 };
        var b = new FakeAdapter { Id = 1 };
        var pool = new EnginePool(new[] { a, b });
        await pool.WarmUpAsync(default);
        for (int i = 0; i < 10; i++) await pool.SearchAsync("start", new EngineLimits(1, 1, false), default);
        Assert.Equal(5, a.Calls);
        Assert.Equal(5, b.Calls);
        Assert.Equal(2, pool.Ready);
    }

    [Fact]
    public async Task Per_worker_lock_prevents_concurrent_use_of_one_worker()
    {
        FakeAdapter.MaxConcurrentPerWorker = 0;
        var a = new FakeAdapter { Delay = 20 };
        var b = new FakeAdapter { Delay = 20 };
        var pool = new EnginePool(new[] { a, b });
        await pool.WarmUpAsync(default);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => pool.SearchAsync("start", new EngineLimits(1, 1, false), default)));
        Assert.Equal(12, a.Calls + b.Calls);
        Assert.Equal(0, FakeAdapter.MaxConcurrentPerWorker);
    }

    [Fact]
    public async Task Waiting_for_busy_worker_honours_cancellation()
    {
        var a = new FakeAdapter { Delay = 500 };
        var pool = new EnginePool(new[] { a });
        await pool.WarmUpAsync(default);
        var running = pool.SearchAsync("start", new EngineLimits(1, 1, false), default);
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.SearchAsync("start", new EngineLimits(1, 1, false), cts.Token));
        await running;
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Lru_evicts_least_recently_used_beyond_capacity()
    {
        var c = new LruCache<string, int>(2, TimeSpan.FromMinutes(15));
        c.Set("a", 1); c.Set("b", 2);
        Assert.True(c.TryGet("a", out _));   // a is now most recent
        c.Set("c", 3);                       // evicts b
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out _));
        Assert.True(c.TryGet("c", out _));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Cache_entries_expire_after_ttl()
    {
        var t = new FakeTime();
        var c = new LruCache<string, int>(10, TimeSpan.FromMinutes(15), t);
        c.Set("k", 7);
        t.Now += TimeSpan.FromMinutes(14);
        Assert.True(c.TryGet("k", out var v) && v == 7);
        t.Now += TimeSpan.FromMinutes(2);
        Assert.False(c.TryGet("k", out _));
        Assert.Equal(0, c.Count);
    }
}
