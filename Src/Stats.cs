using System.Reflection;
using System.Text;
using NodaTime;
using RT.TagSoup;
using RT.Util.ExtensionMethods;

namespace FessBatchAnalyser;

static class Stats
{
    class AugGame
    {
        public AnalysisGame Game;
        public int MyElo;
        public int OpponentElo;
        public double WinVal;
        public AugGame(AnalysisGame game)
        {
            Game = game;
            MyElo = game.PlayerElo(Program.Settings.ChessComUsername);
            OpponentElo = game.OpponentElo(Program.Settings.ChessComUsername);
            WinVal = game.WinVal(Program.Settings.ChessComUsername);
        }
    }

    public static void Generate(IEnumerable<AnalysisGame> games)
    {
        var html = new List<object>();
        var byTimeControl = games.Select(g => new AugGame(g)).GroupBy(x => x.Game.TimeControl).OrderByDescending(g => g.Count());

        html.Add(new P($"Generated on {DateTime.Now}"));
        html.Add(new P($"Last game on {games.Max(g => g.StartedAt.ToDateTimeUtc().ToLocalTime())}"));

        html.Add(new H1("Winrate implied ELO"));
        foreach (var group in byTimeControl)
            html.Add(genWinrateImpliedElo(group.Key, group));

        html.Add(new H1("Win rates vs ELO"));
        foreach (var group in byTimeControl)
        {
            html.Add(new H2(group.Key));
            html.Add(genWinRateVsELO(group));
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FessBatchAnalyser.Css.Analysis.css");
        var css = stream.ReadAllText();
        var thtml = new HTML(new HEAD(new META { charset = "utf-8" }, new STYLELiteral(css)), new BODY(html));
        File.WriteAllText(Path.Combine(Program.DataPath, "stats.html"), thtml.ToString());
    }

    private static object genWinrateImpliedElo(string header, IEnumerable<AugGame> games)
    {
        static object makeRow(string title, IEnumerable<AugGame> gs)
        {
            for (int tgtWinrate = 55; tgtWinrate <= 90; tgtWinrate += 5)
            {
                var winrateImpliedElo = eloRangeFromWinrate(gs, tgtWinrate);
                if (winrateImpliedElo != null)
                    return new P($"{title} ({tgtWinrate}%–{100 - tgtWinrate}%): ", new B($"{winrateImpliedElo.Value.lowerElo} – {winrateImpliedElo.Value.upperElo}"));
            }
            return null;
        }

        var results = new List<object>();
        results.Add(makeRow("All time", games));
        for (var month = games.Min(g => g.Game.StartedAt).ToDateTimeUtc(); month <= DateTime.UtcNow; month = month.AddMonths(1))
            results.Add(makeRow($"{month:MMM yyyy}", games.Where(g => g.Game.StartedAt.ToDateTimeUtc().Month == month.Month && g.Game.StartedAt.ToDateTimeUtc().Year == month.Year)));
        results.Add(ImpliedEloOverTime(games));
        if (results.Count(r => r != null) > 0)
            results.Insert(0, new H2(header));
        return results;
    }

    private static object genWinRateVsELO(IEnumerable<AugGame> games)
    {
        var byElo = games.OrderBy(g => g.OpponentElo).ToList();
        // determine histogram bucket size so that 80% of games land within 11 buckets (one centered on median plus 5 on both sides)
        var medianElo = byElo[byElo.Count / 2].OpponentElo;
        int bucketSize = 4;
        while (true)
        {
            if (games.Count(g => g.OpponentElo >= medianElo - bucketSize * 5.5 && g.OpponentElo <= medianElo + bucketSize * 5.5) >= byElo.Count * 0.8)
                break;
            bucketSize++;
        }
        var result = new List<object>();
        for (int bucket = (int)(medianElo - bucketSize * 5.5); bucket < (int)(medianElo + bucketSize * 5.5); bucket += bucketSize)
        {
            var bucketEnd = bucket + bucketSize;
            var gs = games.Where(g => g.OpponentElo >= bucket && g.OpponentElo < bucketEnd).ToList();
            result.Add(new P($"{bucket}–{bucketEnd}: {(gs.Count == 0 ? -1 : gs.Average(g => g.WinVal) * 100):0.0}% ({gs.Count} games)"));
        }
        return result;
    }

    private static object ImpliedEloOverTime(IEnumerable<AugGame> games)
    {
        var gs = games.OrderBy(g => g.Game.StartedAt).ToList();
        bool showImpliedElo = true;
        again:;
        for (int tgtWinrate = 55; tgtWinrate <= 75; tgtWinrate += 5)
            for (int window = 10; window < gs.Count / 2; window++)
            {
                var results = new List<(int at, int? lowerElo, int? upperElo, int trueElo)>();
                for (int i = 0; i < gs.Count; i++)
                {
                    if (showImpliedElo && i - window / 2 >= 0 && i - window / 2 + window <= gs.Count)
                    {
                        var impliedElo = eloRangeFromWinrate(gs.Skip(i - window / 2).Take(window), tgtWinrate);
                        if (impliedElo == null)
                            goto bad;
                        results.Add((i, impliedElo.Value.lowerElo, impliedElo.Value.upperElo, gs[i].MyElo));
                    }
                    else
                        results.Add((i, null, null, gs[i].MyElo));
                }
                var minX = 0;
                var maxX = results.Count - 1;
                var maxY = (int)Math.Ceiling(results.Max(r => r.upperElo ?? 0) * 6.0 / 5.0 / 50.0) * 50;
                if (!showImpliedElo)
                    maxY = (int)Math.Ceiling(results.Max(r => r.trueElo) * 6.0 / 5.0 / 50.0) * 50;
                var svg = new StringBuilder();
                svg.Append($"<svg width='500px' height='300px' viewBox='0 0 {maxX} {maxY}' preserveAspectRatio='none' style='border: 1px solid #999; margin: 10px; background: #222;' xmlns='http://www.w3.org/2000/svg'><g>");
                //svg.Append($"<rect fill='none' stroke='#921' x='0' y='0' width='{maxX}' height='{maxY}' vector-effect='non-scaling-stroke' />");
                svg.Append($"<polyline fill='none' stroke='#ff0' points='{results.Where(r => r.lowerElo != null).Select(r => $"{r.at},{maxY - r.lowerElo}").JoinString(" ")}' vector-effect='non-scaling-stroke' />");
                svg.Append($"<polyline fill='none' stroke='#ff0' points='{results.Where(r => r.upperElo != null).Select(r => $"{r.at},{maxY - r.upperElo}").JoinString(" ")}' vector-effect='non-scaling-stroke' />");
                svg.Append($"<polyline fill='none' stroke='#f31' stroke-opacity='0.5' points='{results.Select(r => $"{r.at},{maxY - r.trueElo}").JoinString(" ")}' vector-effect='non-scaling-stroke' />");
                for (int y = 100; y < maxY; y += 100)
                    svg.Append($"<polyline fill='none' stroke='#888' points='0 {maxY - y} {maxX} {maxY - y}' vector-effect='non-scaling-stroke' stroke-dasharray='3 3' />");
                return new RawTag(svg.ToString());
                bad:;
            }
        if (!showImpliedElo)
            return null; // not enough points for the smallest window size
        showImpliedElo = false;
        goto again;
    }

    private static (int lowerElo, int upperElo)? eloRangeFromWinrate(IEnumerable<AugGame> games, int tgtWinrate)
    {
        var orderedGames = games.OrderBy(g => g.OpponentElo).ToList();
        if (orderedGames.Count < 7)
            return null;
        int lowerElo = -1;
        for (int i = orderedGames.Count / 5; i < orderedGames.Count; i++)
            if (orderedGames.Take(i).Average(g => g.WinVal) * 100 < tgtWinrate)
            {
                lowerElo = orderedGames[i].OpponentElo;
                break;
            }

        orderedGames.Reverse();
        int upperElo = -1;
        for (int i = orderedGames.Count / 5; i < orderedGames.Count; i++)
            if (orderedGames.Take(i).Average(g => g.WinVal) * 100 > (100 - tgtWinrate))
            {
                upperElo = orderedGames[i].OpponentElo;
                break;
            }

        if (lowerElo != -1 && upperElo != -1 && lowerElo <= upperElo)
            return (lowerElo, upperElo);
        else
            return null;
    }
}
