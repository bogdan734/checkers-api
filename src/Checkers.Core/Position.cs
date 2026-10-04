using System.Numerics;

namespace Checkers.Core;

/// <summary>
/// English/American checkers (8x8, 32 dark squares, PDN numbering 1..32 from Black's side).
/// Bit i of every board is square i+1. Black starts on 1-12, moves "down" (towards 32), moves first.
/// </summary>
public readonly record struct Position(uint BlackMen, uint BlackKings, uint WhiteMen, uint WhiteKings, bool BlackToMove)
{
    public static readonly Position Start = new(0x00000FFFu, 0, 0xFFF00000u, 0, true);

    public uint Black => BlackMen | BlackKings;
    public uint White => WhiteMen | WhiteKings;
    public uint Occupied => Black | White;
    public int PieceCount => BitOperations.PopCount(Occupied);

    public Position Apply(Move m)
    {
        uint from = 1u << m.From, to = 1u << m.To;
        uint bm = BlackMen, bk = BlackKings, wm = WhiteMen, wk = WhiteKings;
        if (BlackToMove)
        {
            if ((bm & from) != 0)
            {
                bm &= ~from;
                if (m.To >= 28) bk |= to; else bm |= to;
            }
            else bk = (bk & ~from) | to;
            wm &= ~m.Captured;
            wk &= ~m.Captured;
        }
        else
        {
            if ((wm & from) != 0)
            {
                wm &= ~from;
                if (m.To < 4) wk |= to; else wm |= to;
            }
            else wk = (wk & ~from) | to;
            bm &= ~m.Captured;
            bk &= ~m.Captured;
        }
        return new Position(bm, bk, wm, wk, !BlackToMove);
    }

    /// <summary>Well-mixed 64-bit hash of the whole position including side to move.</summary>
    public ulong Hash()
    {
        ulong a = BlackMen | ((ulong)BlackKings << 32);
        ulong b = WhiteMen | ((ulong)WhiteKings << 32);
        ulong h = Mix(a) ^ Mix(b + 0x9E3779B97F4A7C15UL);
        return BlackToMove ? h ^ 0xA5A5A5A5DEADBEEFUL : h;
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

/// <summary>A (possibly multi-jump) move. Path holds 0-based squares, 5 bits each, index 0 = origin.</summary>
public readonly struct Move : IEquatable<Move>
{
    public readonly ulong Path;
    public readonly uint Captured;
    public readonly byte Len;

    public Move(ulong path, int len, uint captured)
    {
        Path = path;
        Len = (byte)len;
        Captured = captured;
    }

    public int From => (int)(Path & 31);
    public int To => (int)((Path >> (5 * (Len - 1))) & 31);
    public bool IsCapture => Captured != 0;
    public int Square(int i) => (int)((Path >> (5 * i)) & 31);

    public bool Equals(Move other) => Path == other.Path && Len == other.Len && Captured == other.Captured;
    public override bool Equals(object? obj) => obj is Move m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(Path, Len, Captured);
    public static bool operator ==(Move a, Move b) => a.Equals(b);
    public static bool operator !=(Move a, Move b) => !a.Equals(b);
}

public static class Board
{
    /// <summary>Neighbour square per direction: 0=up-left, 1=up-right, 2=down-left, 3=down-right; -1 = off board.</summary>
    public static readonly int[][] Neighbors = BuildNeighbors();
    public static readonly int[] Row = Enumerable.Range(0, 32).Select(s => s / 4).ToArray();
    public static readonly int[] Col = Enumerable.Range(0, 32).Select(s => (s / 4) % 2 == 0 ? (s % 4) * 2 + 1 : (s % 4) * 2).ToArray();

    private static int[][] BuildNeighbors()
    {
        int[] dr = { -1, -1, 1, 1 };
        int[] dc = { -1, 1, -1, 1 };
        var res = new int[32][];
        for (int s = 0; s < 32; s++)
        {
            res[s] = new int[4];
            int r = s / 4;
            int c = r % 2 == 0 ? (s % 4) * 2 + 1 : (s % 4) * 2;
            for (int d = 0; d < 4; d++)
            {
                int nr = r + dr[d], nc = c + dc[d];
                res[s][d] = nr is < 0 or > 7 || nc is < 0 or > 7 ? -1 : nr * 4 + nc / 2;
            }
        }
        return res;
    }
}

public static class MoveGen
{
    public const int MaxMoves = 128;

    /// <summary>Generates legal moves (captures are mandatory) into <paramref name="buf"/>; returns the count.</summary>
    public static int Generate(in Position p, Move[] buf)
    {
        bool black = p.BlackToMove;
        uint own = black ? p.Black : p.White;
        uint enemy = black ? p.White : p.Black;
        uint kings = black ? p.BlackKings : p.WhiteKings;
        uint occ = p.Occupied;
        int n = 0;

        for (uint bits = own; bits != 0; bits &= bits - 1)
        {
            int sq = BitOperations.TrailingZeroCount(bits);
            bool king = (kings & (1u << sq)) != 0;
            n = Jumps(buf, n, occ & ~(1u << sq), enemy, king, black, sq, sq, (ulong)(uint)sq, 1, 0);
        }
        if (n > 0) return n;

        for (uint bits = own; bits != 0; bits &= bits - 1)
        {
            int sq = BitOperations.TrailingZeroCount(bits);
            bool king = (kings & (1u << sq)) != 0;
            int d0 = king ? 0 : black ? 2 : 0;
            int d1 = king ? 3 : black ? 3 : 1;
            for (int d = d0; d <= d1; d++)
            {
                int to = Board.Neighbors[sq][d];
                if (to < 0 || (occ & (1u << to)) != 0) continue;
                buf[n++] = new Move((ulong)(uint)sq | ((ulong)(uint)to << 5), 2, 0);
            }
        }
        return n;
    }

    public static bool HasMoves(in Position p)
    {
        var buf = new Move[MaxMoves];
        return Generate(p, buf) > 0;
    }

    // occEff: occupancy with the moving piece's origin removed (captured pieces stay until the move ends).
    private static int Jumps(Move[] buf, int n, uint occEff, uint enemy, bool king, bool black,
        int from, int cur, ulong path, int len, uint captured)
    {
        int d0 = king ? 0 : black ? 2 : 0;
        int d1 = king ? 3 : black ? 3 : 1;
        for (int d = d0; d <= d1; d++)
        {
            int mid = Board.Neighbors[cur][d];
            if (mid < 0) continue;
            uint mb = 1u << mid;
            if ((enemy & mb) == 0 || (captured & mb) != 0) continue;
            int land = Board.Neighbors[mid][d];
            if (land < 0 || (occEff & (1u << land)) != 0) continue;

            ulong np = path | ((ulong)(uint)land << (5 * len));
            uint nc = captured | mb;
            bool crowns = !king && (black ? land >= 28 : land < 4);
            if (crowns)
            {
                buf[n++] = new Move(np, len + 1, nc);
                continue;
            }
            int before = n;
            n = Jumps(buf, n, occEff, enemy, king, black, from, land, np, len + 1, nc);
            if (n == before) buf[n++] = new Move(np, len + 1, nc);
        }
        return n;
    }
}
