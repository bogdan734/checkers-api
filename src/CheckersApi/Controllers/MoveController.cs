using CheckersApi.Models;
using CheckersApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CheckersApi.Controllers;

[ApiController]
public sealed class MoveController : ControllerBase
{
    private readonly MoveService _service;
    public MoveController(MoveService service) => _service = service;

    /// <summary>Best move for a PDN position. hardTimeMs is enforced here via a CancellationToken (504 on expiry).</summary>
    [HttpPost("/v1/move/suggest")]
    public async Task<ActionResult<SuggestResponse>> Suggest([FromBody] SuggestRequest request)
    {
        using var hard = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        hard.CancelAfter(_service.ResolveHardTimeMs(request));
        try
        {
            return await _service.SuggestAsync(request, HttpContext.Metrics(), hard.Token);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw new ApiException(504, "timeout", "Engine did not answer within hardTimeMs.");
        }
    }

    [HttpPost("/v1/move/validate")]
    public ValidateResponse Validate([FromBody] ValidateRequest request) => _service.Validate(request);

    /// <summary>All legal moves with the resulting positions (used by the web board; keeps rules server-side).</summary>
    [HttpPost("/v1/position/moves")]
    public MovesResponse Moves([FromBody] MovesRequest request) => _service.Moves(request);
}
