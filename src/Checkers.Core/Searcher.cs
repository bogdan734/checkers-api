using System.Diagnostics;
using System.Numerics;

namespace Checkers.Core;

public sealed record SearchOutcome(Move Best, IReadOnlyList<Move> Pv, int ScoreCp, int Depth, long Nodes, int ElapsedMs);

/// <summary>
/// Iterative-deepening negamax / alpha-beta with transposition table, capture extensions and
/// history/killer ordering. One instance per worker; not thread-safe.
/// This is the built-in stand-in for Chinook (see README).
/// </summary>
public sealed class Searcher
{
    public const int Win = 30000;
    private const int MaxPly = 96;
    private const int MaxExt = 10;
    private const int TtBits = 18;

    private struct TtEntry
    {
        public ulong Key;
        public Move Best;
        public int Score;
        public sbyte Depth;
        public byte Flag; // 1 exact, 2 lower, 3 upper
    }

    private readonly TtEntry[] _tt = new TtEntry[1 << TtBits];
    private readonly Move[][] _moves = new Move[MaxPly + 4][];
    private readonly int[][] _order = new int[MaxPly + 4][];
    private readonly Move[][] _pv = new Move[MaxPly + 4][];
    private readonly int[] _pvLen = new int[MaxPly + 4];
    private readonly ulong[] _path = new ulong[MaxPly + 4];
    private readonly int[,] _history = new int[32, 32];
    private readonly Move[,] _killers = new Move[MaxPly + 4, 2];

    private Stopwatch _sw = new();
    private long _nodes;
    private int _softMs;
    private bool _stopped;
    private bool _timeCheckEnabled;
    private CancellationToken _ct;

    public Searcher()
    {
        for (int i = 0; i < _moves.Length; i++)
        {
            _moves[i] = new Move[MoveGen.MaxMoves];
            _order[i] = new int[MoveGen.MaxMoves];
            _pv[i] = new Move[MaxPly + 4];
        }
    }

    /// <summary>Searches until <paramref name="maxDepth"/> or <paramref name="softMs"/> is reached.
    /// Throws <see cref="OperationCanceledException"/> if the token fires. Returns null if no legal move.</summary>
    public SearchOutcome? Search(Position root, int maxDepth, int softMs, CancellationToken ct = default)
    {
        var rootMoves = new Move[MoveGen.MaxMoves];
        int n = MoveGen.Generate(root, rootMoves);
        if (n == 0) return null;

        _sw = Stopwatch.StartNew();
        _nodes = 0;
        _softMs = Math.Max(1, softMs);
        _stopped = false;
        _timeCheckEnabled = false;
        _ct = ct;
        Array.Clear(_tt);
        Array.Clear(_history);
        Array.Clear(_killers);

        if (n == 1)
            return new SearchOutcome(rootMoves[0], new[] { rootMoves[0] }, Evaluator.Evaluate(root), 0, 0, 0);

        var best = rootMoves[0];
        var bestPv = new List<Move> { best };
        int bestScore = 0, doneDepth = 0;

        for (int depth = 1; depth <= Math.Max(1, maxDepth); depth++)
        {
            _timeCheckEnabled = depth > 1;
            _path[0] = root.Hash();
            int score = RootSearch(root, rootMoves, n, depth);
            if (_stopped) break;

            best = rootMoves[0];
            bestScore = score;
            doneDepth = depth;
            bestPv = _pv[0].Take(_pvLen[0]).ToList();
            if (bestPv.Count == 0) bestPv.Add(best);

            if (Math.Abs(score) >= Win - MaxPly) break;
            if (_sw.ElapsedMilliseconds * 10 >= (long)_softMs * 6) break; // next iteration would not finish in time
        }

        return new SearchOutcome(best, bestPv, bestScore, doneDepth, _nodes, (int)_sw.ElapsedMilliseconds);
    }

    private int RootSearch(Position root, Move[] moves, int n, int depth)
    {
        int alpha = -Win - 1, beta = Win + 1;
        int bestIdx = 0;
        _pvLen[0] = 0;
        for (int i = 0; i < n; i++)
        {
            var child = root.Apply(moves[i]);
            _pvLen[1] = 0;
            int score = -Negamax(child, depth - 1, -beta, -alpha, 1, 0);
            if (_stopped) return 0;
            if (score > alpha)
            {
                alpha = score;
                bestIdx = i;
                _pv[0][0] = moves[i];
                int len = _pvLen[1];
                Array.Copy(_pv[1], 0, _pv[0], 1, len);
                _pvLen[0] = len + 1;
            }
        }
        // Best move first for the next iteration.
        var b = moves[bestIdx];
        for (int i = bestIdx; i > 0; i--) moves[i] = moves[i - 1];
        moves[0] = b;
        return alpha;
    }

    private int Negamax(Position pos, int depth, int alpha, int beta, int ply, int ext)
    {
        _nodes++;
        if ((_nodes & 1023) == 0) CheckStop();
        if (_stopped) return 0;

        _pvLen[ply] = 0;
        ulong hash = pos.Hash();
        for (int i = ply - 1; i >= 0; i--)
            if (_path[i] == hash) return 0; // repetition = draw
        _path[ply] = hash;

        var buf = _moves[ply];
        int n = MoveGen.Generate(pos, buf);
        if (n == 0) return -(Win - ply);
        if (ply >= MaxPly - 1) return Evaluator.Evaluate(pos);

        bool forced = buf[0].IsCapture;
        if (depth <= 0)
        {
            if (!forced || ext >= MaxExt) return Evaluator.Evaluate(pos);
            depth = 1; ext++;
        }
        else if (n == 1 && ext < MaxExt)
        {
            depth++; ext++;
        }

        ref var e = ref _tt[(int)(hash & ((1 << TtBits) - 1))];
        Move ttMove = default;
        bool hasTt = false;
        if (e.Key == hash && e.Flag != 0)
        {
            ttMove = e.Best;
            hasTt = e.Best.Len != 0;
            if (e.Depth >= depth)
            {
                int s = FromTt(e.Score, ply);
                if (e.Flag == 1) return s;
                if (e.Flag == 2 && s >= beta) return s;
                if (e.Flag == 3 && s <= alpha) return s;
            }
        }

        var ord = _order[ply];
        for (int i = 0; i < n; i++)
        {
            var m = buf[i];
            int sc = _history[m.From, m.To];
            if (hasTt && m == ttMove) sc += 1_000_000;
            else if (m.IsCapture) sc += 10_000 + BitOperations.PopCount(m.Captured) * 100;
            else if (m == _killers[ply, 0]) sc += 5000;
            else if (m == _killers[ply, 1]) sc += 4000;
            ord[i] = sc;
        }

        int origAlpha = alpha;
        Move bestMove = default;
        int best = -Win - 1;
        for (int i = 0; i < n; i++)
        {
            int pick = i;
            for (int j = i + 1; j < n; j++) if (ord[j] > ord[pick]) pick = j;
            if (pick != i)
            {
                (buf[i], buf[pick]) = (buf[pick], buf[i]);
                (ord[i], ord[pick]) = (ord[pick], ord[i]);
            }

            var m = buf[i];
            int score = -Negamax(pos.Apply(m), depth - 1, -beta, -alpha, ply + 1, ext);
            if (_stopped) return 0;

            if (score > best)
            {
                best = score;
                bestMove = m;
                if (score > alpha)
                {
                    alpha = score;
                    _pv[ply][0] = m;
                    int len = _pvLen[ply + 1];
                    Array.Copy(_pv[ply + 1], 0, _pv[ply], 1, len);
                    _pvLen[ply] = len + 1;
                    if (alpha >= beta)
                    {
                        if (!m.IsCapture)
                        {
                            _killers[ply, 1] = _killers[ply, 0];
                            _killers[ply, 0] = m;
                            _history[m.From, m.To] += depth * depth;
                        }
                        break;
                    }
                }
            }
        }

        e.Key = hash;
        e.Best = bestMove;
        e.Depth = (sbyte)Math.Min(depth, 100);
        e.Score = ToTt(best, ply);
        e.Flag = best <= origAlpha ? (byte)3 : best >= beta ? (byte)2 : (byte)1;
        return best;
    }

    private void CheckStop()
    {
        _ct.ThrowIfCancellationRequested();
        if (_timeCheckEnabled && _sw.ElapsedMilliseconds >= _softMs) _stopped = true;
    }

    private static int ToTt(int score, int ply) =>
        score >= Win - MaxPly ? score + ply : score <= -Win + MaxPly ? score - ply : score;

    private static int FromTt(int score, int ply) =>
        score >= Win - MaxPly ? score - ply : score <= -Win + MaxPly ? score + ply : score;
}
