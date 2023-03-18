using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Chess;
using NodaTime;
using NodaTime.Text;
using NodaTime.TimeZones;
using RT.Serialization;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace FessBatchAnalyser;

static class Program
{
    public static AnalysisData Data;
    public static string GamesPath;
    public static string AnalysisDataPath;
    public static string AnalysisOutputPath;
    public static string StockfishPath;
    public static int[] Depths = new[] { 10 /*0.10 sec*/, 16 /*0.75sec*/, 20 /*3.4sec*/, 24 /*11.5sec*/, 28 /*30sec*/ };

    public static void Main()
    {
        //Tests.Test();

        var stockfish = new Stockfish();
        stockfish.Start();

        LoadData();
        //ImportNewGames(LoadPgnGames(DownloadChessComPgn("???", 2023, 03)).Where(g => g.TimeControl is "600" or "900+10"));
        //ImportNewGames(LoadPgnGames(File.ReadAllText(GamesPath)));
        SaveData();

        var lastSave = DateTime.UtcNow;
        var lastGen = DateTime.UtcNow;
        foreach (var depth in Depths) //.Concat(Enumerable.Range(Depths.Max() + 1, 50))) // infinite
        {
            var positions = Data.Games.Values.OrderBy(g => g.StartedAt).SelectMany(g => g.Positions).Where(p => !p.BestMoves.Any(m => m.Depth == depth)).ToList();
            if (positions.Count == 0)
                continue;
            if (depth > Depths.Min())
                Generate();
            foreach (var position in positions)
            {
                Console.Write($"Analysing {position.Game.Hash} #{position.FullMoveNum}/{(position.IsBlackMove ? "B" : "W")} to depth {depth}...");
                var start = DateTime.UtcNow;
                var results = stockfish.AnalyseFenToDepth(position.Fen, depth, 5); // depth 28: 5=33 sec, 2=17 sec, 1=11 sec
                Console.WriteLine($" {(DateTime.UtcNow - start).TotalSeconds:0.0} sec");
                Ut.Assert(results.Max() == results.First()); // sanity check (also tests comparator)
                position.BestMoves.AddRange(results);
                if (DateTime.UtcNow - lastSave > TimeSpan.FromSeconds(30))
                {
                    SaveData();
                    lastSave = DateTime.UtcNow;
                }
                if (DateTime.UtcNow - lastGen > TimeSpan.FromMinutes(5) && depth > Depths.Min())
                {
                    Generate();
                    lastGen = DateTime.UtcNow;
                }
            }
            SaveData();
            lastGen = DateTime.UtcNow;
        }
        Generate();
    }

    private static string DownloadChessComPgn(string username, int year, int month)
    {
        var url = $"https://api.chess.com/pub/player/{username}/games/{year}/{month:00}/pgn";
        var pgn = new HttpClient().GetStringAsync(url).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(GamesPath), $"chess.com-games-{username}-{year}-{month:00}.pgn"), pgn);
        return pgn;
    }

    private static void LoadData()
    {
        if (File.Exists(AnalysisDataPath))
            Data = ClassifyXml.DeserializeFile<AnalysisData>(AnalysisDataPath);
        else
            Data = new();
        foreach (var game in Data.Games.Values)
            foreach (var pos in game.Positions)
                pos.Game = game;
    }

    private static void SaveData()
    {
        ClassifyXml.SerializeToFile(Data, AnalysisDataPath);
    }

    private static void ImportNewGames(IEnumerable<AnalysisGame> games)
    {
        foreach (var game in games)
            if (!Data.Games.ContainsKey(game.Hash))
                Data.Games.Add(game.Hash, game);
            else
            {
                // update properties added after initial read
                foreach (var prop in game.Props)
                    Data.Games[game.Hash].Props[prop.Key] = prop.Value;
            }
    }

    private static List<AnalysisGame> LoadPgnGames(string pgn)
    {
        pgn = Regex.Replace(pgn, @"{\[%.*?\]}", "");
        var pgnlines = Regex.Split(pgn, @"\r?\n");
        var ln = 0;
        var games = new List<AnalysisGame>();
        while (ln < pgnlines.Length)
        {
            var props = new Dictionary<string, string>();
            while (pgnlines[ln].StartsWith("["))
            {
                var parsed = Regex.Match(pgnlines[ln], """^\[(?<name>\w+)\s+\"(?<val>.*)\"\]$""");
                props[parsed.Groups["name"].Value] = parsed.Groups["val"].Value;
                ln++;
            }
            Ut.Assert(pgnlines[ln] == "");
            while (pgnlines[ln] == "")
                ln++;
            var san = "";
            while (ln < pgnlines.Length && pgnlines[ln] != "")
            {
                san += " " + pgnlines[ln];
                ln++;
            }
            while (ln < pgnlines.Length && pgnlines[ln] == "")
                ln++;
            var moves = Regex.Split(san, @"\s+").Where(v => !v.EndsWith(".") && v != "").ToArray();
            Ut.Assert(moves[^1] == "0-1" || moves[^1] == "1-0" || moves[^1] == "1/2-1/2");
            moves = moves[..^1];

            var game = new AnalysisGame();
            game.Props = props;
            var board = new ChessBoard();
            foreach (var move in moves.Concat(new string[] { null }))
            {
                var pos = new AnalysisPos();
                pos.Fen = board.ToFen();
                pos.MoveTaken = move;
                pos.FullMoveNum = (board.MoveIndex + 1) / 2 + 1;
                pos.IsBlackMove = board.Turn == PieceColor.Black;
                pos.Game = game;
                game.Positions.Add(pos);
                if (move != null)
                    board.Move(move);
            }
            game.Hash = MD5.HashData(game.Positions.Select(p => p.Fen).JoinString(";").ToUtf8()).Base64UrlEncode();

            games.Add(game);
        }
        return games;
    }

    private static void Generate()
    {
        bool isMateEval(int value) => Math.Abs(value) >= 900_000;
        string strval(int value) => !isMateEval(value) ? $"{value / 100.0:0.0}" : $"M {100 - value / 1_000_000.0:0}";
        object divPosEval(int value, string fen) => Math.Abs(value) == 100_000_000 ? null : new DIV(strval(Math.Abs(value))) { class_ = "poseval ca" + (value > 0 ? " advW" : " advB") + (Math.Abs(value) > 550 ? " advHuge" : Math.Abs(value) > 300 ? " advBig" : Math.Abs(value) > 150 ? " advSmall" : ""), onclick = $"navigator.clipboard.writeText('{fen}');" };

        var html = new List<object>();
        var zone = BclDateTimeZone.ForSystemDefault();

        foreach (var game in Data.Games.Values.OrderBy(g => g.StartedAt))
        {
            var depth = game.Positions.Min(p => p.BestMoves.Max(m => m.Depth));
            html.Add(new H3($"Game {game.Hash} starting on ", new A($"{game.StartedAt.InZone(zone):dd MMM yyyy' at 'HH:mm:ss}") { href = game.Props["Link"] }));
            html.Add(new P($"Analysis depth: {depth}"));
            var moveshtml = new List<object>();
            var bestMove = game.Positions.Select(pos => pos.BestMoves.Where(m => m.Depth == depth).Max()).ToList();
            DIV plrdiv(string color) => new DIV(new SPAN(game.Props[color]), " elo ", game.Props[color + "Elo"]) { class_ = $"movehdrcell {color}" };
            moveshtml.Add(new DIV { class_ = "movehdr" }._(plrdiv("White"), plrdiv("Black")));
            var movediffsW = new List<int>();
            var movediffsB = new List<int>();
            for (int p = 0; p < game.Positions.Count; p++)
            {
                var pos = game.Positions[p];
                var blackAdj = pos.IsBlackMove ? -1 : 1;
                if (pos.MoveTaken != null)
                {
                    moveshtml.Add(divPosEval(bestMove[p].Eval * blackAdj, pos.Fen));
                    moveshtml.Add(new DIV(pos.MoveTaken) { class_ = "movetaken" });

                    var posValueAfterMove = -bestMove[p + 1].Eval;
                    var matchingBest = pos.BestMoves.Where(m => m.Depth == depth).FirstOrDefault(m => m.Move == pos.MoveTaken);
                    var movediff = matchingBest == null ? posValueAfterMove - bestMove[p].Eval : (matchingBest.Eval - bestMove[p].Eval);
                    (pos.IsBlackMove ? movediffsB : movediffsW).Add(movediff);
                    if (!isMateEval(bestMove[p].Eval) && !isMateEval(bestMove[p + 1].Eval))
                        moveshtml.Add(new DIV(strval(movediff)) { class_ = "movediff ra " + (movediff < -700 ? "blunder" : movediff < -450 ? "mistake" : movediff < -160 ? "inacc" : movediff < -90 ? "meh" : "") });
                    else
                        moveshtml.Add(new DIV("") { class_ = "movediff ra " + (false ? "blunder" : "") });

                    var moves = pos.BestMoves.Where(m => m.Depth == depth).OrderByDescending(m => m.Eval).ToList();
                    var takenRank = moves.IndexOf(m => m.Move == pos.MoveTaken) + 1;
                    var rankstr = takenRank switch { 0 => "?", 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{takenRank}th" };
                    moveshtml.Add(new DIV(rankstr) { class_ = "moverank" + (takenRank switch { 1 => " rank1", 0 => " rankN", _ => "" }) });

                    if (pos.IsBlackMove)
                        moveshtml.Add(divPosEval(posValueAfterMove * blackAdj, game.Positions[p + 1].Fen));
                }
                else
                {
                    if (!game.Props["Termination"].EndsWith("won by checkmate"))
                        moveshtml.Add(divPosEval(bestMove[p].Eval * blackAdj, pos.Fen));

                    if (game.Props["Termination"].EndsWith("won by resignation"))
                        moveshtml.Add(new DIV(game.Props["Result"] == (pos.IsBlackMove ? "1-0" : "0-1") ? "(resigned)" : "(opponent resigned)") { class_ = "moveend" });
                    else if (game.Props["Termination"].EndsWith("won on time"))
                        moveshtml.Add(new DIV("(out of time)") { class_ = "moveend" });
                    else if (game.Props["Termination"].EndsWith("drawn by stalemate"))
                        moveshtml.Add(new DIV("(stalemate)") { class_ = "moveend" });
                }
            }
            movediffsW = movediffsW.Where(d => d > -1500).Order().ToList();
            movediffsB = movediffsB.Where(d => d > -1500).Order().ToList();
            moveshtml.Add(new DIV { class_ = "movehdr" }._(
                new DIV($"Median move: {movediffsW[movediffsW.Count / 2] / 100.0:0.00}") { class_ = "movehdrcell" },
                new DIV($"Median move: {movediffsB[movediffsW.Count / 2] / 100.0:0.00}") { class_ = "movehdrcell" }
            ));
            html.Add(new DIV(moveshtml) { class_ = "movetable" });
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FessBatchAnalyser.Css.Analysis.css");
        var css = stream.ReadAllText();
        var thtml = new HTML(new HEAD(new META { charset = "utf-8" }, new STYLELiteral(css)), new BODY(html));
        File.WriteAllText(AnalysisOutputPath, thtml.ToString());
    }
}

class Stockfish
{
    private Process _process;
    private ConcurrentQueue<string> _recentStdOut = new();
    private ConcurrentQueue<string> _recentStdErr = new();
    private Task _readerStdOut, _readerStdErr;

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Program.StockfishPath,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _process = Process.Start(psi);
        _readerStdOut = StandardOutputReader();
        _readerStdErr = StandardErrorReader();

        Send("uci");
        WaitOutput("uciok");

        Send("setoption name Threads value 14");
        Send("setoption name Hash value 128");
        Send("setoption name Use NNUE value true");

        ClearOutput();
    }

    private async Task StandardOutputReader()
    {
        while (!_process.HasExited)
        {
            var line = await _process.StandardOutput.ReadLineAsync();
            _recentStdOut.Enqueue(line);
        }
    }

    private async Task StandardErrorReader()
    {
        while (!_process.HasExited)
        {
            var line = await _process.StandardError.ReadLineAsync();
            _recentStdErr.Enqueue(line);
        }
    }

    private void Send(string command)
    {
        _process.StandardInput.WriteLine(command);
    }

    private void ClearOutput()
    {
        _recentStdOut.Clear();
    }

    private void WaitOutput(string expected)
    {
        while (true)
        {
            if (!_recentStdOut.TryDequeue(out var line))
            {
                Thread.Sleep(50);
                continue;
            }
            if (line == expected)
                return;
        }
    }

    public IEnumerable<AnalysisMove> AnalyseFenToDepth(string fen, int depth, int multiPV)
    {
        Send("setoption name MultiPV value " + multiPV);
        //Send("ucinewgame");
        Send("position fen " + fen);
        Send("go depth " + depth);
        var resultLines = new Dictionary<int, string>();
        while (true)
        {
            if (!_recentStdOut.TryDequeue(out var line))
            {
                Thread.Sleep(50);
                continue;
            }
            if (line.StartsWith("bestmove "))
                break;
            if (line.StartsWith($"info depth {depth} "))
            {
                if (line.Contains("currmove"))
                    continue;
                int multipv = int.Parse(Regex.Match(line, @"\bmultipv\s+(?<mpv>\d+)(\s|$)").Groups["mpv"].Value);
                resultLines[multipv] = line;
            }
        }
        // for each result, parse score (cp or mate) and the moves - decoding from stockfish notation
        var results = new List<AnalysisMove>();
        foreach (var kvp in resultLines)
        {
            var m = Regex.Match(kvp.Value, @" score (?<st>cp|mate) (?<sv>-?\d+) .*? pv (?<pv>.*)$");
            if (!m.Success)
                throw new Exception();
            var score = int.Parse(m.Groups["sv"].Value);
            var rawmoves = m.Groups["pv"].Value.Split(" ");
            var moves = StockfishToSan(fen, rawmoves).ToList();
            var am = new AnalysisMove(score, m.Groups["st"].Value == "mate");
            am.Move = moves[0];
            am.PV = moves.Skip(1).JoinString(" ");
            am.Depth = depth;
            results.Add(am);
        }
        if (results.Count == 0)
        {
            // this is a checkmate or a stalemate position
            var board = ChessBoard.LoadFromFen(fen);
            if (board.EndGame.EndgameType == EndgameType.Checkmate)
                results.Add(new AnalysisMove(0, true) { Move = null, PV = "", Depth = depth }); // loss by checkmate
            else if (board.EndGame.EndgameType == EndgameType.Stalemate)
                results.Add(new AnalysisMove(0, false) { Move = null, PV = "", Depth = depth }); // draw by stalemate
            else
                throw new Exception();
        }
        return results;
    }

    public static IEnumerable<string> StockfishToSan(string fen, IEnumerable<string> rawmoves)
    {
        var board = ChessBoard.LoadFromFen(fen);
        foreach (var rawmove in rawmoves)
        {
            var srcPos = new Position(rawmove[..2]);
            var tgtPos = new Position(rawmove[2..4]);
            var move = new Move(srcPos, tgtPos);
            if (!board.IsValidMove(move)) // if this looks like just a check... this method actually populates most of the Move class!!!
                yield break; // at least one case of Stockfish returning an invalid move 30+ plies deep, we just cut that PV short
            yield return board.ParseToSan(move);
            board.Move(move);
        }
    }
}

class AnalysisData
{
    public Dictionary<string, AnalysisGame> Games = new();
}

class AnalysisGame
{
    public string Hash;
    public Dictionary<string, string> Props = new();
    public List<AnalysisPos> Positions = new();

    public override string ToString() => $"[{Hash[..6]}]: {Props["White"]} vs {Props["Black"]} at {StartedAt}";
    public string TimeControl => Props["TimeControl"];
    public Instant StartedAt => InstantPattern.General.Parse(Props["UTCDate"].Replace(".", "-") + "T" + Props["UTCTime"] + "Z").Value;
}

class AnalysisPos
{
    public string Fen;
    public string MoveTaken;
    public List<AnalysisMove> BestMoves = new();
    public int FullMoveNum;
    public bool IsBlackMove; // whose move is it
    [ClassifyIgnore]
    public AnalysisGame Game;
}

class AnalysisMove : IComparable<AnalysisMove>
{
    private int _score;
    private bool _isMate;
    private AnalysisMove() { }
    public AnalysisMove(int score, bool isMate)
    {
        _score = score;
        _isMate = isMate;
        if (_isMate) Ut.Assert(Math.Abs(_score) < 100);
    }

    public string Move;
    public string PV;
    public int Depth;
    public int Eval => !_isMate ? _score : _score == 0 ? -100_000_000 : (1_000_000 * Math.Sign(_score) * (100 - Math.Abs(_score))); // from the perspective of the player making the move.

    public int CompareTo(AnalysisMove other) => this.Eval.CompareTo(other.Eval);

    private string evalDesc => !_isMate ? $"{_score / 100.0:0.00}" : (_score == 0 ? "lost" : _score > 0 ? $"win in {_score}" : $"lose in {-_score}");
    public override string ToString() => $"{Depth}-ply: {Move} = {evalDesc}; pv = {PV}";
}
