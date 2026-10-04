using System.Net;
using System.Text.Json;
using Checkers.Core;

namespace CheckersApi.Tests.Api;

public class AcceptanceTests : IClassFixture<StubApiFactory>
{
    private const string Midgame = "B:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12";
    private readonly StubApiFactory _f;
    private readonly HttpClient _c;

    public AcceptanceTests(StubApiFactory f)
    {
        _f = f;
        _c = f.CreateClient();
    }

    [Fact]
    public async Task Healthz_is_ok_on_startup_with_two_workers()
    {
        var res = await _c.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.Equal(2, j.GetProperty("workers").GetInt32());
    }

    [Fact]
    public async Task Tablebase_position_returns_fast_with_tablebaseHit()
    {
        await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest("W:WK14:BK1,10")); // warm request path
        var (res, body, wall) = await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest("B:WK14:BK1,10"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.True(wall < 50, $"took {wall} ms");
        Assert.Equal(JsonValueKind.String, body.GetProperty("scoreOrWDL").ValueKind);
        Assert.Equal("stub-alphabeta", body.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Midgame_strong_returns_legal_move_under_600ms()
    {
        await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest("W:W18,22,25,26,27,29,30:B1,3,6,7,9,10,12", "weak"));
        var (res, body, wall) = await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest(Midgame));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(wall < 600, $"took {wall} ms");
        string move = body.GetProperty("bestMove").GetString()!;
        Assert.True(Pdn.TryParseMove(Pdn.Parse(Midgame), move, out _), $"illegal {move}");
        Assert.False(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.True(body.GetProperty("depth").GetInt32() >= 1);
        Assert.True(body.GetProperty("nodes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Number, body.GetProperty("scoreOrWDL").ValueKind);
        Assert.Equal(16, body.GetProperty("positionKey").GetString()!.Length);
        Assert.True(body.GetProperty("pv").GetArrayLength() >= 1);
    }

    [Theory]
    [InlineData("not a position")]
    [InlineData("B:W33:B1")]
    [InlineData("B:W5:B5")]
    [InlineData("")]
    public async Task Invalid_pdn_returns_422(string pdn)
    {
        var (res, body, _) = await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest(pdn));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Unknown_level_and_missing_state_return_422()
    {
        var (r1, _, _) = await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest(Midgame, "godlike"));
        Assert.Equal((HttpStatusCode)422, r1.StatusCode);
        var (r2, _, _) = await Http.PostAsync(_c, "/v1/move/suggest", new { gameId = "x" });
        Assert.Equal((HttpStatusCode)422, r2.StatusCode);
    }

    [Fact]
    public async Task Hard_timeout_returns_504()
    {
        var (res, body, wall) = await Http.PostAsync(_c, "/v1/move/suggest",
            Http.Suggest(Midgame, "strong", new { maxDepth = 40, softTimeMs = 20000, hardTimeMs = 30 }));
        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);
        Assert.Equal("timeout", body.GetProperty("code").GetString());
        Assert.True(wall < 1000, $"took {wall} ms");
        // the pool stays healthy afterwards
        var h = await _c.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, h.StatusCode);
    }

    [Fact]
    public async Task Position_without_moves_returns_422()
    {
        var (res, body, _) = await Http.PostAsync(_c, "/v1/move/suggest", Http.Suggest("B:W5,6,9,10,13,14,17,18:B1")); // black man 1 blocked? ensure no moves
        // 1 can move to 5/6 only if empty; both occupied and jumps blocked (9,10 occupied) => no moves
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Equal("no_legal_moves", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validate_endpoint_reports_legality()
    {
        var (ok, b1, _) = await Http.PostAsync(_c, "/v1/move/validate", new { position = "start", move = "11-15" });
        Assert.True(b1.GetProperty("legal").GetBoolean());
        var (_, b2, _) = await Http.PostAsync(_c, "/v1/move/validate", new { position = "start", move = "11-26" });
        Assert.False(b2.GetProperty("legal").GetBoolean());
        var (bad, _, _) = await Http.PostAsync(_c, "/v1/move/validate", new { position = "junk", move = "11-15" });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Moves_endpoint_lists_legal_moves_with_resulting_positions()
    {
        var (res, body, _) = await Http.PostAsync(_c, "/v1/position/moves", new { position = "start" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(7, body.GetProperty("moves").GetArrayLength());
        Assert.Equal("black", body.GetProperty("turn").GetString());
        var first = body.GetProperty("moves")[0];
        Assert.StartsWith("W:", first.GetProperty("position").GetString());
    }

    [Fact]
    public async Task Repeated_request_is_served_from_cache()
    {
        var req = Http.Suggest("B:W17,21,22,25,26,29,30:B2,3,6,7,10,11,12", "weak");
        var (_, first, _) = await Http.PostAsync(_c, "/v1/move/suggest", req);
        var (_, second, _) = await Http.PostAsync(_c, "/v1/move/suggest", req);
        Assert.False(first.GetProperty("info").GetProperty("cached").GetBoolean());
        Assert.True(second.GetProperty("info").GetProperty("cached").GetBoolean());
        Assert.Equal(first.GetProperty("bestMove").GetString(), second.GetProperty("bestMove").GetString());
    }

    [Fact]
    public async Task Every_request_writes_one_json_log_line_with_core_fields()
    {
        var req = Http.Suggest("B:WK14:BK1,9", "medium");
        await Http.PostAsync(_c, "/v1/move/suggest", req);
        var lines = _f.Sink.Lines.Select(l => JsonDocument.Parse(l).RootElement).Where(j => j.GetProperty("path").GetString() == "/v1/move/suggest").ToList();
        var j = lines.Last();
        Assert.False(string.IsNullOrEmpty(j.GetProperty("requestId").GetString()));
        Assert.True(j.GetProperty("timeMs").GetInt32() >= 0);
        Assert.Equal(200, j.GetProperty("status").GetInt32());
        Assert.True(j.TryGetProperty("depth", out _));
        Assert.True(j.TryGetProperty("nodes", out _));
        Assert.True(j.TryGetProperty("tablebaseHit", out _));
    }

    [Fact]
    public async Task Request_id_header_is_echoed()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("X-Request-Id", "abc-123");
        var res = await _c.SendAsync(req);
        Assert.Equal("abc-123", res.Headers.GetValues("X-Request-Id").Single());
    }
}
