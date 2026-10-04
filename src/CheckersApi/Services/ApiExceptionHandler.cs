using Checkers.Core;
using CheckersApi.Models;
using Microsoft.AspNetCore.Diagnostics;

namespace CheckersApi.Services;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _log;
    public ApiExceptionHandler(ILogger<ApiExceptionHandler> log) => _log = log;

    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        (int status, string code, string message) = ex switch
        {
            ApiException a => (a.Status, a.Code, a.Message),
            PdnException p => (422, p.Code, p.Message),
            BadHttpRequestException b => (400, "bad_request", b.Message),
            _ => (500, "internal_error", "Unexpected server error."),
        };
        if (status >= 500) _log.LogError(ex, "Request {RequestId} failed", ctx.TraceIdentifier);
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new ApiError(code, message, ctx.TraceIdentifier), ct);
        return true;
    }
}
