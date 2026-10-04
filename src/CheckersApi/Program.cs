using Checkers.Core;
using CheckersApi;
using CheckersApi.Engine;
using CheckersApi.Models;
using CheckersApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("Engine"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<LimitsOptions>(builder.Configuration.GetSection("Limits"));

builder.Services.AddControllers().ConfigureApiBehaviorOptions(o =>
{
    // Binding/validation failures are semantic request errors -> 422 with the common error shape.
    o.InvalidModelStateResponseFactory = ctx => new ObjectResult(new ApiError("invalid_request",
        string.Join("; ", ctx.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)), ctx.HttpContext.TraceIdentifier))
    { StatusCode = 422 };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddSingleton<IRequestLogSink, ConsoleRequestLogSink>();
builder.Services.AddSingleton(sp =>
{
    var c = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
    return new LruCache<string, SuggestResponse>(c.Capacity, TimeSpan.FromMinutes(c.TtlMinutes));
});
builder.Services.AddSingleton<IEnginePool>(sp =>
{
    var opt = sp.GetRequiredService<IOptions<EngineOptions>>().Value;
    int workers = Math.Max(1, opt.Workers);
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Engine");
    IEnumerable<IEngineAdapter> adapters = opt.Type.ToLowerInvariant() switch
    {
        "chinook" => Enumerable.Range(0, workers).Select(_ => (IEngineAdapter)new ChinookEngineAdapter(opt, log)).ToList(),
        "stub" => BuildStubWorkers(opt, workers),
        _ => throw new InvalidOperationException($"Unknown Engine:Type '{opt.Type}' (expected 'chinook' or 'stub').")
    };
    return new EnginePool(adapters);
});
builder.Services.AddSingleton<MoveService>();
builder.Services.AddHostedService<EngineWarmupService>();

var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();

static List<IEngineAdapter> BuildStubWorkers(EngineOptions opt, int workers)
{
    var tb = EndgameTablebase.GetOrBuild(opt.TablebasePieces);
    return Enumerable.Range(0, workers).Select(_ => (IEngineAdapter)new StubEngineAdapter(tb)).ToList();
}

public partial class Program { }
