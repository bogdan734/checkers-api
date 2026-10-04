using System.Text.Json.Serialization;
using CheckersApi.Engine;

namespace CheckersApi.Models;

public sealed class SuggestRequest
{
    public string? GameId { get; set; }
    public SuggestState? State { get; set; }
    public string? Level { get; set; }
    public SuggestLimits? Limits { get; set; }
}

public sealed class SuggestState
{
    public string? Notation { get; set; }
    public string? Position { get; set; }
}

public sealed class SuggestLimits
{
    public int? MaxDepth { get; set; }
    public int? SoftTimeMs { get; set; }
    public int? HardTimeMs { get; set; }
}

public sealed record SuggestResponse(
    string Engine,
    string BestMove,
    IReadOnlyList<string> Pv,
    [property: JsonPropertyName("scoreOrWDL")] ScoreOrWdl ScoreOrWdl,
    int Depth,
    long Nodes,
    string PositionKey,
    SuggestInfo Info);

public sealed record SuggestInfo(bool TablebaseHit, int TimeMs, bool Cached);

public sealed class ValidateRequest
{
    public string? Position { get; set; }
    public string? Move { get; set; }
}

public sealed record ValidateResponse(bool Legal);

public sealed class MovesRequest
{
    public string? Position { get; set; }
}

public sealed record MoveInfo(string Move, int From, int To, IReadOnlyList<int> Path, IReadOnlyList<int> Captured, string Position);

public sealed record MovesResponse(string Position, string PositionKey, string Turn, bool GameOver, string? Winner, IReadOnlyList<MoveInfo> Moves);

public sealed record HealthResponse(bool Ok, int Workers, int Configured, string Engine);

public sealed record ApiError(string Code, string Message, string? RequestId);

/// <summary>Per-request metrics the controller reports back to the logging middleware.</summary>
public sealed class RequestMetrics
{
    public int? Depth { get; set; }
    public long? Nodes { get; set; }
    public bool? TablebaseHit { get; set; }
    public bool? Cached { get; set; }
    public string? Level { get; set; }
}
