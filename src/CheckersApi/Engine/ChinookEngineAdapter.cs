using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace CheckersApi.Engine;

/// <summary>
/// One long-lived external engine process (Chinook / KingsRow shim) driven over a line protocol on stdin/stdout:
/// <code>
/// isready                                -> readyok
/// position &lt;canonical pdn&gt;
/// go depth &lt;d&gt; time &lt;ms&gt; tb &lt;0|1&gt;     -> info depth &lt;d&gt; nodes &lt;n&gt; [time &lt;ms&gt;] score cp &lt;x&gt;|wdl &lt;WIN|DRAW|LOSS&gt; tb &lt;0|1&gt; pv &lt;m1&gt; &lt;m2&gt; ...
///                                           bestmove &lt;move&gt;|none
/// quit
/// </code>
/// If the process dies or a search is cancelled (hard timeout) it is killed and respawned on the next request.
/// </summary>
public sealed class ChinookEngineAdapter : IEngineAdapter
{
    private readonly EngineOptions _opt;
    private readonly ILogger _log;
    private Process? _proc;
    private string? _position;

    public ChinookEngineAdapter(EngineOptions options, ILogger log)
    {
        _opt = options;
        _log = log;
    }

    public string Name => "chinook";

    public Task StartAsync(CancellationToken ct) => EnsureStartedAsync(ct);

    public async Task SetPositionAsync(string pdn, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);
        _position = pdn;
        await WriteAsync($"position {pdn}", ct);
    }

    public async Task<EngineSearchResult> SearchAsync(EngineLimits limits, CancellationToken ct)
    {
        if (_position is null) throw new InvalidOperationException("No position set.");
        await EnsureStartedAsync(ct);
        var proc = _proc!;
        using var reg = ct.Register(() => Kill(proc));
        try
        {
            await WriteAsync($"go depth {limits.MaxDepth} time {limits.SoftTimeMs} tb {(limits.UseTablebase ? 1 : 0)}", ct);
            string? best = null;
            EngineSearchResult? info = null;
            while (true)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line is null)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Engine process exited unexpectedly.");
                }
                line = line.Trim();
                if (line.StartsWith("info ")) info = ParseInfo(line);
                else if (line.StartsWith("bestmove ")) { best = line[9..].Trim(); break; }
                else if (line.StartsWith("error ")) _log.LogWarning("Engine error: {Line}", line);
            }
            if (best is null or "none") throw new InvalidOperationException("Engine returned no move.");
            var pv = info?.Pv ?? new[] { best };
            return (info ?? new EngineSearchResult(best, pv, ScoreOrWdl.FromCp(0), 0, 0, false)) with { BestMove = best, Pv = pv };
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Kill(proc);
            throw;
        }
    }

    private static EngineSearchResult ParseInfo(string line)
    {
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int depth = 0; long nodes = 0; bool tb = false; var score = ScoreOrWdl.FromCp(0);
        var pv = new List<string>();
        for (int i = 1; i < t.Length; i++)
        {
            switch (t[i])
            {
                case "depth" when i + 1 < t.Length: depth = int.Parse(t[++i], CultureInfo.InvariantCulture); break;
                case "nodes" when i + 1 < t.Length: nodes = long.Parse(t[++i], CultureInfo.InvariantCulture); break;
                case "time" when i + 1 < t.Length: i++; break;
                case "tb" when i + 1 < t.Length: tb = t[++i] == "1"; break;
                case "score" when i + 2 < t.Length:
                    score = t[i + 1] == "wdl" ? ScoreOrWdl.FromWdl(t[i + 2].ToUpperInvariant()) : ScoreOrWdl.FromCp(int.Parse(t[i + 2], CultureInfo.InvariantCulture));
                    i += 2; break;
                case "pv": pv.AddRange(t[(i + 1)..]); i = t.Length; break;
            }
        }
        return new EngineSearchResult(pv.FirstOrDefault() ?? "", pv, score, depth, nodes, tb);
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_proc is { HasExited: false }) return;
        if (string.IsNullOrWhiteSpace(_opt.Path)) throw new InvalidOperationException("Engine:Path is not configured.");

        var args = _opt.Args ?? "";
        if (!string.IsNullOrWhiteSpace(_opt.Databases)) args = $"{args} --db \"{_opt.Databases}\"".Trim();
        var psi = new ProcessStartInfo(_opt.Path, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.LogDebug("engine stderr: {Line}", e.Data); };
        try { proc.Start(); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot start engine '{_opt.Path}': {ex.Message}", ex); }
        proc.BeginErrorReadLine();
        _proc = proc;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.StartupTimeoutMs);
        using var reg = timeout.Token.Register(() => Kill(proc));
        try
        {
            await WriteAsync("isready", timeout.Token);
            while (true)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new InvalidOperationException("Engine exited during startup.");
                if (line.Trim() == "readyok") break;
            }
        }
        catch
        {
            Kill(proc);
            _proc = null;
            throw;
        }
        _log.LogInformation("Engine worker started (pid {Pid})", proc.Id);
    }

    private async Task WriteAsync(string line, CancellationToken ct)
    {
        var w = _proc!.StandardInput;
        await w.WriteLineAsync(line.AsMemory(), ct);
        await w.FlushAsync(ct);
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteLineAsync("quit");
                await p.StandardInput.FlushAsync();
                if (!p.WaitForExit(500)) Kill(p);
            }
        }
        catch { Kill(p); }
        p.Dispose();
    }
}
