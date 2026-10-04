using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Checkers.Core;

public sealed class PdnException : Exception
{
    public string Code { get; }
    public PdnException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// PDN position (FEN-style) parser/formatter and move notation helpers.
/// Accepted: <c>W:W21,22,K30:B1-12</c>, optionally wrapped as <c>[FEN "..."]</c>, or the keyword <c>start</c>.
/// Canonical form: <c>{B|W}:W{sorted squares, K prefix for kings}:B{...}</c>.
/// </summary>
public static class Pdn
{
    private static readonly Regex FenTag = new(@"^\[\s*FEN\s+""(?<f>[^""]*)""\s*\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Position Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new PdnException("invalid_pdn", "Position is empty.");
        string s = text.Trim();
        var tag = FenTag.Match(s);
        if (tag.Success) s = tag.Groups["f"].Value.Trim();
        if (s.EndsWith('.')) s = s[..^1];
        if (s.Equals("start", StringComparison.OrdinalIgnoreCase) || s.Equals("startpos", StringComparison.OrdinalIgnoreCase))
            return Position.Start;

        var parts = s.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) throw new PdnException("invalid_pdn", "Expected format 'W:W<squares>:B<squares>'.");

        bool blackToMove = parts[0].ToUpperInvariant() switch
        {
            "B" => true,
            "W" => false,
            _ => throw new PdnException("invalid_pdn", $"Side to move must be 'B' or 'W', got '{parts[0]}'.")
        };

        uint bm = 0, bk = 0, wm = 0, wk = 0;
        bool seenW = false, seenB = false;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 0) throw new PdnException("invalid_pdn", "Empty side section.");
            char side = char.ToUpperInvariant(part[0]);
            if (side != 'W' && side != 'B') throw new PdnException("invalid_pdn", $"Side section must start with 'W' or 'B': '{part}'.");
            if (side == 'W' ? seenW : seenB) throw new PdnException("invalid_pdn", $"Duplicate '{side}' section.");
            if (side == 'W') seenW = true; else seenB = true;

            uint men = 0, kings = 0;
            foreach (var tok in part[1..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ParseToken(tok, ref men, ref kings);
            if (side == 'W') { wm = men; wk = kings; } else { bm = men; bk = kings; }
        }

        var pos = new Position(bm, bk, wm, wk, blackToMove);
        Validate(pos);
        return pos;
    }

    private static void ParseToken(string tok, ref uint men, ref uint kings)
    {
        bool king = tok.StartsWith('K') || tok.StartsWith('k');
        string body = king ? tok[1..] : tok;
        int lo, hi;
        var range = body.Split('-');
        if (range.Length == 2 && !king)
        {
            lo = ParseSquare(range[0]);
            hi = ParseSquare(range[1]);
            if (lo > hi) throw new PdnException("invalid_pdn", $"Bad range '{tok}'.");
        }
        else if (range.Length == 1) lo = hi = ParseSquare(body);
        else throw new PdnException("invalid_pdn", $"Bad piece token '{tok}'.");

        for (int sq = lo; sq <= hi; sq++)
        {
            uint bit = 1u << (sq - 1);
            if (((men | kings) & bit) != 0) throw new PdnException("invalid_position", $"Square {sq} listed twice.");
            if (king) kings |= bit; else men |= bit;
        }
    }

    private static int ParseSquare(string t)
    {
        if (!int.TryParse(t, out int sq)) throw new PdnException("invalid_pdn", $"'{t}' is not a square number.");
        if (sq is < 1 or > 32) throw new PdnException("invalid_position", $"Square {sq} is outside 1..32.");
        return sq;
    }

    public static void Validate(in Position p)
    {
        if ((p.Black & p.White) != 0) throw new PdnException("invalid_position", "A square holds pieces of both colours.");
        if (BitOperations.PopCount(p.Black) > 12 || BitOperations.PopCount(p.White) > 12)
            throw new PdnException("invalid_position", "More than 12 pieces for one side.");
        if (p.Black == 0 || p.White == 0) throw new PdnException("invalid_position", "Both sides need at least one piece.");
        if ((p.BlackMen & 0xF0000000u) != 0) throw new PdnException("invalid_position", "Black man on its promotion row (29-32).");
        if ((p.WhiteMen & 0x0000000Fu) != 0) throw new PdnException("invalid_position", "White man on its promotion row (1-4).");
    }

    public static string Format(in Position p)
    {
        var sb = new StringBuilder();
        sb.Append(p.BlackToMove ? "B" : "W").Append(":W");
        AppendSide(sb, p.WhiteMen, p.WhiteKings);
        sb.Append(":B");
        AppendSide(sb, p.BlackMen, p.BlackKings);
        return sb.ToString();
    }

    private static void AppendSide(StringBuilder sb, uint men, uint kings)
    {
        bool first = true;
        for (uint bits = men | kings; bits != 0; bits &= bits - 1)
        {
            int sq = BitOperations.TrailingZeroCount(bits);
            if (!first) sb.Append(',');
            first = false;
            if ((kings & (1u << sq)) != 0) sb.Append('K');
            sb.Append(sq + 1);
        }
    }

    /// <summary>Move text with landing squares: <c>11-15</c>, <c>22x15</c>, <c>9x18x27</c>.</summary>
    public static string FormatMove(Move m)
    {
        var sb = new StringBuilder();
        sb.Append(m.From + 1);
        for (int i = 1; i < m.Len; i++) sb.Append(m.IsCapture ? 'x' : '-').Append(m.Square(i) + 1);
        return sb.ToString();
    }

    /// <summary>Finds the legal move matching text. Accepts '-', 'x' or ':' separators; for captures the
    /// intermediate squares may be omitted if from/to identify a single legal move.</summary>
    public static bool TryParseMove(in Position p, string? text, out Move move)
    {
        move = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sq = new List<int>();
        foreach (var tok in text.Trim().Split(new[] { '-', 'x', 'X', ':' }, StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(tok, out int n) || n is < 1 or > 32) return false;
            sq.Add(n - 1);
        }
        if (sq.Count < 2) return false;

        var buf = new Move[MoveGen.MaxMoves];
        int count = MoveGen.Generate(p, buf);
        for (int i = 0; i < count; i++)
        {
            if (buf[i].Len != sq.Count) continue;
            bool same = true;
            for (int k = 0; k < sq.Count && same; k++) same = buf[i].Square(k) == sq[k];
            if (same) { move = buf[i]; return true; }
        }
        if (sq.Count == 2)
        {
            int found = -1, matches = 0;
            for (int i = 0; i < count; i++)
                if (buf[i].From == sq[0] && buf[i].To == sq[1]) { found = i; matches++; }
            if (matches == 1) { move = buf[found]; return true; }
        }
        return false;
    }

    public static string PositionKey(in Position p) => p.Hash().ToString("x16");
}
