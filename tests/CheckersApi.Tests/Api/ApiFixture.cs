using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using CheckersApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CheckersApi.Tests.Api;

public sealed class CapturingSink : IRequestLogSink
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Write(string jsonLine) => Lines.Enqueue(jsonLine);
}

public class StubApiFactory : WebApplicationFactory<Program>
{
    public CapturingSink Sink { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Engine:Type", "stub");
        builder.UseSetting("Engine:Workers", "2");
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IRequestLogSink>(Sink)));
    }
}

public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<(HttpResponseMessage res, JsonElement body, long wallMs)> PostAsync(HttpClient c, string url, object payload)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await c.PostAsJsonAsync(url, payload, Json);
        long ms = sw.ElapsedMilliseconds;
        var text = await res.Content.ReadAsStringAsync();
        var body = text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (res, body, ms);
    }

    public static object Suggest(string pdn, string? level = "strong", object? limits = null) => new
    {
        gameId = "g1",
        state = new { notation = "PDN", position = pdn },
        level,
        limits,
    };
}
