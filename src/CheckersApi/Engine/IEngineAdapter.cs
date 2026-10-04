using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheckersApi.Engine;

/// <summary>Centipawn score (from side to move) or an exact tablebase result. Serialised as a number or "WIN"/"DRAW"/"LOSS".</summary>
[JsonConverter(typeof(ScoreOrWdlConverter))]
public readonly record struct ScoreOrWdl(int? Cp, string? Wdl)
{
    public static ScoreOrWdl FromCp(int cp) => new(cp, null);
    public static ScoreOrWdl FromWdl(string wdl) => new(null, wdl);
}

public sealed class ScoreOrWdlConverter : JsonConverter<ScoreOrWdl>
{
    public override ScoreOrWdl Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? ScoreOrWdl.FromCp(reader.GetInt32()) : ScoreOrWdl.FromWdl(reader.GetString() ?? "DRAW");

    public override void Write(Utf8JsonWriter writer, ScoreOrWdl value, JsonSerializerOptions options)
    {
        if (value.Cp is { } cp) writer.WriteNumberValue(cp);
        else writer.WriteStringValue(value.Wdl);
    }
}

public sealed record EngineLimits(int MaxDepth, int SoftTimeMs, bool UseTablebase);

public sealed record EngineSearchResult(
    string BestMove,
    IReadOnlyList<string> Pv,
    ScoreOrWdl ScoreOrWdl,
    int Depth,
    long Nodes,
    bool TablebaseHit);

/// <summary>
/// One long-lived engine worker. Not thread-safe: the pool serialises access with a per-worker async lock.
/// </summary>
public interface IEngineAdapter : IAsyncDisposable
{
    string Name { get; }
    /// <summary>Starts and warms the worker (spawn process, load databases).</summary>
    Task StartAsync(CancellationToken ct);
    /// <summary>Sets the position (canonical PDN) the next search runs on.</summary>
    Task SetPositionAsync(string pdn, CancellationToken ct);
    /// <summary>Searches the current position. softTimeMs/maxDepth are enforced by the engine; cancellation aborts the search.</summary>
    Task<EngineSearchResult> SearchAsync(EngineLimits limits, CancellationToken ct);
}
