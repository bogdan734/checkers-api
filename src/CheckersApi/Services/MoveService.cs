using System.Diagnostics;
using System.Numerics;
using Checkers.Core;
using CheckersApi.Engine;
using CheckersApi.Models;
using Microsoft.Extensions.Options;

namespace CheckersApi.Services;

public sealed class MoveService
{
    private sealed record LevelPreset(int MaxDepth, int SoftTimeMs);

    private static readonly Dictionary<string, LevelPreset> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weak"] = new(7, 100),      // depth 6-8 or 100 ms, deterministic
        ["medium"] = new(11, 250),   // depth 10-12 or 250 ms
        ["strong"] = new(16, 550),   // depth 14-18 or 500-600 ms
    };

    private readonly IEnginePool _pool;
    private readonly LruCache<string, SuggestResponse> _cache;
    private readonly LimitsOptions _limits;

    public MoveService(IEnginePool pool, LruCache<string, SuggestResponse> cache, IOptions<LimitsOptions> limits)
    {
        _pool = pool;
        _cache = cache;
        _limits = limits.Value;
    }

    /// <summary>Hard timeout for a request, to be applied by the caller as a CancellationToken.</summary>
    public int ResolveHardTimeMs(SuggestRequest req) =>
        req.Limits?.HardTimeMs is > 0 and <= 120_000 and var h ? h : _limits.DefaultHardTimeMs;

    public async Task<SuggestResponse> SuggestAsync(SuggestRequest req, RequestMetrics metrics, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (req.State is null) throw ApiException.Unprocessable("invalid_request", "'state' is required.");
        if (req.State.Notation is { Length: > 0 } nt && !nt.Equals("PDN", StringComparison.OrdinalIgnoreCase))
            throw ApiException.Unprocessable("invalid_request", $"Unsupported notation '{nt}'; only PDN is supported.");

        var pos = ParsePosition(req.State.Position);
        var (levelName, depth, soft) = ResolveLimits(req);
        metrics.Level = levelName;

        string canonical = Pdn.Format(pos);
        string key = Pdn.PositionKey(pos);

        var buf = new Move[MoveGen.MaxMoves];
        int count = MoveGen.Generate(pos, buf);
        if (count == 0) throw ApiException.Unprocessable("no_legal_moves", "Side to move has no legal moves (game over).");

        string cacheKey = $"{canonical}|{levelName}|{depth}|{soft}";
        if (_cache.TryGet(cacheKey, out var cached))
        {
            Report(metrics, cached.Depth, cached.Nodes, cached.Info.TablebaseHit, cached: true);
            return cached with { Info = cached.Info with { TimeMs = (int)sw.ElapsedMilliseconds, Cached = true } };
        }

        var result = await _pool.SearchAsync(canonical, new EngineLimits(depth, soft, UseTablebase: true), ct);

        // Verify the engine's move is legal at the root: bestMove first, then the remaining PV entries; otherwise it is a server error.
        string? chosen = null;
        Move chosenMove = default;
        foreach (var candidate in new[] { result.BestMove }.Concat(result.Pv))
            if (Pdn.TryParseMove(pos, candidate, out chosenMove)) { chosen = Pdn.FormatMove(chosenMove); break; }
        if (chosen is null)
            throw new ApiException(500, "illegal_engine_move", $"Engine returned an illegal move '{result.BestMove}'.");

        var pv = result.Pv.Count > 0 && Pdn.TryParseMove(pos, result.Pv[0], out var m0) && Pdn.FormatMove(m0) == chosen
            ? result.Pv
            : new[] { chosen };

        var response = new SuggestResponse(_pool.EngineName, chosen, pv, result.ScoreOrWdl, result.Depth, result.Nodes, key,
            new SuggestInfo(result.TablebaseHit, (int)sw.ElapsedMilliseconds, false));
        _cache.Set(cacheKey, response);
        Report(metrics, result.Depth, result.Nodes, result.TablebaseHit, cached: false);
        return response;
    }

    public ValidateResponse Validate(ValidateRequest req)
    {
        var pos = ParsePosition(req.Position);
        return new ValidateResponse(Pdn.TryParseMove(pos, req.Move, out _));
    }

    public MovesResponse Moves(MovesRequest req)
    {
        var pos = ParsePosition(req.Position);
        var buf = new Move[MoveGen.MaxMoves];
        int n = MoveGen.Generate(pos, buf);
        var moves = new List<MoveInfo>(n);
        for (int i = 0; i < n; i++)
        {
            var m = buf[i];
            var path = Enumerable.Range(0, m.Len).Select(k => m.Square(k) + 1).ToList();
            var captured = new List<int>();
            for (uint b = m.Captured; b != 0; b &= b - 1) captured.Add(BitOperations.TrailingZeroCount(b) + 1);
            moves.Add(new MoveInfo(Pdn.FormatMove(m), m.From + 1, m.To + 1, path, captured, Pdn.Format(pos.Apply(m))));
        }
        string turn = pos.BlackToMove ? "black" : "white";
        return new MovesResponse(Pdn.Format(pos), Pdn.PositionKey(pos), turn, n == 0, n == 0 ? (pos.BlackToMove ? "white" : "black") : null, moves);
    }

    private static Position ParsePosition(string? text)
    {
        try { return Pdn.Parse(text); }
        catch (PdnException e) { throw ApiException.Unprocessable(e.Code, e.Message); }
    }

    private (string level, int depth, int soft) ResolveLimits(SuggestRequest req)
    {
        string levelName = "custom";
        LevelPreset preset = new(14, _limits.DefaultSoftTimeMs);
        if (!string.IsNullOrWhiteSpace(req.Level))
        {
            if (!Levels.TryGetValue(req.Level.Trim(), out var p))
                throw ApiException.Unprocessable("invalid_level", $"Level must be one of weak, medium, strong; got '{req.Level}'.");
            levelName = req.Level.Trim().ToLowerInvariant();
            preset = p;
        }
        var lim = req.Limits;
        if (lim?.MaxDepth is { } d and (< 1 or > 64)) throw ApiException.Unprocessable("invalid_limits", "limits.maxDepth must be 1..64.");
        if (lim?.SoftTimeMs is { } s and (< 1 or > 60_000)) throw ApiException.Unprocessable("invalid_limits", "limits.softTimeMs must be 1..60000.");
        if (lim?.HardTimeMs is { } h and (< 1 or > 120_000)) throw ApiException.Unprocessable("invalid_limits", "limits.hardTimeMs must be 1..120000.");
        return (levelName, lim?.MaxDepth ?? preset.MaxDepth, lim?.SoftTimeMs ?? preset.SoftTimeMs);
    }

    private static void Report(RequestMetrics m, int depth, long nodes, bool tb, bool cached)
    {
        m.Depth = depth;
        m.Nodes = nodes;
        m.TablebaseHit = tb;
        m.Cached = cached;
    }
}
