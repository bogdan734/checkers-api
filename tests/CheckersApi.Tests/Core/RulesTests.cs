using Checkers.Core;

namespace CheckersApi.Tests.Core;

public class RulesTests
{
    private static long Perft(Position p, int depth)
    {
        var buf = new Move[MoveGen.MaxMoves];
        int n = MoveGen.Generate(p, buf);
        if (depth == 1) return n;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += Perft(p.Apply(buf[i]), depth - 1);
        return sum;
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 49)]
    [InlineData(3, 302)]
    [InlineData(4, 1469)]
    [InlineData(5, 7361)]
    [InlineData(6, 36768)]
    public void Perft_from_start_matches_known_english_draughts_counts(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position.Start, depth));

    [Fact]
    public void Pdn_roundtrip_and_ranges()
    {
        var p = Pdn.Parse("W:W21-32:B1-12");
        Assert.Equal(Position.Start with { BlackToMove = false }, p);
        Assert.Equal("W:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12", Pdn.Format(p));
        Assert.Equal(Position.Start, Pdn.Parse("[FEN \"B:W21-32:B1-12\"]"));
        Assert.Equal(Position.Start, Pdn.Parse("start"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("X:W1:B2")]
    [InlineData("W:W33:B1")]
    [InlineData("W:W0:B1")]
    [InlineData("W:W5:B5")]
    [InlineData("W:W5,5:B1")]
    [InlineData("W:W2:B1")]      // white man on promotion row
    [InlineData("W:W20:B30")]    // black man on promotion row
    [InlineData("W:W:B1")]       // no white pieces
    public void Invalid_pdn_throws(string pdn) => Assert.Throws<PdnException>(() => Pdn.Parse(pdn));

    [Fact]
    public void Captures_are_mandatory_and_multi_jump_is_found()
    {
        // Black man 9 jumps white 14 -> 18 and white 23 -> 27 (double jump)
        var p = Pdn.Parse("B:W14,23,32:B9");
        var buf = new Move[MoveGen.MaxMoves];
        int n = MoveGen.Generate(p, buf);
        Assert.Equal(1, n);
        Assert.Equal("9x18x27", Pdn.FormatMove(buf[0]));
        Assert.Equal(2, System.Numerics.BitOperations.PopCount(buf[0].Captured));
    }

    [Fact]
    public void Man_stops_on_crowning_row_and_becomes_king()
    {
        var p = Pdn.Parse("B:W26,27:B22");   // 22x31 crowns, could continue as king over 27? must stop
        Assert.True(Pdn.TryParseMove(p, "22x31", out var m));
        var after = p.Apply(m);
        Assert.NotEqual(0u, after.BlackKings);
        Assert.Equal(0u, after.BlackMen);
    }

    [Fact]
    public void Move_text_parsing_accepts_separators_and_rejects_illegal()
    {
        var p = Position.Start;
        Assert.True(Pdn.TryParseMove(p, "11-15", out _));
        Assert.True(Pdn.TryParseMove(p, "11x15", out _));
        Assert.False(Pdn.TryParseMove(p, "11-17", out _));
        Assert.False(Pdn.TryParseMove(p, "nonsense", out _));
    }
}
