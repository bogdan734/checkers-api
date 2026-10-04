namespace CheckersApi.Engine;

public interface IEnginePool
{
    string EngineName { get; }
    int Configured { get; }
    int Ready { get; }
    Task WarmUpAsync(CancellationToken ct);
    Task<EngineSearchResult> SearchAsync(string pdn, EngineLimits limits, CancellationToken ct);
}

/// <summary>
/// Fixed pool of long-lived workers. Requests are spread round-robin; each worker has an async lock so only one
/// search runs on it at a time. Starting from the round-robin slot we prefer a worker that is idle right now.
/// </summary>
public sealed class EnginePool : IEnginePool, IAsyncDisposable
{
    private sealed class Worker
    {
        public required IEngineAdapter Adapter { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public volatile bool Ready;
    }

    private readonly Worker[] _workers;
    private int _next = -1;

    public EnginePool(IEnumerable<IEngineAdapter> adapters)
    {
        _workers = adapters.Select(a => new Worker { Adapter = a }).ToArray();
        if (_workers.Length == 0) throw new ArgumentException("At least one engine worker is required.");
    }

    public string EngineName => _workers[0].Adapter.Name;
    public int Configured => _workers.Length;
    public int Ready => _workers.Count(w => w.Ready);

    public async Task WarmUpAsync(CancellationToken ct)
    {
        await Task.WhenAll(_workers.Select(async w =>
        {
            await w.Gate.WaitAsync(ct);
            try
            {
                await w.Adapter.StartAsync(ct);
                w.Ready = true;
            }
            finally { w.Gate.Release(); }
        }));
    }

    public async Task<EngineSearchResult> SearchAsync(string pdn, EngineLimits limits, CancellationToken ct)
    {
        int start = (int)((uint)Interlocked.Increment(ref _next) % (uint)_workers.Length);
        Worker? chosen = null;
        for (int k = 0; k < _workers.Length && chosen is null; k++)
        {
            var w = _workers[(start + k) % _workers.Length];
            if (w.Gate.Wait(0)) chosen = w;
        }
        if (chosen is null)
        {
            chosen = _workers[start];
            await chosen.Gate.WaitAsync(ct);
        }
        try
        {
            await chosen.Adapter.SetPositionAsync(pdn, ct);
            var result = await chosen.Adapter.SearchAsync(limits, ct);
            chosen.Ready = true;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            chosen.Ready = false; // the adapter respawns lazily; healthz reflects the failure until the next success
            throw;
        }
        finally { chosen.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _workers) await w.Adapter.DisposeAsync();
    }
}
