using CheckersApi.Engine;

namespace CheckersApi.Services;

/// <summary>Warms all engine workers before the host starts accepting traffic.</summary>
public sealed class EngineWarmupService : IHostedService
{
    private readonly IEnginePool _pool;
    private readonly ILogger<EngineWarmupService> _log;

    public EngineWarmupService(IEnginePool pool, ILogger<EngineWarmupService> log)
    {
        _pool = pool;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await _pool.WarmUpAsync(ct);
            _log.LogInformation("Engine pool ready: {Ready}/{Configured} workers ({Engine})", _pool.Ready, _pool.Configured, _pool.EngineName);
        }
        catch (Exception ex)
        {
            // Stay up so /healthz can report 503 with ok=false instead of crashing the IIS worker in a loop.
            _log.LogError(ex, "Engine warm-up failed; /healthz will report not ready.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
