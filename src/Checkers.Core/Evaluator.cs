using System.Numerics;

namespace Checkers.Core;

/// <summary>Static evaluation in centipawns from the side-to-move perspective.</summary>
public static class Evaluator
{
    private const int Man = 100, King = 160;

    private static readonly int[] Center = BuildCenter();

    private static int[] BuildCenter()
    {
        var t = new int[32];
        for (int s = 0; s < 32; s++)
        {
            int r = Board.Row[s], c = Board.Col[s];
            double dist = Math.Abs(r - 3.5) + Math.Abs(c - 3.5);   // 1..7
            t[s] = (int)Math.Round((7 - dist) * 1.5);
            if (c == 0 || c == 7) t[s] -= 2;                       // edge pieces are less mobile
        }
        return t;
    }

    public static int Evaluate(in Position p)
    {
        int score = 0;
        for (uint b = p.BlackMen; b != 0; b &= b - 1)
        {
            int s = BitOperations.TrailingZeroCount(b), r = Board.Row[s];
            score += Man + r * 4 + Center[s] + (r == 0 ? 6 : 0);
        }
        for (uint b = p.WhiteMen; b != 0; b &= b - 1)
        {
            int s = BitOperations.TrailingZeroCount(b), r = 7 - Board.Row[s];
            score -= Man + r * 4 + Center[s] + (r == 0 ? 6 : 0);
        }
        for (uint b = p.BlackKings; b != 0; b &= b - 1) score += King + Center[BitOperations.TrailingZeroCount(b)];
        for (uint b = p.WhiteKings; b != 0; b &= b - 1) score -= King + Center[BitOperations.TrailingZeroCount(b)];

        // Endgame: the side ahead should hunt the enemy, so reward proximity.
        int total = p.PieceCount;
        if (total <= 8 && Math.Abs(score) >= 80)
        {
            int dist = MinDistance(p.Black, p.White);
            score += Math.Sign(score) * (8 - dist) * 3;
        }
        return p.BlackToMove ? score : -score;
    }

    private static int MinDistance(uint a, uint b)
    {
        int best = 14;
        for (uint x = a; x != 0; x &= x - 1)
        {
            int sx = BitOperations.TrailingZeroCount(x);
            for (uint y = b; y != 0; y &= y - 1)
            {
                int sy = BitOperations.TrailingZeroCount(y);
                int d = Math.Max(Math.Abs(Board.Row[sx] - Board.Row[sy]), Math.Abs(Board.Col[sx] - Board.Col[sy]));
                if (d < best) best = d;
            }
        }
        return best;
    }
}
