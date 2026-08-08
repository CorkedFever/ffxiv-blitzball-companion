using System.Text.RegularExpressions;
using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace BlitzballTracker.Tests;

/// <summary>
/// How much of a real recorded match the parser actually resolves.
///
/// This is a measuring instrument as much as a test. Real chat is written by people —
/// every player narrates their own action and no two do it the same way — so full
/// coverage is not the goal and never will be. The point is to notice when a change
/// moves the number, and to have somewhere to look when a match tracks badly.
///
/// Recordings stay out of the repository; set <c>BLITZ_LOGS</c> to run this.
/// </summary>
public class RealLogCoverageTests(ITestOutputHelper output)
{
    private static readonly Regex Roll =
        new(@"Random!\s+(.+?)\s+rolls\s+a", RegexOptions.IgnoreCase);

    private static readonly Regex Action =
        new(@"\[\s*(TACKLE|BLOCK|MOVES?|DIVE|PASS|SHOOT|GUARD|TAUNT|RALLY|SHOVE|SURVEY|RUSH)",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// Every name that rolled dice has to survive world-stripping intact.
    ///
    /// This is the regression for the bug that cost a whole match: a crossworld name
    /// arrives welded to its world, and until that was split the parser recognised
    /// nobody. In a cross-world league that is not a partial failure, it is total.
    /// </summary>
    [RecordedMatchFact]
    public void EveryNameThatRolledIsCleanOfItsWorld()
    {
        foreach (var file in Recordings())
        {
            var lines = File.ReadAllLines(file);

            var rolled = lines
                .Select(l => Roll.Match(l))
                .Where(m => m.Success)
                .Select(m => PlayerNames.StripWorld(m.Groups[1].Value))
                .Distinct()
                .ToList();

            var stillFused = rolled.Where(n => Worlds.Split(n).World is not null).ToList();

            output.WriteLine(
                $"{Path.GetFileName(file)}: {lines.Length} lines, " +
                $"{lines.Count(Action.IsMatch)} action lines, {rolled.Count} players rolled.");

            foreach (var name in stillFused)
                output.WriteLine($"    still carrying a world: {name}");

            Assert.Empty(stillFused);
        }
    }

    /// <summary>
    /// Replay each recording against a roster built from whoever actually rolled, and
    /// report what the parser made of it.
    ///
    /// Roles here are filler — the log cannot tell us who played where, which is the
    /// whole reason a roster has to be entered by hand. So this asserts only that the
    /// people who played were recognised as playing, and prints the rest to read.
    /// </summary>
    [RecordedMatchFact]
    public void ReplayingARecordingResolvesThePeopleWhoPlayed()
    {
        foreach (var file in Recordings())
        {
            var lines = File.ReadAllLines(file);

            var rolled = lines
                .Select(l => Roll.Match(l))
                .Where(m => m.Success)
                .Select(m => PlayerNames.StripWorld(m.Groups[1].Value))
                .Distinct()
                .ToList();

            if (rolled.Count == 0) continue;

            var game = new BlitzGame();
            game.ApplyRoster(RosterOf(rolled));

            var parser = new ChatParser(game);

            LogReplay.Replay(
                lines.Select(LogReplay.ParseLine).Where(l => l is not null).Select(l => l!).ToList(),
                parser);

            var recognised = rolled.Count(n => game.Players.ContainsKey(n));

            output.WriteLine(
                $"{Path.GetFileName(file)}: {recognised}/{rolled.Count} players recognised, " +
                $"{game.PlayByPlay.Count} play-by-play lines, " +
                $"{parser.UnmatchedNames.Count} unmatched names, " +
                $"phase feed: {game.HasPhaseFeed}, final {game.Score.Home}:{game.Score.Away}.");

            foreach (var (name, hits) in parser.UnmatchedNames.OrderByDescending(kv => kv.Value).Take(8))
                output.WriteLine($"    unmatched: {name} ({hits})");

            Assert.Equal(rolled.Count, recognised);
        }
    }

    private static IEnumerable<string> Recordings() =>
        Directory.GetFiles(Fixtures.RecordingsDirectory!, "*.txt").OrderBy(f => f);

    /// <summary>
    /// Six a side, in whatever order the names arrived. Roles are filler: a log does not
    /// record them, so nothing here may assert on them.
    /// </summary>
    private static Roster RosterOf(List<string> names)
    {
        PlayerRole[] roles =
        [
            PlayerRole.Goalkeeper, PlayerRole.LeftDefender, PlayerRole.RightDefender,
            PlayerRole.Midfield, PlayerRole.LeftForward, PlayerRole.RightForward,
        ];

        var roster = new Roster { HomeTeam = "HOME", AwayTeam = "AWAY" };

        for (var i = 0; i < names.Count; i++)
        {
            roster.Entries.Add(new RosterEntry
            {
                Name = names[i],
                Team = i < 6 ? "HOME" : "AWAY",
                Role = roles[i % 6],
            });
        }

        return roster;
    }
}
