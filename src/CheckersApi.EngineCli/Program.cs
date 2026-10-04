using System.Globalization;
using Checkers.Core;

// Reference line-protocol engine process (the same protocol ChinookEngineAdapter speaks).
// It wraps the built-in Searcher/EndgameTablebase and doubles as the template for a Chinook/KingsRow shim:
//   isready                                  -> readyok
//   position <pdn>                           -> (no output)
//   go depth <d> time <ms> tb <0|1>          -> info depth <d> nodes <n> time <ms> score cp <x>|wdl <WIN|DRAW|LOSS> tb <0|1> pv <m1> <m2> ...
//                                               bestmove <move>      (or: bestmove none)
//   quit                                     -> exits
// Options: --tb <maxPieces>  size of the built-in tablebase (default 3, 0 disables).

int tbPieces = 3;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--tb" && int.TryParse(args[i + 1], out var n)) tbPieces = n;

var tablebase = EndgameTablebase.GetOrBuild(tbPieces);
var searcher = new Searcher();
Position? position = null;
var inv = CultureInfo.InvariantCulture;

string? line;
while ((line = Console.ReadLine()) is not null)
{
    line = line.Trim();
    if (line.Length == 0) continue;
    var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
    switch (parts[0])
    {
        case "isready":
            Console.WriteLine("readyok");
            break;
        case "position":
            try { position = Pdn.Parse(parts.Length > 1 ? parts[1] : null); }
            catch (PdnException e) { position = null; Console.WriteLine($"error {e.Message}"); }
            break;
        case "go":
            Go(parts.Length > 1 ? parts[1] : "");
            break;
        case "quit":
            return;
        default:
            Console.WriteLine($"error unknown command '{parts[0]}'");
            break;
    }
}

void Go(string spec)
{
    if (position is null) { Console.WriteLine("error no position"); Console.WriteLine("bestmove none"); return; }
    var kv = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    int depth = 12, time = 300, tb = 1;
    for (int i = 0; i + 1 < kv.Length; i += 2)
    {
        int.TryParse(kv[i + 1], NumberStyles.Integer, inv, out int v);
        switch (kv[i]) { case "depth": depth = v; break; case "time": time = v; break; case "tb": tb = v; break; }
    }

    var pos = position.Value;
    if (tb == 1 && tablebase.Probe(pos) is { } hit)
    {
        string wdl = hit.Result.ToString().ToUpperInvariant();
        Console.WriteLine($"info depth 0 nodes 1 time 0 score wdl {wdl} tb 1 pv {Pdn.FormatMove(hit.Best)}");
        Console.WriteLine($"bestmove {Pdn.FormatMove(hit.Best)}");
        return;
    }
    var o = searcher.Search(pos, depth, time);
    if (o is null) { Console.WriteLine("bestmove none"); return; }
    string pv = string.Join(' ', o.Pv.Select(Pdn.FormatMove));
    Console.WriteLine($"info depth {o.Depth} nodes {o.Nodes} time {o.ElapsedMs} score cp {o.ScoreCp} tb 0 pv {pv}");
    Console.WriteLine($"bestmove {Pdn.FormatMove(o.Best)}");
}
