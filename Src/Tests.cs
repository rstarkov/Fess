using RT.Util;

namespace FessBatchAnalyser;

static class Tests
{
    public static void Test()
    {
        var stockfish = new Stockfish();
        stockfish.Start();

        AnalysisMove getBest(IEnumerable<AnalysisMove> moves)
        {
            var max = moves.Max();
            Ut.Assert(max == moves.First());
            return max;
        }

        var res1 = getBest(stockfish.AnalyseFenToDepth("8/k7/6R1/7R/8/8/8/K7 w - - 0 1", 16, 5)); // white to win by checkmate in 3-ply; mate-score=2
        var res2 = getBest(stockfish.AnalyseFenToDepth("8/k6R/6R1/8/8/8/8/K7 b - - 1 1", 16, 5)); // white to win by checkmate in 2-ply; mate-score=-1 - we have a move; opponents next move is checkmate
        var res3 = getBest(stockfish.AnalyseFenToDepth("k7/7R/6R1/8/8/8/8/K7 w - - 2 2", 16, 5)); // white to win by checkmate in 1-ply; mate-score=1 - there is a checkmating move
        var res4 = getBest(stockfish.AnalyseFenToDepth("k5R1/7R/8/8/8/8/8/K7 b - - 3 2", 16, 5)); // white won by checkmate
        var res5 = getBest(stockfish.AnalyseFenToDepth("k7/5R2/8/8/1R6/8/8/K7 b - - 0 1", 16, 5)); // stalemate

        Ut.Assert(res4.Eval < -res3.Eval);
        Ut.Assert(-res3.Eval == res2.Eval);
        Ut.Assert(res2.Eval < -res1.Eval);
        Ut.Assert(res4.Eval < res5.Eval);
        Ut.Assert(res5.Eval == 0);
    }
}
