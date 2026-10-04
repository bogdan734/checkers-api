using System.Diagnostics;
using System.Text.Json;
using CheckersApi.Models;

namespace CheckersApi.Services;

public interface IRequestLogSink
{
    void Write(string jsonLine);
}

/// <summary>One JSON object per line on stdout (captured by IIS stdout log / docker logs).</summary>
public sealed class ConsoleRequestLogSink : IRequestLogSink
{
    public void Write(string jsonLine) => Console.Out.WriteLine(jsonLine);
}

public sealed class RequestLoggingMiddleware
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly RequestDelegate _next;
    private readonly IRequestLogSink _sink;

    public RequestLoggingMiddleware(RequestDelegate next, IRequestLogSink sink)
    {
        _next = next;
        _sink = sink;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        bool log = path.StartsWithSegments("/v1") || path.StartsWithSegments("/healthz");

        string id = ctx.Request.Headers.TryGetValue("X-Request-Id", out var h) && IsSafeId(h.ToString())
            ? h.ToString() : Guid.NewGuid().ToString("N");
        ctx.TraceIdentifier = id;
        ctx.Response.Headers["X-Request-Id"] = id;
        var metrics = new RequestMetrics();
        ctx.Items[typeof(RequestMetrics)] = metrics;

        var sw = Stopwatch.StartNew();
        try { await _next(ctx); }
        finally
        {
            if (log)
            {
                _sink.Write(JsonSerializer.Serialize(new
                {
                    ts = DateTimeOffset.UtcNow,
                    requestId = id,
                    method = ctx.Request.Method,
                    path = path.Value,
                    status = ctx.Response.StatusCode,
                    timeMs = (int)sw.ElapsedMilliseconds,
                    level = metrics.Level,
                    depth = metrics.Depth,
                    nodes = metrics.Nodes,
                    tablebaseHit = metrics.TablebaseHit,
                    cached = metrics.Cached,
                }, Json));
            }
        }
    }

    private static bool IsSafeId(string s) => s.Length is > 0 and <= 64 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

public static class RequestMetricsExtensions
{
    public static RequestMetrics Metrics(this HttpContext ctx) =>
        ctx.Items.TryGetValue(typeof(RequestMetrics), out var m) && m is RequestMetrics rm ? rm : new RequestMetrics();
}
