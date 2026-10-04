using System.Collections.Concurrent;
using System.Numerics;

namespace Checkers.Core;

public enum Wdl { Win, Draw, Loss }

public sealed record TbProbe(Wdl Result, int Plies, Move Best);

/// <summary>
/// Exact win/draw/loss + distance-to-end tablebase for positions with up to <see cref="MaxPieces"/> pieces,
/// generated at startup by round-synchronous retrograde-style value iteration. This is the built-in
/// stand-in for the Chinook 2-8 piece databases (3 pieces by default, 4 is possible but slow/large).
/// </summary>
public sealed class EndgameTablebase
{
    private static readonly ConcurrentDictionary<int, Lazy<EndgameTablebase>> Cache = new();

    public int MaxPieces { get; }
    public int Entries => _index.Count;
    private readonly Dictionary<ulong, int> _index = new();
    private byte[] _state = Array.Empty<byte>();   // 0 draw, 1 win, 2 loss
    private short[] _dist = Array.Empty<short>();  // plies to end

    private EndgameTablebase(int maxPieces) => MaxPieces = maxPieces;

    public static EndgameTablebase GetOrBuild(int maxPieces) =>
        Cache.GetOrAdd(Math.Clamp(maxPieces, 0, 4), n => new Lazy<EndgameTablebase>(() => Build(n))).Value;

    public static EndgameTablebase Build(int maxPieces)
    {
        var tb = new EndgameTablebase(maxPieces);
        if (maxPieces >= 2) tb.Generate();
        return tb;
    }

    public static ulong Key(in Position p)
    {
        ulong key = p.BlackToMove ? 1UL : 0UL;
        int shift = 1;
        for (uint b = p.Occupied; b != 0; b &= b - 1)
        {
            int sq = BitOperations.TrailingZeroCount(b);
            uint bit = 1u << sq;
            ulong type = (p.BlackMen & bit) != 0 ? 0UL : (p.BlackKings & bit) != 0 ? 1UL : (p.WhiteMen & bit) != 0 ? 2UL : 3UL;
            key |= (((ulong)sq << 2 | type) + 1) << shift;
            shift += 8;
        }
        return key;
    }

    public bool CanProbe(in Position p) =>
        MaxPieces >= 2 && p.PieceCount <= MaxPieces && p.Black != 0 && p.White != 0;

    public TbProbe? Probe(in Position p)
    {
        if (!CanProbe(p) || !_index.TryGetValue(Key(p), out int i)) return null;
        var buf = new Move[MoveGen.MaxMoves];
        int n = MoveGen.Generate(p, buf);
        if (n == 0) return null;

        byte st = _state[i];
        int bestIdx = -1, bestDist = st == 1 ? int.MaxValue : -1;
        for (int k = 0; k < n; k++)
        {
            var child = p.Apply(buf[k]);
            (byte cs, int cd) = ChildValue(child);
            switch (st)
            {
                case 1 when cs == 2 && cd < bestDist: bestDist = cd; bestIdx = k; break;
                case 2 when cs == 1 && cd > bestDist: bestDist = cd; bestIdx = k; break;
                case 0 when cs == 0 && bestIdx < 0: bestIdx = k; break;
            }
        }
        if (bestIdx < 0) return null;
        return new TbProbe(st == 1 ? Wdl.Win : st == 2 ? Wdl.Loss : Wdl.Draw, st == 0 ? 0 : _dist[i], buf[bestIdx]);
    }

    private (byte state, int dist) ChildValue(in Position child)
    {
        if (child.BlackToMove ? child.Black == 0 : child.White == 0) return (2, 0);
        return _index.TryGetValue(Key(child), out int j) ? (_state[j], _dist[j]) : ((byte)0, 0);
    }

    private void Generate()
    {
        var positions = new List<Position>();
        for (int k = 2; k <= MaxPieces; k++) Enumerate(positions, 0, k, 0, 0, 0, 0);
        int total = positions.Count;
        for (int i = 0; i < total; i++) _index[Key(positions[i])] = i;

        // Successor graph computed once: succ[start[i]..start[i+1]); -1 marks "opponent has no pieces" (a loss for the mover's opponent).
        var start = new int[total + 1];
        var succ = new List<int>(total * 6);
        var buf = new Move[MoveGen.MaxMoves];
        for (int i = 0; i < total; i++)
        {
            start[i] = succ.Count;
            var p = positions[i];
            int n = MoveGen.Generate(p, buf);
            for (int k = 0; k < n; k++)
            {
                var c = p.Apply(buf[k]);
                bool noPieces = c.BlackToMove ? c.Black == 0 : c.White == 0;
                succ.Add(noPieces ? -1 : _index[Key(c)]);
            }
        }
        start[total] = succ.Count;
        var edges = succ.ToArray();

        _state = new byte[total];
        _dist = new short[total];
        var active = new List<int>(total);
        for (int i = 0; i < total; i++)
        {
            if (start[i] == start[i + 1]) { _state[i] = 2; _dist[i] = 0; } // no moves: side to move loses
            else active.Add(i);
        }

        var updates = new List<(int idx, byte st, short d)>();
        while (true)
        {
            updates.Clear();
            foreach (int i in active)
            {
                int minLoss = int.MaxValue, maxWin = -1;
                bool allWins = true;
                for (int e = start[i]; e < start[i + 1]; e++)
                {
                    int j = edges[e];
                    byte cs = j < 0 ? (byte)2 : _state[j];
                    int cd = j < 0 ? 0 : _dist[j];
                    if (cs == 2) { if (cd < minLoss) minLoss = cd; allWins = false; }
                    else if (cs == 1) { if (cd > maxWin) maxWin = cd; }
                    else allWins = false;
                }
                if (minLoss != int.MaxValue) updates.Add((i, 1, (short)(minLoss + 1)));
                else if (allWins) updates.Add((i, 2, (short)(maxWin + 1)));
            }
            if (updates.Count == 0) break;
            foreach (var (idx, st, d) in updates) { _state[idx] = st; _dist[idx] = d; }
            active.RemoveAll(i => _state[i] != 0);
        }
    }

    private static void Enumerate(List<Position> acc, int startSq, int remaining, uint bm, uint bk, uint wm, uint wk)
    {
        if (remaining == 0)
        {
            if ((bm | bk) == 0 || (wm | wk) == 0) return;
            acc.Add(new Position(bm, bk, wm, wk, true));
            acc.Add(new Position(bm, bk, wm, wk, false));
            return;
        }
        for (int sq = startSq; sq < 32; sq++)
        {
            uint bit = 1u << sq;
            if (sq < 28) Enumerate(acc, sq + 1, remaining - 1, bm | bit, bk, wm, wk);
            Enumerate(acc, sq + 1, remaining - 1, bm, bk | bit, wm, wk);
            if (sq >= 4) Enumerate(acc, sq + 1, remaining - 1, bm, bk, wm | bit, wk);
            Enumerate(acc, sq + 1, remaining - 1, bm, bk, wm, wk | bit);
        }
    }
}
