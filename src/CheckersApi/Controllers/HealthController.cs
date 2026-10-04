using CheckersApi.Engine;
using CheckersApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace CheckersApi.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    private readonly IEnginePool _pool;
    public HealthController(IEnginePool pool) => _pool = pool;

    [HttpGet("/healthz")]
    public IActionResult Health()
    {
        int ready = _pool.Ready;
        bool ok = ready > 0 && ready == _pool.Configured;
        var body = new HealthResponse(ok, ready, _pool.Configured, _pool.EngineName);
        return ok ? Ok(body) : StatusCode(503, body);
    }
}
