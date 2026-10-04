using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CheckersApi.Tests.Api;

/// <summary>Exercises ChinookEngineAdapter against the reference line-protocol process (CheckersApi.EngineCli).</summary>
public class ChinookProcessTests
{
    private static WebApplicationFactory<Program> Factory(string path, string args) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Engine:Type", "chinook");
            b.UseSetting("Engine:Path", path);
            b.UseSetting("Engine:Args", args);
            b.UseSetting("Engine:Workers", "2");
            b.UseSetting("Engine:StartupTimeoutMs", "15000");
        });

    private static string CliDll()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "CheckersApi.EngineCli.dll");
        Assert.True(File.Exists(dll), $"missing {dll}");
        return dll;
    }

    [Fact]
    public async Task Process_pool_warms_and_serves_tablebase_and_search_requests()
    {
        await using var f = Factory("dotnet", $"\"{CliDll()}\" --tb 3");
        var c = f.CreateClient();

        var h = JsonDocument.Parse(await c.GetStringAsync("/healthz")).RootElement;
        Assert.True(h.GetProperty("ok").GetBoolean());
        Assert.Equal(2, h.GetProperty("workers").GetInt32());
        Assert.Equal("chinook", h.GetProperty("engine").GetString());

        var (r1, tb, _) = await Http.PostAsync(c, "/v1/move/suggest", Http.Suggest("B:W26:B22"));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.True(tb.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.Equal("22x31", tb.GetProperty("bestMove").GetString());
        Assert.Equal("WIN", tb.GetProperty("scoreOrWDL").GetString());

        var (r2, mid, wall) = await Http.PostAsync(c, "/v1/move/suggest", Http.Suggest("B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12", "medium"));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.False(mid.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.True(mid.GetProperty("depth").GetInt32() >= 1);
        Assert.True(wall < 1200);
    }

    [Fact]
    public async Task Hard_timeout_kills_worker_and_pool_recovers()
    {
        await using var f = Factory("dotnet", $"\"{CliDll()}\"");
        var c = f.CreateClient();
        var slow = Http.Suggest("B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12", "strong", new { maxDepth = 40, softTimeMs = 20000, hardTimeMs = 100 });
        var (res, _, _) = await Http.PostAsync(c, "/v1/move/suggest", slow);
        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);

        // both workers must still answer (the killed one respawns lazily)
        for (int i = 0; i < 4; i++)
        {
            var (ok, _, _) = await Http.PostAsync(c, "/v1/move/suggest",
                Http.Suggest($"B:W18,22,25,26,27,29,{30 + (i % 2)}:B1,3,6,7,9,10,12", "weak", new { hardTimeMs = 5000 }));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }

    [Fact]
    public async Task Missing_engine_binary_makes_healthz_report_not_ready()
    {
        await using var f = Factory(Path.Combine(Path.GetTempPath(), "no-such-chinook.exe"), "");
        var c = f.CreateClient();
        var res = await c.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var h = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(h.GetProperty("ok").GetBoolean());
        Assert.Equal(0, h.GetProperty("workers").GetInt32());
    }
}
