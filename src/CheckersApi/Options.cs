namespace CheckersApi;

public sealed class EngineOptions
{
    /// <summary>"chinook" (external process pool) or "stub" (built-in alpha-beta engine).</summary>
    public string Type { get; set; } = "stub";
    public string Path { get; set; } = "";
    public int Workers { get; set; } = 2;
    public string Databases { get; set; } = "";
    /// <summary>Extra command-line arguments for the engine executable (before --db).</summary>
    public string Args { get; set; } = "";
    /// <summary>Max pieces of the built-in tablebase used by the stub engine (0-4, default 3).</summary>
    public int TablebasePieces { get; set; } = 3;
    public int StartupTimeoutMs { get; set; } = 20000;
}

public sealed class CacheOptions
{
    public int Capacity { get; set; } = 20000;
    public int TtlMinutes { get; set; } = 15;
}

public sealed class LimitsOptions
{
    public int DefaultSoftTimeMs { get; set; } = 300;
    public int DefaultHardTimeMs { get; set; } = 1200;
}
